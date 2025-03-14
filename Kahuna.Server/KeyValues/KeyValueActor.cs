
using Google.Protobuf;
using Kahuna.Locks;
using Nixie;
using Kommander;
using Kommander.Time;

using Kahuna.Persistence;
using Kahuna.Replication;
using Kahuna.Shared.KeyValue;
using Kahuna.Replication.Protos;
using Kommander.Data;

namespace Kahuna.KeyValues;

/// <summary>
/// Each of these actors functions as a worker, accepting requests to perform operations on key/value pairs.
/// The actor maintains an in-memory cache and if a key is not found, it attempts to retrieve it from disk.
/// Operations with Linearizable consistency persist all modifications to disk.
/// </summary>
public sealed class KeyValueActor : IActorStruct<KeyValueRequest, KeyValueResponse>
{
    private readonly IActorContextStruct<KeyValueActor, KeyValueRequest, KeyValueResponse> actorContext;

    private readonly IActorRef<BackgroundWriterActor, BackgroundWriteRequest> backgroundWriter;

    private readonly IPersistence persistence;

    private readonly IRaft raft;

    private readonly Dictionary<string, KeyValueContext> keyValues = new();
    
    private readonly HashSet<string> keysToEvict = [];

    private readonly ILogger<IKahuna> logger;

    private uint operations;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="actorContext"></param>
    /// <param name="raft"></param>
    /// <param name="logger"></param>
    public KeyValueActor(
        IActorContextStruct<KeyValueActor, KeyValueRequest, KeyValueResponse> actorContext,
        IActorRef<BackgroundWriterActor, BackgroundWriteRequest> backgroundWriter,
        IPersistence persistence,
        IRaft raft,
        ILogger<IKahuna> logger
    )
    {
        this.actorContext = actorContext;
        this.backgroundWriter = backgroundWriter;
        this.persistence = persistence;
        this.raft = raft;
        this.logger = logger;
    }

    /// <summary>
    /// Main entry point for the actor.
    /// Receives messages one at a time to prevent concurrency issues
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public async Task<KeyValueResponse> Receive(KeyValueRequest message)
    {
        try
        {
            logger.LogDebug(
                "KeyValueActor Message: {Actor} {Type} {Key} {Value} {ExpiresMs} {Flags} {Consistency}",
                actorContext.Self.Runner.Name,
                message.Type,
                message.Key,
                message.Value?.Length,
                message.ExpiresMs,
                message.Flags,
                message.Consistency
            );

            if ((operations++) % 1000 == 0)
                await Collect();

            return message.Type switch
            {
                KeyValueRequestType.TrySet => await TrySet(message),
                KeyValueRequestType.TryDelete => await TryDelete(message),
                KeyValueRequestType.TryExtend => await TryExtend(message),
                KeyValueRequestType.TryGet => await TryGet(message),
                _ => new(KeyValueResponseType.Errored)
            };
        }
        catch (Exception ex)
        {
            logger.LogError("Error processing message: {Type} {Message}\n{Stacktrace}", ex.GetType().Name, ex.Message, ex.StackTrace);
        }

        return new(KeyValueResponseType.Errored);
    }
    
    /// <summary>
    /// Tries to set a key value pair based on the specified flags
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private async Task<KeyValueResponse> TrySet(KeyValueRequest message)
    {
        bool exists = true;
        HLCTimestamp currentTime = await raft.HybridLogicalClock.SendOrLocalEvent();

        if (!keyValues.TryGetValue(message.Key, out KeyValueContext? context))
        {
            exists = false;
            KeyValueContext? newContext = null;

            /// Try to retrieve KeyValue context from persistence
            if (message.Consistency == KeyValueConsistency.Linearizable)
            {
                newContext = await persistence.GetKeyValue(message.Key);
                if (newContext is not null)
                {
                    if (newContext.State != KeyValueState.Deleted)
                        exists = true;
                    
                    if (newContext.Expires - currentTime < TimeSpan.Zero)
                        exists = false;
                }
            }

            newContext ??= new() { Revision = -1 };
            
            context = newContext;

            keyValues.Add(message.Key, newContext);
        }
        else
        {
            if (context.State == KeyValueState.Deleted)
                exists = false;
        }

        switch (message.Flags)
        {
            case KeyValueFlags.SetIfExists when !exists:
            case KeyValueFlags.SetIfNotExists when exists:
            case KeyValueFlags.SetIfEqualToValue when exists && !((ReadOnlySpan<byte>)context.Value).SequenceEqual(message.CompareValue):
            case KeyValueFlags.SetIfEqualToRevision when exists && context.Revision != message.CompareRevision:
                return new(KeyValueResponseType.NotSet, context.Revision);

            case KeyValueFlags.None:
            case KeyValueFlags.Set:
            default:
                break;
        }
        
        KeyValueProposal proposal = new(
            message.Key,
            message.Value,
            context.Revision + 1,
            currentTime + message.ExpiresMs,
            currentTime,
            KeyValueState.Set
        );

        if (message.Consistency == KeyValueConsistency.Linearizable)
        {
            bool success = await PersistAndReplicateKeyValueMessage(message.Type, proposal, currentTime);
            if (!success)
                return new(KeyValueResponseType.Errored);
        }
        
        context.Value = proposal.Value;
        context.Revision++;
        context.Expires = currentTime + message.ExpiresMs;
        context.LastUsed = currentTime;
        context.State = KeyValueState.Set;

        return new(KeyValueResponseType.Set, context.Revision);
    }
    
    /// <summary>
    /// Set a timeout on key. After the timeout has expired, the key will automatically be deleted
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private async Task<KeyValueResponse> TryExtend(KeyValueRequest message)
    {
        KeyValueContext? context = await GetKeyValueContext(message.Key, message.Consistency);
        if (context is null)
            return new(KeyValueResponseType.DoesNotExist);
        
        if (context.State == KeyValueState.Deleted)
            return new(KeyValueResponseType.DoesNotExist, context.Revision);
        
        HLCTimestamp currentTime = await raft.HybridLogicalClock.SendOrLocalEvent();
        
        if (context.Expires - currentTime < TimeSpan.Zero)
            return new(KeyValueResponseType.DoesNotExist, context.Revision);
        
        KeyValueProposal proposal = new(
            message.Key,
            context.Value,
            context.Revision,
            currentTime + message.ExpiresMs,
            currentTime,
            context.State
        );
        
        if (message.Consistency == KeyValueConsistency.Linearizable)
        {
            bool success = await PersistAndReplicateKeyValueMessage(message.Type, proposal, currentTime);
            if (!success)
                return new(KeyValueResponseType.Errored);
        }
        
        context.Expires = proposal.Expires;
        context.LastUsed = proposal.LastUsed;

        return new(KeyValueResponseType.Extended, context.Revision);
    }
    
    /// <summary>
    /// Looks for a KeyValue on the resource and tries to delete it
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private async Task<KeyValueResponse> TryDelete(KeyValueRequest message)
    {
        KeyValueContext? context = await GetKeyValueContext(message.Key, message.Consistency);
        if (context is null)
            return new(KeyValueResponseType.DoesNotExist);
        
        if (context.State == KeyValueState.Deleted)
            return new(KeyValueResponseType.DoesNotExist, context.Revision);
        
        HLCTimestamp currentTime = await raft.HybridLogicalClock.SendOrLocalEvent();
        
        KeyValueProposal proposal = new(
            message.Key,
            null,
            context.Revision,
            context.Expires,
            currentTime,
            KeyValueState.Deleted
        );

        if (message.Consistency == KeyValueConsistency.Linearizable)
        {
            bool success = await PersistAndReplicateKeyValueMessage(message.Type, proposal, currentTime);
            if (!success)
                return new(KeyValueResponseType.Errored);
        }
        
        context.Value = proposal.Value;
        context.LastUsed = proposal.LastUsed;
        context.State = proposal.State;
        
        return new(KeyValueResponseType.Deleted, context.Revision);
    }

    /// <summary>
    /// Gets a value by the specified key
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private async Task<KeyValueResponse> TryGet(KeyValueRequest message)
    {
        KeyValueContext? context = await GetKeyValueContext(message.Key, message.Consistency);
        if (context is null || context.State == KeyValueState.Deleted)
            return new(KeyValueResponseType.DoesNotExist, new ReadOnlyKeyValueContext(null, context?.Revision ?? 0, HLCTimestamp.Zero));

        HLCTimestamp currentTime = await raft.HybridLogicalClock.SendOrLocalEvent();

        if (context.Expires - currentTime < TimeSpan.Zero)
            return new(KeyValueResponseType.DoesNotExist, new ReadOnlyKeyValueContext(null, context?.Revision ?? 0, HLCTimestamp.Zero));
        
        context.LastUsed = currentTime;

        ReadOnlyKeyValueContext readOnlyKeyValueContext = new(context.Value, context.Revision, context.Expires);

        return new(KeyValueResponseType.Get, readOnlyKeyValueContext);
    }

    private async ValueTask<KeyValueContext?> GetKeyValueContext(string resource, KeyValueConsistency? consistency)
    {
        if (!keyValues.TryGetValue(resource, out KeyValueContext? context))
        {
            if (consistency == KeyValueConsistency.Linearizable)
            {
                context = await persistence.GetKeyValue(resource);
                if (context is not null)
                {
                    context.LastUsed = await raft.HybridLogicalClock.SendOrLocalEvent();
                    keyValues.Add(resource, context);
                    return context;
                }
                
                return null;
            }
            
            return null;    
        }
        
        return context;
    }

    /// <summary>
    /// Persists and replicates KeyValue message to the Raft cluster
    /// </summary>
    /// <param name="type"></param>
    /// <param name="proposal"></param>
    /// <param name="currentTime"></param>
    /// <returns></returns>
    private async Task<bool> PersistAndReplicateKeyValueMessage(KeyValueRequestType type, KeyValueProposal proposal, HLCTimestamp currentTime)
    {
        if (!raft.Joined)
            return true;

        int partitionId = raft.GetPartitionKey(proposal.Key);

        KeyValueMessage kvm = new()
        {
            Type = (int)type,
            Key = proposal.Key,
            Revision = proposal.Revision,
            ExpireLogical = proposal.Expires.L,
            ExpireCounter = proposal.Expires.C,
            TimeLogical = currentTime.L,
            TimeCounter = currentTime.C,
            Consistency = (int)KeyValueConsistency.LinearizableReplication
        };
        
        if (proposal.Value is not null)
            kvm.Value = ByteString.CopyFrom(proposal.Value);

        (bool success, RaftOperationStatus status, long _) = await raft.ReplicateLogs(
            partitionId,
            ReplicationTypes.KeyValues,
            ReplicationSerializer.Serialize(kvm)
        );

        if (!success)
        {
            logger.LogWarning("Failed to replicate key/value {Key} Partition={Partition} Status={Status}", proposal.Key, partitionId, status);
            
            return false;
        }

        backgroundWriter.Send(new(
            BackgroundWriteType.QueueStoreLock,
            partitionId,
            proposal.Key,
            proposal.Value,
            proposal.Revision,
            proposal.Expires,
            (int)KeyValueConsistency.Linearizable,
            (int)proposal.State
        ));

        return success;
    }

    private async ValueTask Collect()
    {
        if (keyValues.Count < 2000)
            return;
        
        int number = 0;
        TimeSpan range = TimeSpan.FromMinutes(30);
        HLCTimestamp currentTime = await raft.HybridLogicalClock.SendOrLocalEvent();

        foreach (KeyValuePair<string, KeyValueContext> key in keyValues)
        {
            if ((currentTime - key.Value.LastUsed) < range)
                continue;
            
            keysToEvict.Add(key.Key);
            number++;
            
            if (number > 100)
                break;
        }

        foreach (string key in keysToEvict)
        {
            keyValues.Remove(key);
            
            Console.WriteLine("Removed {0}", key);
        }
        
        keysToEvict.Clear();
    }
}
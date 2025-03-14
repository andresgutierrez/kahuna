
/**
 * This file is part of Kahuna
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flurl.Http;
using Kahuna.Client.Communication;
using Kahuna.Shared.KeyValue;
using Kahuna.Shared.Locks;
using Microsoft.Extensions.Logging;

namespace Kahuna.Client;

/// <summary>
/// Client for the Kahuna service
/// </summary>
public class KahunaClient
{
    private readonly string[] urls;

    private readonly ILogger<KahunaClient>? logger;

    private readonly IKahunaCommunication communication;

    private int currentServer;
    
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="url"></param>
    /// <param name="logger"></param>
    /// <param name="communication"></param>
    public KahunaClient(string url, ILogger<KahunaClient>? logger = null, IKahunaCommunication? communication = null)
    {
        this.urls = [url];
        this.logger = logger;
        this.communication = (communication ?? new GrpcCommunication(logger));
    }
    
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="urls"></param>
    /// <param name="logger"></param>
    /// <param name="communication"></param>
    public KahunaClient(string[] urls, ILogger<KahunaClient>? logger = null, IKahunaCommunication? communication = null)
    {
        this.urls = urls;
        this.logger = logger;
        this.communication = (communication ?? new GrpcCommunication(logger));
    }
    
    private async Task<(KahunaLockAcquireResult, long)> TryAcquireLock(string resource, byte[] owner, TimeSpan expiryTime, LockConsistency consistency)
    {
        return await communication.TryAcquireLock(GetRoundRobinUrl(), resource, owner, (int)expiryTime.TotalMilliseconds, consistency).ConfigureAwait(false);
    }
    
    private async Task<(KahunaLockAcquireResult, byte[]?, LockConsistency, long)> PeriodicallyTryAcquireLock(
        string resource, 
        TimeSpan expiryTime, 
        TimeSpan wait, 
        TimeSpan retry,
        LockConsistency consistency
    )
    {
        try
        {
            byte[] owner = Guid.NewGuid().ToByteArray();
            
            Stopwatch stopWatch = Stopwatch.StartNew();
            
            long fencingToken = -1;
            KahunaLockAcquireResult result = KahunaLockAcquireResult.Error;

            while (stopWatch.Elapsed < wait)
            {
                (result, fencingToken) = await TryAcquireLock(resource, owner, expiryTime, consistency).ConfigureAwait(false);

                if (result != KahunaLockAcquireResult.Success)
                {
                    await Task.Delay((int)Math.Max(100, retry.TotalMilliseconds + Random.Shared.Next(-50, 50))).ConfigureAwait(false);
                    continue;
                }

                return (result, owner, consistency, fencingToken);
            }

            return (result, null, consistency, fencingToken);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error locking lock instance: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Tries to acquire a lock on a resource with a given expiry time
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    private async Task<(KahunaLockAcquireResult, byte[]?, LockConsistency, long)> SingleTimeTryAcquireLock(string resource, TimeSpan expiryTime, LockConsistency consistency)
    {
        try
        {
            byte[] owner = Guid.NewGuid().ToByteArray();

            (KahunaLockAcquireResult result, long fencingToken) = await TryAcquireLock(resource, owner, expiryTime, consistency).ConfigureAwait(false);

            return (result, owner, consistency, fencingToken);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error locking lock instance: {Message}", ex.Message);

            return (KahunaLockAcquireResult.Error, null, consistency, -1);
        }
    }
    
    /// <summary>
    /// Gets or creates a lock on a resource with a given expiry time.
    /// If the lock can't be acquired immediately, it will try to acquire it periodically 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiryTime"></param>
    /// <param name="waitTime"></param>
    /// <param name="retryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<KahunaLock> GetOrCreateLock(string resource, int expiryTime = 30000, int waitTime = 0, int retryTime = 0, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        TimeSpan expiry = TimeSpan.FromMilliseconds(expiryTime);
        TimeSpan wait = TimeSpan.FromMilliseconds(waitTime);
        TimeSpan retry = TimeSpan.FromMilliseconds(retryTime);

        return await GetOrCreateLock(resource, expiry, wait, retry, consistency).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or creates a lock on a resource with a given expiry time.
    /// If the lock can't be acquired immediately, it will try to acquire it periodically 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiry"></param>
    /// <param name="wait"></param>
    /// <param name="retry"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    /// <exception cref="KahunaException"></exception>
    public async Task<KahunaLock> GetOrCreateLock(string resource, TimeSpan expiry, TimeSpan wait, TimeSpan retry, LockConsistency consistency = LockConsistency.Ephemeral)
    {        
        if (wait == TimeSpan.Zero)
            return new(this, resource, await SingleTimeTryAcquireLock(resource, expiry, consistency).ConfigureAwait(false));
        
        if (retry == TimeSpan.Zero)
            throw new KahunaException("Retry cannot be zero", LockResponseType.InvalidInput);
        
        return new(this, resource, await PeriodicallyTryAcquireLock(resource, expiry, wait, retry, consistency).ConfigureAwait(false));
    }

    /// <summary>
    /// Gets or creates a lock on a resource with a given expiry time.
    /// Gives up immediately if the lock is not available 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiry"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<KahunaLock> GetOrCreateLock(string resource, TimeSpan expiry, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        return new(this, resource, await SingleTimeTryAcquireLock(resource, expiry, consistency).ConfigureAwait(false));
    }
    
    /// <summary>
    /// Tried to extend the lock by the specified duration
    /// Returns true if the lock was successfully extended, false otherwise 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="duration"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryExtend(string resource, byte[] owner, TimeSpan duration, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtend(GetRoundRobinUrl(), resource, owner, (int)duration.TotalMilliseconds, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error extending lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Tried to extend the lock by the specified duration
    /// Returns true if the lock was successfully extended, false otherwise 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="durationMs"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryExtend(string resource, byte[] owner, int durationMs, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtend(GetRoundRobinUrl(), resource, owner, durationMs, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error extending lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Tried to extend the lock by the specified duration
    /// Returns true if the lock was successfully extended, false otherwise 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="durationMs"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryExtend(string resource, string owner, int durationMs, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtend(GetRoundRobinUrl(), resource, Encoding.UTF8.GetBytes(owner), durationMs, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error extending lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Tried to extend the lock by the specified duration
    /// Returns true if the lock was successfully extended, false otherwise 
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="durationMs"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryExtend(string resource, string owner, TimeSpan duration, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtend(GetRoundRobinUrl(), resource, Encoding.UTF8.GetBytes(owner), (int)duration.TotalMilliseconds, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error extending lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Unlocks a lock on a resource if the owner is the current lock owner
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<bool> Unlock(string resource, byte[] owner, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryUnlock(GetRoundRobinUrl(), resource, owner, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error unlocking lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Unlocks a lock on a resource if the owner is the current lock owner
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="owner"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<bool> Unlock(string resource, string owner, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryUnlock(GetRoundRobinUrl(), resource, Encoding.UTF8.GetBytes(owner), consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogInformation("Error unlocking lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Obtains information about an existing lock
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<KahunaLockInfo?> GetLockInfo(string resource, LockConsistency consistency = LockConsistency.Ephemeral)
    {
        try
        {
            return await communication.Get(GetRoundRobinUrl(), resource, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error getting lock instance: {Message}", ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Set key to hold the string value. If key already holds a value, it is overwritten
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> SetKeyValue(string key, byte[]? value, int expiryTime = 30000, KeyValueFlags flags = KeyValueFlags.Set, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TrySetKeyValue(GetRoundRobinUrl(), key, value, expiryTime, flags, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Set key to hold the string value. If key already holds a value, it is overwritten
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> SetKeyValue(string key, string value, int expiryTime = 30000, KeyValueFlags flags = KeyValueFlags.Set, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TrySetKeyValue(GetRoundRobinUrl(), key, Encoding.UTF8.GetBytes(value), expiryTime, flags, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Set key to hold the string value. If key already holds a value, it is overwritten
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> SetKeyValue(string key, string value, TimeSpan expiryTime, KeyValueFlags flags = KeyValueFlags.Set, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TrySetKeyValue(GetRoundRobinUrl(), key, Encoding.UTF8.GetBytes(value), (int)expiryTime.TotalMilliseconds, flags, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Compare Value and Set (CVAS) operation. Sets the value of a key if the current value is equal to the expected value
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="compareValue"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryCompareValueAndSetKeyValue(string key, byte[] value, byte[] compareValue, int expiryTime = 30000, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryCompareValueAndSetKeyValue(GetRoundRobinUrl(), key, value, compareValue, expiryTime, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value (cvas): {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Compare Value and Set (CVAS) operation. Sets the value of a key if the current value is equal to the expected value
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="compareValue"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryCompareValueAndSetKeyValue(string key, string value, string compareValue, int expiryTime = 30000, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryCompareValueAndSetKeyValue(GetRoundRobinUrl(), key, Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(compareValue), expiryTime, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value (cvas): {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Compare Revision and Set (CRAS) operation. Sets the value of a key if the current revision is equal to the expected value
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="compareRevision"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryCompareRevisionAndSetKeyValue(string key, byte[]? value, long compareRevision, int expiryTime = 30000, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryCompareRevisionAndSetKeyValue(GetRoundRobinUrl(), key, value, compareRevision, expiryTime, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value (cras): {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Compare Revision and Set (CRAS) operation. Sets the value of a key if the current revision is equal to the expected value
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="compareRevision"></param>
    /// <param name="expiryTime"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> TryCompareRevisionAndSetKeyValue(string key, string value, long compareRevision, int expiryTime = 30000, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryCompareRevisionAndSetKeyValue(GetRoundRobinUrl(), key, Encoding.UTF8.GetBytes(value), compareRevision, expiryTime, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value (cras): {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Set key to hold the string value. If key already holds a value, it is overwritten.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="expiry"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> SetKeyValue(string key, byte[]? value, TimeSpan expiry, KeyValueFlags flags = KeyValueFlags.Set, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        return await SetKeyValue(key, value, (int)expiry.TotalMilliseconds, flags, consistency).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Get the value of key. If the key does not exist null is returned
    /// </summary>
    /// <param name="key"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(byte[]?, long)> GetKeyValue(string key, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryGetKeyValue(GetRoundRobinUrl(), key, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error setting key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Removes the specified key. A key is ignored if it does not exist.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> DeleteKeyValue(string key, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryDeleteKeyValue(GetRoundRobinUrl(), key, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error deleting key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Set a timeout on key. After the timeout has expired, the key will automatically be deleted
    /// </summary>
    /// <param name="key"></param>
    /// <param name="expiresMs"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> ExtendKeyValue(string key, int expiresMs, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtendKeyValue(GetRoundRobinUrl(), key, expiresMs, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error extending key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Set a timeout on key. After the timeout has expired, the key will automatically be deleted
    /// </summary>
    /// <param name="key"></param>
    /// <param name="expiresMs"></param>
    /// <param name="consistency"></param>
    /// <returns></returns>
    public async Task<(bool, long)> ExtendKeyValue(string key, TimeSpan expiresMs, KeyValueConsistency consistency = KeyValueConsistency.Ephemeral)
    {
        try
        {
            return await communication.TryExtendKeyValue(GetRoundRobinUrl(), key, (int)expiresMs.TotalMilliseconds, consistency).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError("Error extending key/value: {Message}", ex.Message);

            throw;
        }
    }
    
    /// <summary>
    /// Chooses the next server in the list of servers in a round-robin fashion
    /// </summary>
    /// <returns></returns>
    private string GetRoundRobinUrl()
    {
        int serverPointer = Interlocked.Increment(ref currentServer);
        return urls[serverPointer % urls.Length];
    }
}


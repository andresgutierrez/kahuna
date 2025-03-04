using Kahuna.Locks;

namespace Kahuna.Persistence;

public interface IPersistence
{
    public Task StoreLock(string resource, string owner, long expiresPhysical, uint expiresCounter, long fencingToken, long consistency, LockState state);

    public Task<LockContext?> GetLock(string resource);
}
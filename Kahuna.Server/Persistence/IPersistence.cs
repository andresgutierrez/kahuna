
using Kahuna.Locks;
using Kahuna.KeyValues;

namespace Kahuna.Persistence;

public interface IPersistence
{
    public Task<bool> StoreLock(string resource, byte[]? owner, long expiresPhysical, uint expiresCounter, long fencingToken, int consistency, int state);

    public Task<bool> StoreKeyValue(string key, byte[]? value, long expiresPhysical, uint expiresCounter, long revision, int consistency, int state);

    public Task<LockContext?> GetLock(string resource);
    
    public Task<KeyValueContext?> GetKeyValue(string keyName);
}
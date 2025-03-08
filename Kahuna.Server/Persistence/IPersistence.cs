
using Kahuna.Locks;
using Kahuna.KeyValues;

namespace Kahuna.Persistence;

public interface IPersistence
{
    public Task StoreLock(string resource, string owner, long expiresPhysical, uint expiresCounter, long fencingToken, int consistency, int state);

    public Task StoreKeyValue(string key, string value, long expiresPhysical, uint expiresCounter, int consistency, int state);

    public Task<LockContext?> GetLock(string resource);
    
    public Task<KeyValueContext?> GetKeyValue(string keyName);
}
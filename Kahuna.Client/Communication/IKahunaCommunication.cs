
/**
 * This file is part of Kahuna
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using Kahuna.Shared.KeyValue;
using Kahuna.Shared.Locks;

namespace Kahuna.Client.Communication;

public interface IKahunaCommunication
{
     Task<(KahunaLockAcquireResult, long)> TryAcquireLock(string url, string resource, byte[] owner, int expiryTime, LockConsistency consistency);

     Task<bool> TryUnlock(string url, string resource, byte[] owner, LockConsistency consistency);

     Task<(bool, long)> TryExtend(string url, string resource, byte[] owner, int expiryTime, LockConsistency consistency);

     Task<KahunaLockInfo?> Get(string url, string resource, LockConsistency consistency);

     Task<(bool, long)> TrySetKeyValue(string url, string key, byte[]? value, int expiryTime, KeyValueFlags flags, KeyValueConsistency consistency);

     Task<(bool, long)> TryCompareValueAndSetKeyValue(string url, string key, byte[]? value, byte[]? compareValue, int expiryTime, KeyValueConsistency consistency);

     Task<(bool, long)> TryCompareRevisionAndSetKeyValue(string url, string key, byte[]? value, long compareRevision, int expiryTime, KeyValueConsistency consistency);

     Task<(byte[]?, long)> TryGetKeyValue(string url, string key, KeyValueConsistency consistency);

     Task<(bool, long)> TryDeleteKeyValue(string url, string key, KeyValueConsistency consistency);

     Task<(bool, long)> TryExtendKeyValue(string url, string key, int expiresMs, KeyValueConsistency consistency);
}
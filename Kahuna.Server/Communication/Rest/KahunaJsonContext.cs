
using System.Text.Json.Serialization;

namespace Kahuna.Communication.Rest;

[JsonSerializable(typeof(ExternGetLockRequest))]
[JsonSerializable(typeof(ExternGetLockResponse))]
[JsonSerializable(typeof(ExternLockRequest))]
[JsonSerializable(typeof(ExternGetLockResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class KahunaJsonContext : JsonSerializerContext
{

}
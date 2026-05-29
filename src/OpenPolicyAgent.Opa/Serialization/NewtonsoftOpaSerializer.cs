using Newtonsoft.Json;
using OpenPolicyAgent.Opa.Internal;

namespace OpenPolicyAgent.Opa.Serialization;

/// <summary>
/// Default <see cref="IOpaSerializer"/> implementation, backed by Newtonsoft.Json.
/// Accepts a <see cref="JsonSerializerSettings"/> instance to customize behavior
/// (null-handling, converters, contract resolvers, etc.).
/// </summary>
/// <remarks>
/// When constructed with no settings, the serializer ships with two converters
/// pre-installed (<see cref="OpaAnyDictionaryConverter"/> and
/// <see cref="OpaFlexibleObjectConverter"/>) that normalize JSON objects and
/// arrays into nested <c>Dictionary&lt;string, object&gt;</c> / <c>List&lt;object&gt;</c>
/// structures. This lets callers assert against plain CLR collection literals
/// without accounting for <c>JObject</c>/<c>JArray</c> nesting. Users who supply
/// their own <see cref="JsonSerializerSettings"/> are expected to register the
/// converters themselves if they want this behavior.
/// </remarks>
public sealed class NewtonsoftOpaSerializer : IOpaSerializer
{
    private readonly JsonSerializerSettings _settings;

    public NewtonsoftOpaSerializer(JsonSerializerSettings? settings = null)
    {
        _settings = settings ?? CreateDefaultSettings();
    }

    /// <summary>The settings instance this serializer is using (either user-supplied or the SDK defaults).</summary>
    public JsonSerializerSettings Settings => _settings;

    public string Serialize(object? value) => JsonConvert.SerializeObject(value, _settings);

    public T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, _settings);

    internal static JsonSerializerSettings CreateDefaultSettings()
    {
        var s = new JsonSerializerSettings();
        s.Converters.Add(new OpaAnyDictionaryConverter());
        s.Converters.Add(new OpaFlexibleObjectConverter());
        return s;
    }
}

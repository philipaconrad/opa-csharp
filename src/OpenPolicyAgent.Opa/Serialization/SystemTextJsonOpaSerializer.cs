using System.Text.Json;

namespace OpenPolicyAgent.Opa.Serialization;

/// <summary>
/// <see cref="IOpaSerializer"/> implementation backed by System.Text.Json.
/// Pass an instance of this type to the <see cref="OpaClient"/> constructor
/// to opt into STJ for input serialization and result deserialization.
/// </summary>
/// <remarks>
/// Note: the SDK's data-filter result types (UCAST/SQL filters and column
/// masks via <c>OpenPolicyAgent.Ucast.Linq</c>) currently rely on Newtonsoft
/// converters and will not deserialize correctly under STJ. Use the default
/// <see cref="NewtonsoftOpaSerializer"/> when invoking
/// <c>GetFilters</c> / <c>GetMultipleFilters</c>.
/// </remarks>
public sealed class SystemTextJsonOpaSerializer : IOpaSerializer
{
    private readonly JsonSerializerOptions? _options;

    public SystemTextJsonOpaSerializer(JsonSerializerOptions? options = null)
    {
        _options = options;
    }

    /// <summary>The options instance this serializer was configured with, if any.</summary>
    public JsonSerializerOptions? Options => _options;

    public string Serialize(object? value) => JsonSerializer.Serialize(value, _options);

    public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _options);
}

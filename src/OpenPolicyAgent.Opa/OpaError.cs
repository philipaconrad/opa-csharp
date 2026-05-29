using Newtonsoft.Json;
using STJ = System.Text.Json.Serialization;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// Per-input failure details surfaced by <see cref="OpaClient"/> through
/// <see cref="OpaBatchEntry{T}.Error"/> or
/// <see cref="OpaServerException.BatchQueryErrors"/>. For uniform 4xx/5xx
/// failures of an entire request, see <see cref="OpaPolicyException"/> /
/// <see cref="OpaServerException"/> instead.
/// </summary>
public sealed class OpaError
{
    /// <summary>OPA short-form error code, e.g. "internal_error", "invalid_parameter".</summary>
    [JsonProperty("code")]
    [STJ.JsonPropertyName("code")]
    public string Code { get; set; } = default!;

    /// <summary>Long-form error message returned by OPA.</summary>
    [JsonProperty("message")]
    [STJ.JsonPropertyName("message")]
    public string Message { get; set; } = default!;

    /// <summary>Decision identifier supplied by OPA when decision logging is enabled.</summary>
    [JsonProperty("decision_id", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("decision_id")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionId { get; set; }

    /// <summary>HTTP status code associated with this error. Typically 500 for server-side, 400 for client-side; null when not applicable (e.g. all-failures fallback path).</summary>
    [JsonProperty("status_code", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("status_code")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; set; }

    public OpaError() { }

    public override string ToString() => JsonConvert.SerializeObject(this);
}

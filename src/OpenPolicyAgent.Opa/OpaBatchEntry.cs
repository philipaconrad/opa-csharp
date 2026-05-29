using System.Collections.Generic;
using Newtonsoft.Json;
using STJ = System.Text.Json.Serialization;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// One entry in the result of <see cref="OpaClient.EvaluateBatch{T}"/>.
/// Each entry represents either a successful evaluation (with the
/// deserialized <see cref="Value"/> and optional decision metadata) or a
/// failure (with <see cref="Error"/> populated). Discriminate via
/// <see cref="IsSuccess"/>.
/// </summary>
/// <remarks>
/// To consume only successes or only failures, use the
/// <see cref="OpaBatchExtensions.Successes{T}"/> /
/// <see cref="OpaBatchExtensions.Failures{T}"/> helpers.
/// </remarks>
public sealed class OpaBatchEntry<T>
{
    /// <summary>True when this entry represents a successful evaluation.</summary>
    [JsonProperty("is_success")]
    [STJ.JsonPropertyName("is_success")]
    public bool IsSuccess { get; init; }

    /// <summary>The deserialized policy result. Defined when <see cref="IsSuccess"/> is true; default(T) when the policy returned an undefined decision.</summary>
    [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("value")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public T? Value { get; init; }

    /// <summary>Failure details. Defined when <see cref="IsSuccess"/> is false.</summary>
    [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("error")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public OpaError? Error { get; init; }

    /// <summary>HTTP status code associated with this entry (200 for success, 500 for failure).</summary>
    [JsonProperty("status_code", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("status_code")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; init; }

    /// <summary>Decision identifier supplied by OPA when decision logging is enabled.</summary>
    [JsonProperty("decision_id", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("decision_id")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public string? DecisionId { get; init; }

    /// <summary>Per-eval query metrics, when <c>metrics=true</c> was set on the request.</summary>
    [JsonProperty("metrics", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("metrics")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object>? Metrics { get; init; }

    /// <summary>Provenance metadata, when <c>provenance=true</c> was set on the request.</summary>
    [JsonProperty("provenance", NullValueHandling = NullValueHandling.Ignore)]
    [STJ.JsonPropertyName("provenance")]
    [STJ.JsonIgnore(Condition = STJ.JsonIgnoreCondition.WhenWritingNull)]
    public OpaProvenance? Provenance { get; init; }

    public override string ToString() => JsonConvert.SerializeObject(this);
}

/// <summary>
/// Convenience filters for <see cref="OpaBatchEntry{T}"/> dictionaries.
/// </summary>
public static class OpaBatchExtensions
{
    /// <summary>Returns just the successful entries' values, keyed by the original input id.</summary>
    public static IDictionary<string, T> Successes<T>(this IDictionary<string, OpaBatchEntry<T>> batch)
    {
        var result = new Dictionary<string, T>();
        foreach (var kv in batch)
        {
            if (kv.Value.IsSuccess && kv.Value.Value is not null)
            {
                result[kv.Key] = kv.Value.Value;
            }
            else if (kv.Value.IsSuccess)
            {
                // T may be a non-nullable value type with a default value; preserve it for the caller.
                result[kv.Key] = default!;
            }
        }
        return result;
    }

    /// <summary>Returns just the failure entries' errors, keyed by the original input id.</summary>
    public static IDictionary<string, OpaError> Failures<T>(this IDictionary<string, OpaBatchEntry<T>> batch)
    {
        var result = new Dictionary<string, OpaError>();
        foreach (var kv in batch)
        {
            if (!kv.Value.IsSuccess && kv.Value.Error is not null)
            {
                result[kv.Key] = kv.Value.Error;
            }
        }
        return result;
    }
}

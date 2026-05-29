using System.Collections.Generic;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// A successful single-policy evaluation result paired with the metadata
/// fields OPA returns alongside it (decision id, metrics, provenance).
/// Returned by <see cref="OpaClient.EvaluateWithMetadataAsync{T}"/> for
/// callers that need decision-id correlation, query metrics, or bundle
/// revision info — for instance, audit logging.
/// </summary>
/// <remarks>
/// The simpler <see cref="OpaClient.Evaluate{T}"/> overload returns just
/// <c>T</c> and is preferred when metadata isn't needed.
/// </remarks>
public sealed class OpaResult<T>
{
    /// <summary>The deserialized policy result. Null when the queried path was undefined.</summary>
    public T? Value { get; init; }

    /// <summary>Decision identifier supplied by OPA when decision logging is enabled.</summary>
    public string? DecisionId { get; init; }

    /// <summary>Query performance metrics, when <c>metrics=true</c> was set on the request.</summary>
    public IReadOnlyDictionary<string, object>? Metrics { get; init; }

    /// <summary>Provenance metadata (OPA build info, bundle revisions), when <c>provenance=true</c> was set on the request.</summary>
    public OpaProvenance? Provenance { get; init; }

    /// <summary>HTTP status code of the response (always 200 for a successful single-eval).</summary>
    public int? StatusCode { get; init; }
}

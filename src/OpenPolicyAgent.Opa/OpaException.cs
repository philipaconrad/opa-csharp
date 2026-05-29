using System;

/// <summary>
/// Base type for all exceptions thrown by the OpenPolicyAgent.Opa SDK.
/// Concrete subtypes (<see cref="OpenPolicyAgent.Opa.OpaTransportException"/>,
/// <see cref="OpenPolicyAgent.Opa.OpaPolicyException"/>,
/// <see cref="OpenPolicyAgent.Opa.OpaServerException"/>,
/// <see cref="OpenPolicyAgent.Opa.OpaSerializationException"/>) discriminate the
/// failure mode. Catch the base type to ignore SDK errors uniformly, or a
/// subtype to handle a specific failure class.
/// </summary>
[Serializable]
public class OpaException : Exception
{
    /// <summary>HTTP status code returned by OPA, or null for transport-level failures.</summary>
    public int? StatusCode { get; }

    /// <summary>OPA short-form error code (e.g. "internal_error", "invalid_parameter"), or null when not provided by the server.</summary>
    public string? Code { get; }

    /// <summary>Decision identifier supplied by OPA when decision logging is enabled.</summary>
    public string? DecisionId { get; }

    /// <summary>The raw HTTP response body, when available. Useful for surfacing server messages the SDK didn't model.</summary>
    public string? RawBody { get; }

    public OpaException()
    { }

    public OpaException(string message)
        : base(message)
    { }

    public OpaException(string message, Exception innerException)
        : base(message, innerException)
    { }

    public OpaException(
        string message,
        int? statusCode,
        string? code = null,
        string? decisionId = null,
        string? rawBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
        DecisionId = decisionId;
        RawBody = rawBody;
    }
}

using System;
using System.Collections.Generic;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// Thrown when an HTTP request to OPA fails before a response is received
/// (DNS, connect, read timeout, TLS, etc.). <see cref="OpaException.StatusCode"/>
/// is always null on this subtype.
/// </summary>
[Serializable]
public class OpaTransportException : OpaException
{
    public OpaTransportException(string message, Exception? innerException = null)
        : base(message, statusCode: null, innerException: innerException)
    { }
}

/// <summary>
/// Thrown for HTTP 4xx responses from OPA: malformed query, invalid input,
/// missing path, batch endpoint not present on this server, etc. The request
/// will not succeed if retried as-is; the caller must adjust the inputs.
/// </summary>
[Serializable]
public class OpaPolicyException : OpaException
{
    public OpaPolicyException(string message, int statusCode, string? code = null, string? decisionId = null, string? rawBody = null, Exception? innerException = null)
        : base(message, statusCode, code, decisionId, rawBody, innerException)
    { }
}

/// <summary>
/// Thrown for HTTP 5xx responses from OPA: policy evaluation errors, internal
/// server errors, etc. Often safe to retry. For batch endpoints that return
/// 500 across all inputs, <see cref="BatchQueryErrors"/> carries the per-input
/// error details.
/// </summary>
[Serializable]
public class OpaServerException : OpaException
{
    /// <summary>For batch endpoints that returned 500 for every input, this contains the per-input error details keyed by the caller-supplied input id. Null for non-batch errors.</summary>
    public IReadOnlyDictionary<string, OpaError>? BatchQueryErrors { get; }

    /// <summary>The batch_decision_id supplied by OPA, when this exception arose from a batch endpoint.</summary>
    public string? BatchDecisionId { get; }

    public OpaServerException(string message, int statusCode, string? code = null, string? decisionId = null, string? rawBody = null, Exception? innerException = null)
        : base(message, statusCode, code, decisionId, rawBody, innerException)
    { }

    public OpaServerException(string message, int statusCode, IReadOnlyDictionary<string, OpaError> batchQueryErrors, string? batchDecisionId = null, string? rawBody = null)
        : base(message, statusCode, code: null, decisionId: null, rawBody: rawBody)
    {
        BatchQueryErrors = batchQueryErrors;
        BatchDecisionId = batchDecisionId;
    }
}

/// <summary>
/// Thrown when the SDK cannot serialize a request body or deserialize a
/// response body (unexpected content-type, malformed JSON, type-coercion
/// failure on the result). Indicates a contract mismatch between the SDK and
/// the server, or between the requested generic <c>T</c> and the actual
/// response shape.
/// </summary>
[Serializable]
public class OpaSerializationException : OpaException
{
    public OpaSerializationException(string message, Exception? innerException = null)
        : base(message, statusCode: null, innerException: innerException)
    { }

    public OpaSerializationException(string message, int? statusCode, string? rawBody, Exception? innerException = null)
        : base(message, statusCode, code: null, decisionId: null, rawBody: rawBody, innerException: innerException)
    { }
}

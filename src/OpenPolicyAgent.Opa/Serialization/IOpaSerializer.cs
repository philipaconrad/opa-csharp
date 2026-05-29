using System;

namespace OpenPolicyAgent.Opa.Serialization;

/// <summary>
/// Pluggable JSON (de)serialization strategy used by <see cref="OpaClient"/>
/// for request bodies and result coercion. Two implementations ship with the
/// SDK: <see cref="NewtonsoftOpaSerializer"/> (default, preserves backwards
/// compatibility with consumers that already pass
/// <see cref="Newtonsoft.Json.JsonSerializerSettings"/>) and
/// <see cref="SystemTextJsonOpaSerializer"/>.
/// </summary>
/// <remarks>
/// Implementations are not used for parsing OPA's response envelope itself
/// (that's done internally with a fixed parser); they are invoked only for
/// serializing user-supplied input objects and deserializing the result body
/// into the caller's <c>T</c>. This means a <c>T</c> with attributes from a
/// different serializer (e.g. <c>[JsonProperty]</c> when
/// <see cref="SystemTextJsonOpaSerializer"/> is configured) will not be
/// honored — choose the serializer that matches your domain types.
/// </remarks>
public interface IOpaSerializer
{
    /// <summary>Serialize an arbitrary user object to a JSON string for use as an HTTP request body.</summary>
    string Serialize(object? value);

    /// <summary>Deserialize a JSON string into the requested type <typeparamref name="T"/>.</summary>
    T? Deserialize<T>(string json);
}

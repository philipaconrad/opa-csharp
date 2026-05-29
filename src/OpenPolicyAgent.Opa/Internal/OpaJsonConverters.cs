using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenPolicyAgent.Opa.Internal;

/// <summary>
/// Newtonsoft converter that recursively normalizes a JSON object/array into
/// nested <see cref="Dictionary{TKey, TValue}"/> / <see cref="List{T}"/>
/// structures with primitive values unwrapped from <see cref="JValue"/>.
/// Triggered when the requested type is <see cref="Dictionary{TKey, TValue}"/>
/// of <c>string</c> to <c>object</c>. Lets callers assert against
/// <c>new Dictionary&lt;string, object&gt; { ... }</c> literals without
/// needing to account for Newtonsoft's default JObject/JArray nesting.
/// </summary>
internal sealed class OpaAnyDictionaryConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        objectType == typeof(Dictionary<string, object>) ||
        objectType == typeof(Dictionary<string, object?>);

    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        if (reader.TokenType != JsonToken.StartObject)
        {
            throw new JsonSerializationException("Expected start of object");
        }
        return ParseObject(JToken.Load(reader));
    }

    private static Dictionary<string, object?> ParseObject(JToken token)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in token.Children<JProperty>())
        {
            dict[prop.Name] = ParseValue(prop.Value);
        }
        return dict;
    }

    private static List<object?> ParseArray(JToken token)
    {
        var list = new List<object?>();
        foreach (var child in token.Children())
        {
            list.Add(ParseValue(child));
        }
        return list;
    }

    private static object? ParseValue(JToken value) => value switch
    {
        JObject => ParseObject(value),
        JArray => ParseArray(value),
        JValue v => v.Value,
        _ => null,
    };
}

/// <summary>
/// Newtonsoft converter that handles <c>object</c>-typed values (commonly
/// found inside <c>Dictionary&lt;string, object&gt;</c>) by unwrapping them
/// into native CLR types: nested <c>Dictionary</c> / <c>List</c> for objects /
/// arrays, primitives for leaves.
/// </summary>
internal sealed class OpaFlexibleObjectConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(object);

    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var token = JToken.ReadFrom(reader);
        return ParseValue(token);
    }

    private static object? ParseValue(JToken value) => value switch
    {
        JObject => ParseObject(value),
        JArray => ParseArray(value),
        JValue v => v.Value,
        _ => null,
    };

    private static Dictionary<string, object?> ParseObject(JToken token)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in token.Children<JProperty>())
        {
            dict[prop.Name] = ParseValue(prop.Value);
        }
        return dict;
    }

    private static List<object?> ParseArray(JToken token)
    {
        var list = new List<object?>();
        foreach (var child in token.Children())
        {
            list.Add(ParseValue(child));
        }
        return list;
    }
}

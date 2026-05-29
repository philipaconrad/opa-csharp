using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenPolicyAgent.Opa.Serialization;

namespace OpenPolicyAgent.Opa.Internal;

/// <summary>
/// Bespoke HTTP layer for OPA's REST API. Owns one <see cref="HttpClient"/>
/// (either user-supplied or default), serializes user input via the
/// configured <see cref="IOpaSerializer"/>, and parses OPA's response envelope
/// directly with <see cref="JsonDocument"/> (independent of the user's chosen
/// serializer — the envelope shape is fixed by OPA's wire protocol).
///
/// Only used by <see cref="OpaClient"/>; not part of the public surface.
/// </summary>
internal sealed class OpaHttp : IDisposable
{
    private static readonly HttpClient s_sharedClient = new();

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IOpaSerializer _serializer;
    private readonly Func<string>? _bearerTokenSource;
    private readonly string _baseUrl;

    public OpaHttp(string baseUrl, IOpaSerializer serializer, HttpClient? httpClient = null, Func<string>? bearerTokenSource = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _serializer = serializer;
        _bearerTokenSource = bearerTokenSource;
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = s_sharedClient;
            _ownsHttp = false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) { _http.Dispose(); }
    }

    // ---------- public endpoint methods ----------

    /// <summary>POST / — Execute the server's default decision against an input value. Note: this endpoint returns the raw decision value as the response body, not a {result, ...} envelope.</summary>
    public async Task<EvalEnvelope> EvaluateDefaultAsync(object? input, EvalRequestOptions opts, CancellationToken ct = default)
    {
        var url = BuildUrl("/", opts);
        var bodyJson = _serializer.Serialize(input);
        using var req = NewRequest(HttpMethod.Post, url, bodyJson);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException hre)
        {
            throw new OpaTransportException($"HTTP request to OPA failed: {hre.Message}", hre);
        }
        try
        {
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (status == 200)
            {
                // OPA returns the decision value directly, with no envelope. An empty/whitespace
                // body signifies an undefined decision.
                return new EvalEnvelope { RawResultJson = string.IsNullOrWhiteSpace(body) ? null : body };
            }
            if (status >= 400 && status < 500) throw BuildPolicyException(status, body);
            if (status >= 500) throw BuildServerException(status, body);
            throw new OpaSerializationException($"unexpected HTTP status {status}", status, body);
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>POST /v1/data/{path} — Evaluate a policy with the supplied input.</summary>
    public Task<EvalEnvelope> EvaluateAsync(string path, object? input, EvalRequestOptions opts, CancellationToken ct = default)
        => SendForEvalAsync(HttpMethod.Post, "/v1/data/" + path, body: input, wrapInput: true, opts, ct);

    /// <summary>GET /v1/data/{path} — Evaluate a policy with no input (data lookup style).</summary>
    public Task<EvalEnvelope> EvaluateNoInputAsync(string path, EvalRequestOptions opts, CancellationToken ct = default)
        => SendForEvalAsync(HttpMethod.Get, "/v1/data/" + path, body: null, wrapInput: false, opts, ct);

    /// <summary>POST /v1/batch/data/{path} — Evaluate a policy against a batch of inputs (EOPA only).</summary>
    public async Task<BatchEnvelope> EvaluateBatchAsync(string path, IDictionary<string, object?> inputs, EvalRequestOptions opts, CancellationToken ct = default)
    {
        var url = BuildUrl("/v1/batch/data/" + path, opts);
        var bodyJson = BuildBatchInputsJson(inputs);
        using var req = NewRequest(HttpMethod.Post, url, bodyJson);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            var status = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (status == 200 || status == 207)
            {
                return ParseBatchSuccess(status, body);
            }
            if (status == 500)
            {
                // Two shapes: { batch_decision_id, responses: {id: {code, message}, ...} } or a single-eval-style { code, message }.
                var (batchDecisionId, batchQueryErrors) = TryParseBatchAllFailures(body);
                if (batchQueryErrors is not null)
                {
                    throw new OpaServerException(
                        message: "OPA returned batch query errors for all inputs",
                        statusCode: status,
                        batchQueryErrors: batchQueryErrors,
                        batchDecisionId: batchDecisionId,
                        rawBody: body);
                }
                throw BuildServerException(status, body);
            }
            if (status >= 400 && status < 500)
            {
                throw BuildPolicyException(status, body);
            }
            if (status >= 500)
            {
                throw BuildServerException(status, body);
            }
            throw new OpaSerializationException($"unexpected HTTP status {status}", status, body);
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>POST /v1/compile/{path} — partial evaluation. Returns the raw HttpResponseMessage; caller decodes per content-type.</summary>
    public async Task<(int StatusCode, string? ContentType, string Body)> CompileAsync(string path, string requestBodyJson, string acceptHeader, EvalRequestOptions opts, CancellationToken ct = default)
    {
        var url = BuildUrl("/v1/compile/" + path, opts);
        using var req = NewRequest(HttpMethod.Post, url, requestBodyJson);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptHeader));
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            var status = (int)resp.StatusCode;
            var ctype = resp.Content.Headers.ContentType?.MediaType;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (status >= 400 && status < 500)
            {
                throw BuildPolicyException(status, body);
            }
            if (status >= 500)
            {
                throw BuildServerException(status, body);
            }
            return (status, ctype, body);
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>GET /health — server liveness/readiness probe.</summary>
    public async Task<bool> HealthAsync(bool? bundles, bool? plugins, IList<string>? excludePlugin, CancellationToken ct = default)
    {
        var qs = new StringBuilder();
        if (bundles is bool b) AppendQuery(qs, "bundles", b ? "true" : "false");
        if (plugins is bool p) AppendQuery(qs, "plugins", p ? "true" : "false");
        if (excludePlugin is not null)
        {
            foreach (var x in excludePlugin) AppendQuery(qs, "exclude-plugin", x);
        }
        var url = _baseUrl + "/health" + (qs.Length > 0 ? "?" + qs : "");
        using var req = NewRequest(HttpMethod.Get, url, null);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            var status = (int)resp.StatusCode;
            if (status == 200) return true;
            if (status == 500) return false;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new OpaServerException($"unexpected /health status {status}", status, rawBody: body);
        }
        finally
        {
            resp.Dispose();
        }
    }

    // ---------- shared single-eval send ----------

    private async Task<EvalEnvelope> SendForEvalAsync(HttpMethod method, string pathSegment, object? body, bool wrapInput, EvalRequestOptions opts, CancellationToken ct)
    {
        var url = BuildUrl(pathSegment, opts);
        string? bodyJson = null;
        if (method != HttpMethod.Get)
        {
            bodyJson = wrapInput
                ? BuildWrappedInputJson(body)
                : _serializer.Serialize(body);
        }
        using var req = NewRequest(method, url, bodyJson);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException hre)
        {
            throw new OpaTransportException($"HTTP request to OPA failed: {hre.Message}", hre);
        }
        try
        {
            var status = (int)resp.StatusCode;
            var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (status == 200)
            {
                return ParseSuccessEnvelope(responseBody);
            }
            if (status >= 400 && status < 500)
            {
                throw BuildPolicyException(status, responseBody);
            }
            if (status >= 500)
            {
                throw BuildServerException(status, responseBody);
            }
            throw new OpaSerializationException($"unexpected HTTP status {status}", status, responseBody);
        }
        finally
        {
            resp.Dispose();
        }
    }

    // ---------- request building ----------

    private string BuildUrl(string pathSegment, EvalRequestOptions opts)
    {
        var qs = new StringBuilder();
        AppendQuery(qs, "pretty", opts.Pretty ? "true" : "false");
        AppendQuery(qs, "provenance", opts.Provenance ? "true" : "false");
        if (!string.IsNullOrEmpty(opts.Explain)) AppendQuery(qs, "explain", opts.Explain);
        AppendQuery(qs, "metrics", opts.Metrics ? "true" : "false");
        AppendQuery(qs, "instrument", opts.Instrument ? "true" : "false");
        AppendQuery(qs, "strict-builtin-errors", opts.StrictBuiltinErrors ? "true" : "false");
        return _baseUrl + pathSegment + (qs.Length > 0 ? "?" + qs : "");
    }

    private static void AppendQuery(StringBuilder qs, string key, string value)
    {
        if (qs.Length > 0) qs.Append('&');
        qs.Append(Uri.EscapeDataString(key));
        qs.Append('=');
        qs.Append(Uri.EscapeDataString(value));
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url, string? bodyJson)
    {
        var req = new HttpRequestMessage(method, url);
        if (_bearerTokenSource is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerTokenSource());
        }
        if (bodyJson is not null)
        {
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }
        return req;
    }

    private string BuildWrappedInputJson(object? input)
    {
        // {"input": <serialized-input>} — written by string concatenation so the user's
        // serializer choice (Newtonsoft vs STJ) handles the input value, not the wrapper.
        var inner = _serializer.Serialize(input);
        return "{\"input\":" + inner + "}";
    }

    private string BuildBatchInputsJson(IDictionary<string, object?> inputs)
    {
        // {"inputs": {"id1": <serialized>, "id2": <serialized>, ...}}
        var sb = new StringBuilder("{\"inputs\":{");
        bool first = true;
        foreach (var kv in inputs)
        {
            if (!first) sb.Append(',');
            first = false;
            // input ids are arbitrary strings supplied by callers; escape per JSON rules.
            sb.Append('"');
            sb.Append(EscapeJsonString(kv.Key));
            sb.Append("\":");
            sb.Append(_serializer.Serialize(kv.Value));
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        // Minimal JSON string escaper for keys. Avoids pulling Newtonsoft for a few characters.
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------- response envelope parsing ----------

    private static EvalEnvelope ParseSuccessEnvelope(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new EvalEnvelope();
        }
        using var doc = JsonDocument.Parse(body);
        return ReadEvalEnvelope(doc.RootElement);
    }

    private static EvalEnvelope ReadEvalEnvelope(JsonElement root)
    {
        var env = new EvalEnvelope();
        if (root.ValueKind != JsonValueKind.Object) return env;
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "result":
                    env.RawResultJson = prop.Value.GetRawText();
                    break;
                case "decision_id":
                    env.DecisionId = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;
                case "metrics":
                    env.RawMetricsJson = prop.Value.GetRawText();
                    break;
                case "provenance":
                    env.RawProvenanceJson = prop.Value.GetRawText();
                    break;
            }
        }
        return env;
    }

    private static BatchEnvelope ParseBatchSuccess(int status, string body)
    {
        var env = new BatchEnvelope { StatusCode = status };
        if (string.IsNullOrEmpty(body)) return env;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return env;
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "batch_decision_id":
                    env.BatchDecisionId = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;
                case "metrics":
                    env.RawMetricsJson = prop.Value.GetRawText();
                    break;
                case "responses":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in prop.Value.EnumerateObject())
                        {
                            env.Entries[entry.Name] = ReadBatchEntry(entry.Value);
                        }
                    }
                    break;
            }
        }
        return env;
    }

    private static BatchEntryRaw ReadBatchEntry(JsonElement el)
    {
        var entry = new BatchEntryRaw { StatusCode = 200 };
        if (el.ValueKind != JsonValueKind.Object) return entry;
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "http_status_code":
                    if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var sc))
                        entry.StatusCode = sc;
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                        entry.StatusCode = prop.Value.GetInt32();
                    break;
                case "result":
                    entry.RawResultJson = prop.Value.GetRawText();
                    break;
                case "decision_id":
                    entry.DecisionId = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;
                case "metrics":
                    entry.RawMetricsJson = prop.Value.GetRawText();
                    break;
                case "provenance":
                    entry.RawProvenanceJson = prop.Value.GetRawText();
                    break;
                case "code":
                    entry.Code = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;
                case "message":
                    entry.Message = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                    break;
            }
        }
        // If 'code'/'message' are present but http_status_code was absent, infer 500.
        if (entry.RawResultJson is null && entry.Code is not null && !el.TryGetProperty("http_status_code", out _))
        {
            entry.StatusCode = 500;
        }
        return entry;
    }

    private static (string? batchDecisionId, IReadOnlyDictionary<string, OpaError>? batchQueryErrors) TryParseBatchAllFailures(string body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            string? batchId = null;
            Dictionary<string, OpaError>? errors = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "batch_decision_id" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    batchId = prop.Value.GetString();
                }
                else if (prop.Name == "responses" && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    errors = new Dictionary<string, OpaError>();
                    foreach (var entry in prop.Value.EnumerateObject())
                    {
                        var raw = ReadBatchEntry(entry.Value);
                        errors[entry.Name] = new OpaError
                        {
                            Code = raw.Code ?? "internal_error",
                            Message = raw.Message ?? "",
                            DecisionId = raw.DecisionId,
                        };
                    }
                }
            }
            if (errors is null || errors.Count == 0) return (batchId, null);
            return (batchId, errors);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    // ---------- error response parsing ----------

    private static OpaPolicyException BuildPolicyException(int status, string body)
    {
        var (code, message, decisionId) = TryParseError(body);
        return new OpaPolicyException(
            message: message ?? $"OPA returned HTTP {status}",
            statusCode: status,
            code: code,
            decisionId: decisionId,
            rawBody: body);
    }

    private static OpaServerException BuildServerException(int status, string body)
    {
        var (code, message, decisionId) = TryParseError(body);
        return new OpaServerException(
            message: message ?? $"OPA returned HTTP {status}",
            statusCode: status,
            code: code,
            decisionId: decisionId,
            rawBody: body);
    }

    private static (string? code, string? message, string? decisionId) TryParseError(string body)
    {
        if (string.IsNullOrEmpty(body)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? code = null, message = null, decisionId = null;
            foreach (var prop in root.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "code":
                        if (prop.Value.ValueKind == JsonValueKind.String) code = prop.Value.GetString();
                        break;
                    case "message":
                        if (prop.Value.ValueKind == JsonValueKind.String) message = prop.Value.GetString();
                        break;
                    case "decision_id":
                        if (prop.Value.ValueKind == JsonValueKind.String) decisionId = prop.Value.GetString();
                        break;
                }
            }
            return (code, message, decisionId);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}

internal struct EvalRequestOptions
{
    public bool Pretty;
    public bool Provenance;
    public string? Explain;
    public bool Metrics;
    public bool Instrument;
    public bool StrictBuiltinErrors;
}

internal sealed class EvalEnvelope
{
    public string? RawResultJson;
    public string? DecisionId;
    public string? RawMetricsJson;
    public string? RawProvenanceJson;
}

internal sealed class BatchEnvelope
{
    public int StatusCode;
    public string? BatchDecisionId;
    public string? RawMetricsJson;
    public Dictionary<string, BatchEntryRaw> Entries = new();
}

internal sealed class BatchEntryRaw
{
    public int StatusCode;
    public string? RawResultJson;
    public string? DecisionId;
    public string? RawMetricsJson;
    public string? RawProvenanceJson;
    public string? Code;
    public string? Message;
}

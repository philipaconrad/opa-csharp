using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using OpenPolicyAgent.Opa.Filters;
using OpenPolicyAgent.Opa.Internal;
using OpenPolicyAgent.Opa.Serialization;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// OpaClient provides high-level convenience APIs for interacting with an OPA server.
/// It is generally recommended to use this class for most common OPA integrations.
/// </summary>
public class OpaClient
{
    private const string DefaultServerUrl = "http://localhost:8181";

    private readonly OpaHttp _http;
    private readonly IOpaSerializer _defaultSerializer;
    private readonly string _serverUrl;
    private readonly ILogger _logger;

    // Records whether to go to fallback mode immediately for batched queries.
    // Switched to false the first time the OPA server returns a 404 from
    // /v1/batch/data (vanilla OSS OPA, where that endpoint isn't implemented).
    private bool _opaSupportsBatchQueryAPI = true;

    private static readonly EvalRequestOptions DefaultOptions = new()
    {
        Pretty = false,
        Provenance = false,
        Explain = "notes",
        Metrics = false,
        Instrument = false,
        StrictBuiltinErrors = false,
    };

    /// <summary>
    /// Constructs an OpaClient, connecting to a specified server address if provided.
    /// </summary>
    /// <param name="serverUrl">The URL for connecting to the OPA server instance. (default: "http://localhost:8181")</param>
    /// <param name="logger">The ILogger instance to use for this OpaClient. (default: NullLogger)</param>
    /// <param name="jsonSerializerSettings">The Newtonsoft.Json.JsonSerializerSettings to use as the default for serializing inputs for OPA. Ignored when an explicit <paramref name="serializer"/> is supplied. (default: SDK defaults that include OPA's value-shape converters)</param>
    /// <param name="serializer">Pluggable JSON (de)serialization strategy. Pass an instance of <see cref="NewtonsoftOpaSerializer"/> or <see cref="SystemTextJsonOpaSerializer"/>. (default: <see cref="NewtonsoftOpaSerializer"/> initialized from <paramref name="jsonSerializerSettings"/>)</param>
    /// <param name="httpClient">A custom HttpClient instance to use for all requests. Useful for setting timeouts, custom DelegatingHandlers, or non-bearer auth schemes. (default: a process-wide shared HttpClient)</param>
    /// <param name="bearerTokenSource">An optional callback returning a bearer token to attach to each request. Invoked once per request, so token rotation is supported. (default: no Authorization header)</param>
    public OpaClient(
        string? serverUrl = null,
        ILogger<OpaClient>? logger = null,
        JsonSerializerSettings? jsonSerializerSettings = null,
        IOpaSerializer? serializer = null,
        HttpClient? httpClient = null,
        Func<string>? bearerTokenSource = null)
    {
        _serverUrl = serverUrl?.TrimEnd('/') ?? DefaultServerUrl;
        _logger = logger ?? new NullLogger<OpaClient>();
        _defaultSerializer = serializer ?? new NewtonsoftOpaSerializer(jsonSerializerSettings);
        _http = new OpaHttp(_serverUrl, _defaultSerializer, httpClient, bearerTokenSource);
    }

    // ---------- single-eval high-level API ----------

    /// <summary>
    /// Simple allow/deny-style check against a rule, using the provided object.
    /// Equivalent to <see cref="Evaluate{T}"/> with <c>T = bool</c>.
    /// </summary>
    /// <param name="path">The rule to evaluate. (Example: "app/rbac")</param>
    /// <param name="input">The input C# object OPA will use for evaluating the rule.</param>
    /// <param name="jsonSerializerSettings">Optional Newtonsoft serializer settings used for serializing <paramref name="input"/> on this call only.</param>
    public Task<bool> Check(string path, object? input, JsonSerializerSettings? jsonSerializerSettings = null)
        => Evaluate<bool>(path, input, jsonSerializerSettings);

    /// <summary>
    /// Evaluate a policy and coerce the result to type <typeparamref name="T"/>.
    /// Throws an <see cref="OpaException"/> subtype on failure.
    /// </summary>
    /// <param name="path">The rule to evaluate. (Example: "app/rbac")</param>
    /// <param name="input">The input C# object OPA will use for evaluating the rule.</param>
    /// <param name="jsonSerializerSettings">Optional per-call Newtonsoft serializer settings used to round-trip <paramref name="input"/> before sending. Output deserialization always uses the configured default serializer.</param>
    public async Task<T> Evaluate<T>(string path, object? input, JsonSerializerSettings? jsonSerializerSettings = null)
    {
        EvalEnvelope env;
        try
        {
            env = await _http.EvaluateAsync(path, MaybeRoundTrip(input, jsonSerializerSettings), DefaultOptions).ConfigureAwait(false);
        }
        catch (OpaException e)
        {
            LogMessages.LogQueryError(_logger, path, e.Message);
            throw;
        }
        catch (Exception e)
        {
            LogMessages.LogQueryError(_logger, path, e.Message);
            throw new OpaException($"executing policy at '{path}' failed due to exception '{e}'", e);
        }
        return CoerceResult<T>(env.RawResultJson, path);
    }

    /// <summary>
    /// Evaluate the server's default policy and coerce the result to type <typeparamref name="T"/>.
    /// </summary>
    public async Task<T> EvaluateDefault<T>(object? input, JsonSerializerSettings? jsonSerializerSettings = null)
    {
        EvalEnvelope env;
        try
        {
            env = await _http.EvaluateDefaultAsync(MaybeRoundTrip(input, jsonSerializerSettings), DefaultOptions).ConfigureAwait(false);
        }
        catch (OpaException e)
        {
            LogMessages.LogDefaultQueryError(_logger, e.Message);
            throw;
        }
        catch (Exception e)
        {
            LogMessages.LogDefaultQueryError(_logger, e.Message);
            throw new OpaException($"executing server default policy failed due to exception '{e}'", e);
        }
        return CoerceDefault<T>(env.RawResultJson);
    }

    /// <summary>
    /// Evaluate a policy and return the deserialized result alongside OPA's
    /// per-decision metadata (decision_id, metrics, provenance). Useful for
    /// audit logging or correlating policy decisions with downstream effects.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the policy result into.</typeparam>
    /// <param name="path">The rule to evaluate.</param>
    /// <param name="input">The input C# object OPA will use for evaluating the rule.</param>
    /// <param name="provenance">Whether to request OPA build/bundle provenance metadata.</param>
    /// <param name="metrics">Whether to request OPA query performance metrics.</param>
    /// <param name="jsonSerializerSettings">Optional per-call Newtonsoft serializer settings.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<OpaResult<T>> EvaluateWithMetadataAsync<T>(
        string path,
        object? input,
        bool provenance = true,
        bool metrics = false,
        JsonSerializerSettings? jsonSerializerSettings = null,
        CancellationToken ct = default)
    {
        var opts = DefaultOptions;
        opts.Provenance = provenance;
        opts.Metrics = metrics;
        EvalEnvelope env;
        try
        {
            env = await _http.EvaluateAsync(path, MaybeRoundTrip(input, jsonSerializerSettings), opts, ct).ConfigureAwait(false);
        }
        catch (OpaException e)
        {
            LogMessages.LogQueryError(_logger, path, e.Message);
            throw;
        }
        return new OpaResult<T>
        {
            Value = env.RawResultJson is null ? default : _defaultSerializer.Deserialize<T>(env.RawResultJson),
            DecisionId = env.DecisionId,
            Metrics = env.RawMetricsJson is null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(env.RawMetricsJson),
            Provenance = env.RawProvenanceJson is null ? null : JsonConvert.DeserializeObject<OpaProvenance>(env.RawProvenanceJson),
            StatusCode = 200,
        };
    }

    // ---------- batch-eval high-level API ----------

    /// <summary>
    /// Evaluate a policy against a map of inputs. Each entry in the returned
    /// dictionary is either a successful evaluation (<see cref="OpaBatchEntry{T}.IsSuccess"/>
    /// is true, <see cref="OpaBatchEntry{T}.Value"/> populated) or a failure
    /// (<see cref="OpaBatchEntry{T}.Error"/> populated).
    /// </summary>
    /// <remarks>
    /// When the OPA server does not support the <c>/v1/batch/data</c> endpoint
    /// (vanilla OSS OPA returns 404), the client transparently falls back to
    /// running each input as a sequential single-policy query and reconstructs
    /// the same result shape. The fallback is sticky for the lifetime of this
    /// client.
    ///
    /// Use the <see cref="OpaBatchExtensions.Successes{T}"/> /
    /// <see cref="OpaBatchExtensions.Failures{T}"/> helpers to filter the result
    /// dictionary down to one side at a time.
    /// </remarks>
    /// <typeparam name="T">The type to deserialize each successful policy result into.</typeparam>
    /// <param name="path">The rule to evaluate.</param>
    /// <param name="inputs">Map of caller-supplied input ids to input values.</param>
    public async Task<Dictionary<string, OpaBatchEntry<T>>> EvaluateBatch<T>(string path, IDictionary<string, object?> inputs)
    {
        if (_opaSupportsBatchQueryAPI)
        {
            try
            {
                var batch = await _http.EvaluateBatchAsync(path, inputs, DefaultOptions).ConfigureAwait(false);
                return BatchToEntries<T>(batch);
            }
            catch (OpaPolicyException ex) when (ex.StatusCode == 404)
            {
                _opaSupportsBatchQueryAPI = false;
                LogMessages.LogBatchQueryFallback(_logger);
            }
            catch (OpaServerException ex) when (ex.BatchQueryErrors is not null)
            {
                var entries = new Dictionary<string, OpaBatchEntry<T>>(ex.BatchQueryErrors.Count);
                foreach (var kv in ex.BatchQueryErrors)
                {
                    entries[kv.Key] = new OpaBatchEntry<T> { IsSuccess = false, Error = kv.Value };
                }
                return entries;
            }
            // Other OpaPolicyException / OpaServerException variants propagate up.
        }

        return await BatchFallbackAsync<T>(path, inputs).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, OpaBatchEntry<T>>> BatchFallbackAsync<T>(string path, IDictionary<string, object?> inputs)
    {
        var results = new Dictionary<string, OpaBatchEntry<T>>(inputs.Count);
        bool sawSuccess = false, sawFailure = false;
        foreach (var kv in inputs)
        {
            try
            {
                var env = await _http.EvaluateAsync(path, kv.Value, DefaultOptions).ConfigureAwait(false);
                var v = env.RawResultJson is null ? default : _defaultSerializer.Deserialize<T>(env.RawResultJson);
                results[kv.Key] = new OpaBatchEntry<T>
                {
                    IsSuccess = true,
                    Value = v,
                    DecisionId = env.DecisionId,
                };
                sawSuccess = true;
            }
            catch (OpaServerException ex)
            {
                results[kv.Key] = new OpaBatchEntry<T>
                {
                    IsSuccess = false,
                    Error = new OpaError
                    {
                        Code = ex.Code ?? "internal_error",
                        Message = ex.Message ?? "",
                        DecisionId = ex.DecisionId,
                    },
                };
                sawFailure = true;
            }
            // OpaPolicyException (4xx) propagates: a malformed request is fatal for the whole batch.
        }
        // For mixed results, surface explicit per-entry status codes for symmetry with the EOPA endpoint's 207 response.
        if (sawSuccess && sawFailure)
        {
            foreach (var key in results.Keys.ToList())
            {
                var prev = results[key];
                results[key] = prev.IsSuccess
                    ? new OpaBatchEntry<T> { IsSuccess = true, Value = prev.Value, DecisionId = prev.DecisionId, Metrics = prev.Metrics, Provenance = prev.Provenance, StatusCode = 200 }
                    : new OpaBatchEntry<T> { IsSuccess = false, Error = new OpaError { Code = prev.Error!.Code, Message = prev.Error.Message, DecisionId = prev.Error.DecisionId, StatusCode = 500 }, StatusCode = 500 };
            }
        }
        return results;
    }

    private Dictionary<string, OpaBatchEntry<T>> BatchToEntries<T>(BatchEnvelope batch)
    {
        var results = new Dictionary<string, OpaBatchEntry<T>>(batch.Entries.Count);
        bool mixed = batch.StatusCode == 207;
        foreach (var kv in batch.Entries)
        {
            var raw = kv.Value;
            if (raw.StatusCode == 200)
            {
                var v = raw.RawResultJson is null ? default : _defaultSerializer.Deserialize<T>(raw.RawResultJson);
                results[kv.Key] = new OpaBatchEntry<T>
                {
                    IsSuccess = true,
                    Value = v,
                    StatusCode = mixed ? 200 : null,
                    DecisionId = raw.DecisionId,
                    Metrics = raw.RawMetricsJson is null ? null : JsonConvert.DeserializeObject<Dictionary<string, object>>(raw.RawMetricsJson),
                    Provenance = raw.RawProvenanceJson is null ? null : JsonConvert.DeserializeObject<OpaProvenance>(raw.RawProvenanceJson),
                };
            }
            else
            {
                results[kv.Key] = new OpaBatchEntry<T>
                {
                    IsSuccess = false,
                    StatusCode = mixed ? raw.StatusCode : null,
                    Error = new OpaError
                    {
                        Code = raw.Code ?? "internal_error",
                        Message = raw.Message ?? "",
                        DecisionId = raw.DecisionId,
                        StatusCode = mixed ? raw.StatusCode : null,
                    },
                };
            }
        }
        return results;
    }

    // ---------- compile / data filters ----------

    /// <summary>
    /// Uses EOPA's Compile API to partially evaluate a data filter policy.
    /// </summary>
    public async Task<(IFilter, ColumnMasks?)> GetFilters(
        string path,
        object? input,
        List<string>? unknowns = null,
        Filters.TargetSQLTableMappings? tableMappings = null,
        Filters.TargetDialects targetDialect = Filters.TargetDialects.UcastLinq,
        JsonSerializerSettings? jsonSerializerSettings = null)
    {
        var (jsonContent, acceptHeader) = BuildCompilePayload(input, unknowns, tableMappings, [targetDialect], jsonSerializerSettings);
        var (_, _, body) = await _http.CompileAsync(path, jsonContent, acceptHeader, DefaultOptions).ConfigureAwait(false);
        return targetDialect switch
        {
            Filters.TargetDialects.UcastAll or Filters.TargetDialects.UcastMinimal or Filters.TargetDialects.UcastPrisma or Filters.TargetDialects.UcastLinq
                => BuildCompileResultUCAST(path, body, targetDialect),
            Filters.TargetDialects.SqlSqlserver or Filters.TargetDialects.SqlMysql or Filters.TargetDialects.SqlPostgresql or Filters.TargetDialects.SqlSqlite
                => BuildCompileResultSQL(path, body, targetDialect),
            _ => throw new NotImplementedException(),
        };
    }

    /// <summary>
    /// Uses EOPA's Compile API for multi-target partial evaluation.
    /// </summary>
    public async Task<(Dictionary<string, IFilter>, ColumnMasks?)> GetMultipleFilters(
        string path,
        object? input,
        List<string>? unknowns = null,
        Filters.TargetSQLTableMappings? tableMappings = null,
        List<Filters.TargetDialects>? targetDialects = null,
        JsonSerializerSettings? jsonSerializerSettings = null)
    {
        targetDialects ??= [Filters.TargetDialects.UcastLinq];
        var (jsonContent, acceptHeader) = BuildCompilePayload(input, unknowns, tableMappings, targetDialects, jsonSerializerSettings);
        var (_, _, body) = await _http.CompileAsync(path, jsonContent, acceptHeader, DefaultOptions).ConfigureAwait(false);

        var result = JsonConvert.DeserializeObject<CompileResultMultitargetRecord>(body)
            ?? throw new OpaException($"executing policy at '{path}' succeeded, but OPA did not reply with valid data filters");

        var queries = new Dictionary<string, IFilter>(targetDialects.Count);
        ColumnMasks? masks = null;
        foreach (var dialect in targetDialects)
        {
            switch (dialect)
            {
                case Filters.TargetDialects.UcastAll:
                case Filters.TargetDialects.UcastMinimal:
                case Filters.TargetDialects.UcastPrisma:
                case Filters.TargetDialects.UcastLinq:
                    if (result.Result.Ucast is not null) queries["ucast"] = new UCASTFilter(result.Result.Ucast.Query);
                    masks ??= result.Result.Ucast?.Masks;
                    break;
                case Filters.TargetDialects.SqlPostgresql:
                    queries["postgresql"] = new SQLFilter(result.Result.PostgreSql?.Query ?? "", "postgresql");
                    masks ??= result.Result.PostgreSql?.Masks;
                    break;
                case Filters.TargetDialects.SqlMysql:
                    queries["mysql"] = new SQLFilter(result.Result.MySql?.Query ?? "", "mysql");
                    masks ??= result.Result.MySql?.Masks;
                    break;
                case Filters.TargetDialects.SqlSqlserver:
                    queries["sqlserver"] = new SQLFilter(result.Result.SqlServer?.Query ?? "", "sqlserver");
                    masks ??= result.Result.SqlServer?.Masks;
                    break;
                case Filters.TargetDialects.SqlSqlite:
                    queries["sqlite"] = new SQLFilter(result.Result.Sqlite?.Query ?? "", "sqlserver");
                    masks ??= result.Result.Sqlite?.Masks;
                    break;
            }
        }
        return (queries, masks);
    }

    private (string jsonContent, string acceptHeader) BuildCompilePayload(
        object? input,
        List<string>? unknowns,
        Filters.TargetSQLTableMappings? tableMappings,
        List<Filters.TargetDialects> targetDialects,
        JsonSerializerSettings? jsonSerializerSettings)
    {
        var acceptHeader = targetDialects.Count switch
        {
            1 => targetDialects[0].ToAcceptHeader(),
            _ => "application/vnd.opa.multitarget+json",
        };
        var reqObj = new Dictionary<string, object?> { { "input", input } };
        if (unknowns is not null) reqObj.Add("unknowns", unknowns);
        if (tableMappings is not null || targetDialects.Count > 1)
        {
            var options = new Dictionary<string, object>(2);
            if (tableMappings is not null) options.Add("tableMappings", tableMappings);
            if (targetDialects.Count > 1) options.Add("targetDialects", targetDialects.Select(x => x.ToOptionString()).ToList());
            reqObj.Add("options", options);
        }
        // Compile API payloads always go through Newtonsoft because the filter result types
        // (UCASTNode, MaskingTypes) are decorated with Newtonsoft-specific converters.
        return (JsonConvert.SerializeObject(reqObj, jsonSerializerSettings), acceptHeader);
    }

    private (IFilter, ColumnMasks?) BuildCompileResultUCAST(string path, string response, Filters.TargetDialects dialect)
    {
        var result = JsonConvert.DeserializeObject<CompileResultUCASTRecord>(response)
            ?? throw new OpaException($"executing policy at '{path}' succeeded, but OPA did not reply with valid data filters");
        IFilter query = dialect switch
        {
            Filters.TargetDialects.UcastAll or Filters.TargetDialects.UcastMinimal or Filters.TargetDialects.UcastPrisma or Filters.TargetDialects.UcastLinq
                => new UCASTFilter(result.Result.Query),
            _ => throw new NotImplementedException(),
        };
        return (query, result.Result.Masks);
    }

    private (IFilter, ColumnMasks?) BuildCompileResultSQL(string path, string response, Filters.TargetDialects dialect)
    {
        var result = JsonConvert.DeserializeObject<CompileResultSQLRecord>(response)
            ?? throw new OpaException($"executing policy at '{path}' succeeded, but OPA did not reply with valid data filters");
        IFilter query = dialect switch
        {
            Filters.TargetDialects.SqlPostgresql or Filters.TargetDialects.SqlMysql or Filters.TargetDialects.SqlSqlserver or Filters.TargetDialects.SqlSqlite
                => new SQLFilter(result.Result.Query, dialect.ToOptionString()),
            _ => throw new NotImplementedException(),
        };
        return (query, result.Result.Masks);
    }

    // ---------- result coercion helpers ----------

    private T CoerceResult<T>(string? rawResultJson, string path)
    {
        if (rawResultJson is null)
        {
            LogMessages.LogQueryNullResult(_logger, path);
            throw new OpaException($"executing policy at '{path}' succeeded, but OPA did not reply with a result");
        }
        return DeserializeOrThrow<T>(rawResultJson);
    }

    private T CoerceDefault<T>(string? rawResultJson)
    {
        if (rawResultJson is null)
        {
            LogMessages.LogDefaultQueryNullResult(_logger);
            throw new OpaException("executing server default policy succeeded, but OPA did not reply with a result");
        }
        return DeserializeOrThrow<T>(rawResultJson);
    }

    private T DeserializeOrThrow<T>(string rawJson)
    {
        try
        {
            var v = _defaultSerializer.Deserialize<T>(rawJson);
            if (v is null)
            {
                if (IsNullable(typeof(T))) return default!;
                throw new OpaException($"Could not convert result to type {typeof(T).FullName}");
            }
            return v;
        }
        catch (OpaException) { throw; }
        catch (Exception e)
        {
            throw new OpaException($"Exception occurred while converting result to type {typeof(T).FullName}", e);
        }
    }

    private static bool IsNullable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) != null;

    // Round-trip arbitrary inputs through the per-call settings if they were supplied.
    // Per-call settings only affect input serialization; result deserialization always
    // goes through the configured default serializer.
    private static object? MaybeRoundTrip(object? input, JsonSerializerSettings? perCallSettings)
    {
        if (input is null) return null;
        if (perCallSettings is null) return input;
        var json = JsonConvert.SerializeObject(input, perCallSettings);
        return JsonConvert.DeserializeObject<object>(json, perCallSettings);
    }
}

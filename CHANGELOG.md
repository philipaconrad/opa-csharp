# Change Log

All notable changes to this project will be documented in this file. This
project adheres to [Semantic Versioning](http://semver.org/).

## Unreleased

## 2.0.0

This release replaces the Speakeasy-generated SDK code with a compact, bespoke client. The HTTP behavior against OPA is unchanged. The high-level `OpaClient` remains the recommended surface, with new optional constructor parameters for serializer choice, `HttpClient` injection, and bearer-token authentication.

This is a major release because the surface area below `OpaClient` has been redesigned. The previous low-level `OpaApiClient` and the Speakeasy-emitted models, requests, and exceptions are no more. Most consumers of the high-level API will only need to update batch evaluation call sites and exception-catching code.

### New features

#### Pluggable JSON serializer

`OpaClient` now accepts an `IOpaSerializer` on its constructor, and the SDK ships with two implementations: `NewtonsoftOpaSerializer` (the default, preserving backwards compatibility with the existing `JsonSerializerSettings` parameter) and `SystemTextJsonOpaSerializer` for projects that have standardized on `System.Text.Json`.

```csharp
using OpenPolicyAgent.Opa;
using OpenPolicyAgent.Opa.Serialization;

var opa = new OpaClient(
    serverUrl: "http://localhost:8181",
    serializer: new SystemTextJsonOpaSerializer());

var allowed = await opa.Check("authz/allow", input);
```

> **Note**: `GetFilters` / `GetMultipleFilters` still require Newtonsoft for response parsing because `OpenPolicyAgent.Ucast.Linq` ships Newtonsoft-specific `JsonConverter`s on its filter types. Other operations work end-to-end under either serializer.

#### Improved exception hierarchy

The exceptions the library will use have been redesigned around a single base type, `OpaException`, plus four subtypes that distinguish the failure class. All exceptions carry fixed properties (`StatusCode`, `Code`, `DecisionId`, `RawBody`) so callers can branch on whatever level of detail fits their use case.

| Subtype                     | Thrown for                                                                 |
|-----------------------------|----------------------------------------------------------------------------|
| `OpaTransportException`     | Network failures: DNS, connect, read timeout, TLS. `StatusCode` is null.   |
| `OpaPolicyException`        | HTTP 4xx — malformed query, unknown path, batch endpoint not present.      |
| `OpaServerException`        | HTTP 5xx — eval errors, internal errors. May carry `BatchQueryErrors`.       |
| `OpaSerializationException` | Unexpected content type, malformed JSON, type-coercion failure on results. |

```csharp
try {
    var allowed = await opa.Check("authz/allow", input);
}
catch (OpaServerException e) when (e.Code == "internal_error") {
    // 5xx from the OPA server.
}
catch (OpaPolicyException e) {
    // 4xx indicating the request itself is malformed.
}
catch (OpaTransportException) {
    // Network problem reaching OPA.
}
```

Catching the base `OpaException` still works for callers who just want catch everything.

#### `EvaluateWithMetadataAsync<T>` for decision metadata

For callers who need OPA's `decision_id`, query metrics, or bundle provenance alongside the policy result, there's a new method that returns an `OpaResult<T>` carrying the value plus the response metadata fields.

```csharp
OpaResult<bool> result = await opa.EvaluateWithMetadataAsync<bool>(
    "authz/allow",
    input,
    provenance: true,
    metrics: false);

logger.LogInformation(
    "decision {DecisionId} on bundle {Version} = {Allow}",
    result.DecisionId,
    result.Provenance?.Version,
    result.Value);
```

The simpler `Evaluate<T>` variants still returns just `T` and remains the recommended choice when metadata isn't needed.

#### `HttpClient` injection

For full control over timeouts, custom `DelegatingHandler`s, mTLS, proxy configuration, or auth schemes other than bearer tokens, `OpaClient` now accepts a user-supplied `HttpClient`. When supplied, the SDK does not dispose of it — the caller owns its lifecycle.

```csharp
var handler = new SocketsHttpHandler {
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};
using var http = new HttpClient(handler) {
    Timeout = TimeSpan.FromSeconds(30),
};

var opa = new OpaClient(serverUrl: "https://opa.internal:8181", httpClient: http);
```

When no `HttpClient` is supplied, the SDK uses a shared default, matching the .NET idiom of long-lived `HttpClient` instances.

#### Bearer-token rotation

For servers that require a bearer token, pass a callback rather than a static string. The callback is invoked once per request, so rotating credentials work without rebuilding the client.

```csharp
var opa = new OpaClient(
    serverUrl: "https://opa.internal:8181",
    bearerTokenSource: () => tokenProvider.GetCurrentToken());
```

This feature replaces the previous Speakeasy-emitted `bearerAuth:` constructor parameter on the now-removed `OpaApiClient` type.

### Breaking changes
#### `EvaluateBatch<T>` type changes

`EvaluateBatch` returned a tuple of two disjoint dictionaries (`OpaBatchResults`, `OpaBatchErrors`). Walking the result meant zipping the two halves and remembering which keys appeared in which side. The new shape is a single dictionary of discriminated entries:

```csharp
var inputs = new Dictionary<string, object?>() {
    { "AAA", new Dictionary<string, object>() { { "subject", "alice" } } },
    { "BBB", new Dictionary<string, object>() { { "subject", "bob"   } } },
};

Dictionary<string, OpaBatchEntry<bool>> results =
    await opa.EvaluateBatch<bool>("authz/allow", inputs);

foreach (var (key, entry) in results) {
    if (entry.IsSuccess) Console.WriteLine($"{key}: {entry.Value}");
    else                 Console.WriteLine($"{key} failed: {entry.Error!.Message}");
}
```

Each `OpaBatchEntry<T>` carries `IsSuccess`, `Value`, `Error`, `StatusCode`, `DecisionId`, `Metrics`, `Provenance` fields. For callers who only want one side, the new `OpaBatchExtensions.Successes()` / `.Failures()` projections allow simulating the "two dictionaries" flow:

```csharp
IDictionary<string, bool>     successes = results.Successes();
IDictionary<string, OpaError> failures  = results.Failures();
```

The non-generic `EvaluateBatch` overload is removed. The new signature requires passing a type argument (e.g. `EvaluateBatch<bool>`, `EvaluateBatch<Dictionary<string, object>>`, etc). The input parameter has also been broadened from `Dictionary<string, Dictionary<string, object>>` to `IDictionary<string, object?>`, so any input value type per id is now accepted.

The 404-fallback behavior is unchanged. When an OPA server doesn't implement the `/v1/batch/data` endpoint, the client transparently switches doing a sequence of sequential queries, and reconstructs the same result shape. The fallback is then used for the lifetime of the client.

#### Low-level `OpaApiClient` and Speakeasy types removed

The entire `OpenPolicyAgent.Opa.OpenApi.*` namespace tree is gone, including:

- `OpaApiClient` — the low-level client class.
- All request and response wrapper types: `ExecutePolicyRequest`, `ExecutePolicyResponse`, `ExecutePolicyWithInputRequest`, `ExecuteBatchPolicyWithInputRequest`, etc.
- The discriminated-union types `Input`, `Result`, `Responses`, with their `CreateBoolean` / `CreateMapOfAny` / etc. helpers.
- The wrapper types `SuccessfulPolicyResponse`, `BatchSuccessfulPolicyEvaluation`, `BatchMixedResults`, and their `*WithStatusCode` siblings.

Migrate to the high-level `OpaClient`:

```csharp
// Before:
var sdk = new OpaApiClient(serverUrl: opaUrl);
var req = new ExecutePolicyWithInputRequest() {
    Path = "app/rbac",
    RequestBody = new ExecutePolicyWithInputRequestBody() {
        Input = Input.CreateMapOfAny(input),
    },
};
var res = await sdk.ExecutePolicyWithInputAsync(req);
var allow = res.SuccessfulPolicyResponse?.Result?.MapOfAny?["allow"];

// After:
var opa = new OpaClient(serverUrl: opaUrl);
var result = await opa.Evaluate<Dictionary<string, object>>("app/rbac", input);
var allow = result["allow"];
```

If you have a use case that genuinely needs lower-level access than `OpaClient` provides, please open an issue describing it.

#### `OpaError` and result types

`OpaError.HttpStatusCode` has been replaced by `OpaError.StatusCode` (`string?` is now `int?`). The corresponding serialized field is renamed from `http_status_code` to `status_code`. Code asserting on the previous string form needs to be updated:

```csharp
// Before:
Assert.Equal("500", err.HttpStatusCode);
// After:
Assert.Equal(500, err.StatusCode);
```

The non-generic `OpaResult` (which leaked the `Result` discriminated-union through) is removed entirely. `OpaResult<T>` (returned by `EvaluateWithMetadataAsync`) replaces it with a fully-typed value. `OpaBatchResults`, `OpaBatchErrors`, `OpaBatchResultGeneric<T>`, `OpaBatchInputs`, and the `DictionaryExtensions` helpers in `OpaBatchTypes.cs` are all removed. The new `Dictionary<string, OpaBatchEntry<T>>` shape provides the same functionality.

#### Exception type renames

The Speakeasy-emitted exception types (`ClientError`, `ServerError`, `BatchServerError`, `SDKException`, `UnhealthyServer`) are removed. Catch the new `OpaException` base type, or one of the subtypes documented above. The all-failures batch case that used to throw `BatchServerError` is now an `OpaServerException` whose `BatchQueryErrors` property carries the per-input details:

```csharp
// Before:
catch (BatchServerError bse) {
    foreach (var (id, srvErr) in bse.Responses!) { ... }
}
// After:
catch (OpaServerException ex) when (ex.BatchQueryErrors is not null) {
    foreach (var (id, opaErr) in ex.BatchQueryErrors) { ... }
}
```

Authored by @philipaconrad


## 1.6.6

This release updates the help text hints for Data Filters to point at the upstream OPA Compile API documentation.


## 1.6.2, 1.6.3, 1.6.4, 1.6.5

These contain release engineering improvements, including support for publishing Github Releases again.


## 1.6.1

This release contains release engineering fixes, with no significant code or dependency changes.


## 1.6.0

This release is the first official project release since the project was donated to the Open Policy Agent organization on Github.

### New NuGet package

The repo now publishes to [`OpenPolicyAgent.Opa`](https://www.nuget.org/packages/OpenPolicyAgent.Opa/) on NuGet, reflecting the change in project ownership.

### Compile API changes

This release includes breaking changes for the Compile API `GetFilters` APIs, and should be used with OPA v1.9.0 or later, and EOPA v1.44.0 or later.
These changes were needed in order to track upstream support of filter compilation landing in OPA.

-----

## Older Releases

## 2024-03-07 22:18:53
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.204.1 (2.279.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.0.3] .

## 2024-03-12 18:29:32
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.207.0 (2.280.6) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.0.4] .

## 2024-03-13 21:03:43
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.209.2 (2.281.2) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.0.5] .

## 2024-03-20 00:03:04
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.213.0 (2.283.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.1.0] .

## 2024-03-27 00:03:10
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.227.0 (2.291.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.1.1] .

## 2024-03-27 21:16:30
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.228.1 (2.292.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.2.0] .

## 2024-04-02 00:03:23
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.231.1 (2.295.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.3.0] .

## 2024-04-19 00:03:14
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.260.6 (2.311.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.6.0] .
### Releases
- [NuGet v0.6.0] https://www.nuget.org/packages/Styra.OpenApi/0.6.0 - .

## 2024-04-19 18:19:58
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.264.1 (2.312.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.6.0] .
### Releases
- [NuGet v0.6.0] https://www.nuget.org/packages/Styra.OpenApi/0.6.0 - .

## 2024-04-22 00:03:47
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.267.0 (2.312.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.6.0] .
### Releases
- [NuGet v0.6.0] https://www.nuget.org/packages/Styra.OpenApi/0.6.0 - .

## 2024-04-23 00:03:42
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.269.1 (2.312.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.6.0] .
### Releases
- [NuGet v0.6.0] https://www.nuget.org/packages/Styra.OpenApi/0.6.0 - .

## 2024-04-29 00:03:32
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.276.0 (2.314.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.0] .
### Releases
- [NuGet v0.7.0] https://www.nuget.org/packages/Styra.OpenApi/0.7.0 - .

## 2024-04-30 12:47:41
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.277.2 (2.317.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.2] .
### Releases
- [NuGet v0.7.2] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.2 - .

## 2024-04-30 17:57:17
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.277.4 (2.318.3) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.3] .
### Releases
- [NuGet v0.7.3] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.3 - .

## 2024-05-02 20:38:41
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.277.8 (2.319.10) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.4] .
### Releases
- [NuGet v0.7.4] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.4 - .

## 2024-05-02 20:52:18
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.277.8 (2.319.10) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.5] .
### Releases
- [NuGet v0.7.5] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.5 - .

## 2024-05-06 00:03:24
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.279.0 (2.322.5) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.6] .
### Releases
- [NuGet v0.7.6] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.6 - .

## 2024-05-08 00:02:59
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.283.1 (2.324.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.7.7] .
### Releases
- [NuGet v0.7.7] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.7.7 - .

## 2024-05-17 00:03:15
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.293.0 (2.332.4) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.8.0] .
### Releases
- [NuGet v0.8.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.8.0 - .

## 2024-05-24 00:04:20
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.295.2 (2.335.5) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v0.8.1] .
### Releases
- [NuGet v0.8.1] https://www.nuget.org/packages/Styra.Opa.OpenApi/0.8.1 - .

## 2024-06-04 19:41:50
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.299.7 (2.338.12) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.0.0] .
### Releases
- [NuGet v1.0.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.0.0 - .

## 2024-06-20 00:03:21
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.310.0 (2.347.4) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.0.1] .
### Releases
- [NuGet v1.0.1] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.0.1 - .

## 2024-06-21 00:03:15
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.313.0 (2.347.8) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.0.2] .
### Releases
- [NuGet v1.0.2] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.0.2 - .

## 2024-06-24 00:04:19
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.314.2 (2.349.6) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.1.0] .
### Releases
- [NuGet v1.1.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.1.0 - .

## 2024-06-27 00:03:34
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.321.0 (2.354.2) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.1.1] .
### Releases
- [NuGet v1.1.1] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.1.1 - .


## 2024-08-05 19:17:57
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.352.2 (2.385.2) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.2.0] .
### Releases
- [NuGet v1.2.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.2.0 - .

## 2024-08-19 16:29:28
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.376.0 (2.402.5) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.3.0] .
### Releases
- [NuGet v1.3.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.3.0 - .

## 2024-08-20 20:26:52
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.376.1 (2.402.5) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.3.4] .
### Releases
- [NuGet v1.3.4] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.3.4 - .

## 2024-09-04 00:03:42
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.390.1 (2.409.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.3.5] .
### Releases
- [NuGet v1.3.5] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.3.5 - .

## 2024-09-05 20:05:55
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.390.7 (2.409.8) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.3.6] .
### Releases
- [NuGet v1.3.6] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.3.6 - .

## 2024-09-16 17:18:51
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.397.2 (2.415.8) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.3.7] .
### Releases
- [NuGet v1.3.7] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.3.7 - .

## 2024-11-18 22:43:42
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.441.0 (2.460.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.4.1] .
### Releases
- [NuGet v1.4.1] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.4.1 - .

## 2025-02-19 20:03:04
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.495.1 (2.515.4) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.5.0] .
### Releases
- [NuGet v1.5.0] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.5.0 - .

## 2025-04-04 00:04:04
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.528.1 (2.565.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.5.1] .
### Releases
- [NuGet v1.5.1] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.5.1 - .

## 2025-04-11 00:04:00
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.531.2 (2.570.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.5.2] .
### Releases
- [NuGet v1.5.2] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.5.2 - .

## 2025-04-15 18:59:57
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.532.0 (2.578.0) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.5.3] .
### Releases
- [NuGet v1.5.3] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.5.3 - .

## 2025-04-24 21:47:22
### Changes
Based on:
- OpenAPI Doc  
- Speakeasy CLI 1.538.0 (2.591.1) https://github.com/speakeasy-api/speakeasy
### Generated
- [csharp v1.5.4] .
### Releases
- [NuGet v1.5.4] https://www.nuget.org/packages/Styra.Opa.OpenApi/1.5.4 - .
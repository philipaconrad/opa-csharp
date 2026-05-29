# OPA C# SDK

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![NuGet Version](https://img.shields.io/nuget/v/OpenPolicyAgent.Opa?style=flat&color=%2324b6e0)](https://www.nuget.org/packages/OpenPolicyAgent.Opa/)

> [!IMPORTANT]
> Reference documentation is available at <https://open-policy-agent.github.io/opa-csharp>

You can use the OPA C# SDK to connect to [Open Policy Agent](https://www.openpolicyagent.org/) and [EOPA](https://github.com/open-policy-agent/eopa) deployments.

## SDK Installation

### Nuget

```bash
dotnet add package OpenPolicyAgent.Opa
```

## SDK Example Usage

The following examples assume an OPA server at `http://localhost:8181` equipped with the following Rego policy in `authz.rego`:

```rego
package authz
import rego.v1

default allow := false
allow if input.subject == "alice"
```

and this `data.json`:

```json
{
  "roles": {
    "admin": ["read", "write"]
  }
}
```

### Simple Query

For a simple boolean response with input, use the SDK as follows:

```csharp
using OpenPolicyAgent.Opa;

string opaUrl = "http://localhost:8181";
OpaClient opa = new OpaClient(opaUrl);

var input = new Dictionary<string, object>() {
    {"subject", "alice"},
    {"action", "read"},
};

bool allowed = false;

try
{
    allowed = await opa.Check("authz/allow", input);
}
catch (OpaException e)
{
    Console.WriteLine("exception while making request against OPA: " + e);
}

Console.WriteLine("allowed: " + allowed);
```

<details>
  <summary>Result</summary>

```txt
allowed: True
```

</details>

### Simple Query with Output

The `.Evaluate()` method can be used instead of `.Check()` for non-boolean output types:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using OpenPolicyAgent.Opa;

var opaUrl = "http://localhost:8181";
var opa = new OpaClient(opaUrl);

var input = new Dictionary<string, string>() {
    {"subject", "alice"},
    {"action", "read"},
};

var result = new Dictionary<string, List<string>>();

try
{
    result = await opa.Evaluate<Dictionary<string, List<string>>>("roles", input);
}
catch (OpaException e)
{
    Console.WriteLine("exception while making request against OPA: " + e.Message);
}

Console.WriteLine("content of data.roles:");
foreach (var pair in result)
{
    Console.Write("  {0} => [ ", pair.Key);
    foreach (var item in pair.Value)
    {
        Console.Write("{0} ", item);
    }
    Console.WriteLine("]");
}
```

<details>
  <summary>Result</summary>

```txt
content of data.roles:
    admin => [ read write ]
```

</details>

### Default Rule

For evaluating the default rule (configured with your OPA service), use `EvaluateDefault`. `input` is optional, and left `null` in this example:

```csharp
using OpenPolicyAgent.Opa;

string opaUrl = "http://localhost:8181";
OpaClient opa = new OpaClient(opaUrl);

bool allowed = false;

try
{
    allowed = await opa.EvaluateDefault<bool>(input: null);
}
catch (OpaException e)
{
    Console.WriteLine("exception while making request against OPA: " + e);
}

Console.WriteLine("allowed: " + allowed);
```

<details>
  <summary>Result</summary>

```txt
allowed: False
```

</details>

### Batched Queries

EOPA supports executing many queries in a single request with the [Batch API][eopa-batch-api].

   [eopa-batch-api]: https://github.com/open-policy-agent/eopa/blob/main/docs/eopa/reference/api-reference/batch-api.md

The OPA C# SDK has native support for EOPA's batch API, with a fallback behavior of sequentially executing single queries if the Batch API is unavailable (such as with open source Open Policy Agent).

`EvaluateBatch<T>` returns a dictionary of `OpaBatchEntry<T>`, one entry per input id. Each entry is either a success (with the deserialized `Value`) or a failure (with `Error` populated) — discriminate via `IsSuccess`. The `Successes()` / `Failures()` extension helpers project the dictionary down to one side at a time when that's all the caller wants.

```csharp
using OpenPolicyAgent.Opa;

string opaUrl = "http://localhost:8181";
OpaClient opa = new OpaClient(opaUrl);

var inputs = new Dictionary<string, object?>() {
    { "AAA", new Dictionary<string, object>() { { "subject", "alice" }, { "action", "read"  } } },
    { "BBB", new Dictionary<string, object>() { { "subject", "bob"   }, { "action", "write" } } },
    { "CCC", new Dictionary<string, object>() { { "subject", "dave"  }, { "action", "read"  } } },
    { "DDD", new Dictionary<string, object>() { { "subject", "sybil" }, { "action", "write" } } },
};

Dictionary<string, OpaBatchEntry<bool>> results;
try
{
    results = await opa.EvaluateBatch<bool>("authz/allow", inputs);
}
catch (OpaException e)
{
    Console.WriteLine("exception while making request against OPA: " + e.Message);
    return;
}

Console.WriteLine("Query results, by key:");
foreach (var (key, entry) in results)
{
    if (entry.IsSuccess)
        Console.WriteLine("  {0} => {1}", key, entry.Value);
    else
        Console.WriteLine("  {0} => error: {1}", key, entry.Error!.Message);
}
```

<details>
  <summary>Result</summary>

```txt
Query results, by key:
    AAA => True
    BBB => False
    CCC => False
    DDD => False
```

</details>

### Using Custom Classes for Input and Output

Using the OPA C# SDK, it can be more natural to use custom class types as inputs and outputs to a policy, rather than `System.Collections.Dictionary` (or `Collections.List`). By default, the OPA C# SDK uses [`Newtonsoft.Json`](https://www.newtonsoft.com/json) to serialize and deserialize inputs and outputs JSON to the provided types — see the *Pluggable JSON serializer* section below for swapping in `System.Text.Json`.

In the example below, note:

- Using an `enum` for an input field
- Hiding the sensitive `UUID` with the `JsonIgnore` property
- Deserializing the query response to a `bool`

```csharp
using System;
using OpenPolicyAgent.Opa;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Application
{
    class Program
    {
        public enum ActionType
        {
            invalid,
            create,
            read,
            update,
            delete
        }

        private class CustomRBACObject
        {

            [JsonProperty("user")]
            public string User = "";

            [JsonProperty("action")]
            [JsonConverter(typeof(StringEnumConverter))]
            public ActionType Action = ActionType.invalid;

            [JsonIgnore]
            public string UUID = System.Guid.NewGuid().ToString();

            public CustomRBACObject() { }

            public CustomRBACObject(string user, ActionType action)
            {
                User = user;
                Action = action;
            }
        }

        static async Task<int> Main(string[] args)
        {
            string opaUrl = "http://localhost:8181";
            OpaClient opa = new OpaClient(opaUrl);

            var input = new CustomRBACObject("bob", ActionType.read);
            Console.WriteLine("The JSON that OPA will receive: {{\"input\": {0}}}", JsonConvert.SerializeObject(input));

            bool allowed = false;
            try
            {
                allowed = await opa.Evaluate<bool>("authz/allow", input);
            }
            catch (OpaException e)
            {
                Console.WriteLine("exception while making request against OPA: " + e.Message);
            }

            Console.WriteLine("allowed: " + allowed);
            return 0;
        }
    }
}
```

<details>
  <summary>Result</summary>

```txt
The JSON that OPA will receive: {"input": {"user":"bob","action":"read"}}
allowed: False
```

</details>

### Decision metadata

When a caller needs OPA's `decision_id`, query metrics, or bundle provenance alongside the policy result — for example, to correlate an allow/deny decision with a downstream side effect in an audit log — use `EvaluateWithMetadataAsync<T>`. It returns an `OpaResult<T>` carrying the deserialized value plus the metadata fields.

```csharp
using OpenPolicyAgent.Opa;

var opa = new OpaClient("http://localhost:8181");

var input = new Dictionary<string, object>() {
    { "subject", "alice" },
    { "action", "read" },
};

OpaResult<bool> result = await opa.EvaluateWithMetadataAsync<bool>(
    "authz/allow",
    input,
    provenance: true,
    metrics: false);

Console.WriteLine("allowed:     {0}", result.Value);
Console.WriteLine("decision id: {0}", result.DecisionId ?? "<none>");
if (result.Provenance is not null)
{
    Console.WriteLine("OPA version: {0}", result.Provenance.Version);
}
```

### Integrating logging with the OPA C# SDK

The OPA C# SDK uses opt-in, [compile-time source generated logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logger-message-generator), which can be integrated as a part of the overall logs of a larger application.

Here's a quick example:

```csharp
using Microsoft.Extensions.Logging;
using OpenPolicyAgent.Opa;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddConsole());
        ILogger<OpaClient> logger = factory.CreateLogger<OpaClient>();

        var opaURL = "http://localhost:8181";
        OpaClient opa = new OpaClient(opaURL, logger);

        logger.LogInformation("Initialized an OPA client for the OPA at: {Description}.", opaURL);

        var allow = await opa.Evaluate<bool>("this/rule/does/not/exist", false);

        return 0;
    }
}
```

<details>
    <summary>Result</summary>

```log
info: OpenPolicyAgent.Opa.OpaClient[0]
      Initialized an OPA client for the OPA at: http://localhost:8181.
warn: OpenPolicyAgent.Opa.OpaClient[2066302899]
      executing policy at 'this/rule/does/not/exist' succeeded, but OPA did not reply with a result
Unhandled exception. OpaException: executing policy at 'this/rule/does/not/exist' succeeded, but OPA did not reply with a result
    ...
```

</details>

## Configuration

### Server URL

The default `OpaClient()` constructor connects to `http://localhost:8181`. Override the server URL by passing a `serverUrl` argument:

```csharp
var opa = new OpaClient(serverUrl: "http://opa.example.internal:8181");
```

### Authentication: bearer token

To authenticate to a server that requires a bearer token, pass a `bearerTokenSource` callback. The callback is invoked once per request, so it can return a rotating token without needing to reconstruct the client:

```csharp
var opa = new OpaClient(
    serverUrl: "https://opa.example.internal:8181",
    bearerTokenSource: () => GetCurrentToken());
```

### Custom HttpClient

For full control over timeouts, custom `DelegatingHandler`s, mTLS, proxy configuration, or auth schemes other than bearer tokens, pass your own `HttpClient`:

```csharp
var handler = new SocketsHttpHandler {
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};
var http = new HttpClient(handler) {
    Timeout = TimeSpan.FromSeconds(30),
};

var opa = new OpaClient(serverUrl: "https://opa.example.internal:8181", httpClient: http);
```

When a custom `HttpClient` is supplied, the SDK does not dispose of it — the caller owns its lifecycle.

### Pluggable JSON serializer

The SDK ships with two implementations of the `IOpaSerializer` interface:

| Serializer | When to use |
| --- | --- |
| `NewtonsoftOpaSerializer` (default) | Default — used when no serializer is specified. Honors `Newtonsoft.Json.JsonSerializerSettings` and any custom `JsonConverter`s registered through them. |
| `SystemTextJsonOpaSerializer`       | Opt in if your project standardizes on `System.Text.Json`. Honors `JsonSerializerOptions`. |

```csharp
using OpenPolicyAgent.Opa;
using OpenPolicyAgent.Opa.Serialization;

var opa = new OpaClient(
    serverUrl: "http://localhost:8181",
    serializer: new SystemTextJsonOpaSerializer());
```

> [!NOTE]
> `GetFilters` / `GetMultipleFilters` currently still require Newtonsoft for response parsing because the `OpenPolicyAgent.Ucast.Linq` package ships Newtonsoft-specific `JsonConverter`s on its filter types. Other operations work end-to-end under either serializer.

## Error Handling

All exceptions thrown by the SDK derive from `OpaException`. Specialized subtypes discriminate the failure class so callers can pick a level of granularity that fits their use case:

| Subtype | Thrown for |
| --- | --- |
| `OpaTransportException`     | Network failures: DNS resolution, connect, read timeout, TLS handshake. `StatusCode` is always null. |
| `OpaPolicyException`        | HTTP 4xx — malformed query, invalid input, unknown path, batch endpoint not present on this server. The request will not succeed if retried unchanged. |
| `OpaServerException`        | HTTP 5xx — policy evaluation errors, internal errors. Often safe to retry. For batch endpoints that return 500 across all inputs, exposes the per-input details via `BatchQueryErrors`. |
| `OpaSerializationException` | Unexpected content type, malformed JSON, or a type-coercion failure when binding the policy result to the requested generic `T`. |

All subtypes inherit the rich properties on `OpaException`:

| Property     | Type    | Description |
| ------------ | ------- | ----------- |
| `StatusCode` | `int?`  | HTTP status code returned by OPA. Null for transport-level failures. |
| `Code`       | `string?` | OPA short-form error code, e.g. `"internal_error"`. |
| `DecisionId` | `string?` | Decision identifier supplied by OPA when decision logging is enabled. |
| `RawBody`    | `string?` | Raw HTTP response body, when available. |

Catch the base type to fail closed across any SDK error:

```csharp
try
{
    var allowed = await opa.Check("authz/allow", input);
    if (!allowed) DenyRequest();
}
catch (OpaException)
{
    DenyRequest(); // fail closed
}
```

Or discriminate on the specific failure class:

```csharp
try
{
    var allowed = await opa.Check("authz/allow", input);
}
catch (OpaServerException e) when (e.Code == "internal_error")
{
    // 5xx on the OPA side; probably worth a retry
}
catch (OpaPolicyException e)
{
    // 4xx; the request itself is malformed, retry won't help
}
catch (OpaTransportException)
{
    // network problem reaching OPA; trip the circuit breaker
}
```

## Community

For questions, discussions and announcements, please join
the OPA community on [Slack](https://slack.openpolicyagent.org/)!

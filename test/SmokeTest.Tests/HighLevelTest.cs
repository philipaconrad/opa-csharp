using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenPolicyAgent.Opa;
using OpenPolicyAgent.Opa.Filters;
using OpenPolicyAgent.Opa.Serialization;
using OpenPolicyAgent.Ucast.Linq;

namespace SmokeTest.Tests;

// Used to verify presence of log messages in tests.
public class ListLogger : ILogger<OpaClient>
{
  public List<string> Logs { get; } = [];

  IDisposable ILogger.BeginScope<TState>(TState state)
  {
    return null!;
  }

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
  {
    ArgumentNullException.ThrowIfNull(formatter);

    var message = formatter(state, exception);
    if (!string.IsNullOrEmpty(message))
    {
      Logs.Add(message);
    }
  }
}

// Note(philip): Run with `--logger "console;verbosity=detailed"` to see logged messages.
public class HighLevelTest : IClassFixture<OPAContainerFixture>, IClassFixture<EOPAContainerFixture>
{
  private readonly ITestOutputHelper _testOutput;
  public IContainer _containerOpa;
  public IContainer _containerEopa;

  private class CustomRBACInputObject
  {
    [JsonProperty("user")]
    public string? User;

    [JsonProperty("action")]
    public string? Action;

    [JsonProperty("object")]
    public string? Object;

    [JsonProperty("type")]
    public string? Type;

    [JsonIgnore]
    public string UUID = System.Guid.NewGuid().ToString();

    public CustomRBACInputObject() { }
  }

  public class CustomRBACOutputObject
  {
    [JsonProperty("allow")]
    public bool Allow = false;

    [JsonProperty("user_is_admin")]
    public bool? IsAdmin;

    [JsonProperty("user_is_granted")]
    public List<object>? Grants;

    public CustomRBACOutputObject() { }
  }

  public HighLevelTest(OPAContainerFixture opaFixture, EOPAContainerFixture eopaFixture, ITestOutputHelper output)
  {
    _containerOpa = opaFixture.GetContainer();
    _containerEopa = eopaFixture.GetContainer();
    _testOutput = output;
  }

  private string OpaUrl() => new UriBuilder(Uri.UriSchemeHttp, _containerOpa.Hostname, _containerOpa.GetMappedPublicPort(8181)).Uri.ToString();
  private string EOpaUrl() => new UriBuilder(Uri.UriSchemeHttp, _containerEopa.Hostname, _containerEopa.GetMappedPublicPort(8181)).Uri.ToString();

  private OpaClient GetOpaClient() => new(serverUrl: OpaUrl());
  private OpaClient GetOpaClientWithLogger(ILogger<OpaClient> logger) => new(serverUrl: OpaUrl(), logger: logger);
  private OpaClient GetEOpaClient() => new(serverUrl: EOpaUrl());

  // ---------- single-eval tests ----------

  [Fact]
  public async Task RBACCheckDictionaryTest()
  {
    var client = GetOpaClient();

    var allow = await client.Check("app/rbac/allow", new Dictionary<string, object>() {
      { "user", "alice" },
      { "action", "read" },
      { "object", "id123" },
      { "type", "dog" },
    });

    Assert.True(allow);
  }

  [Fact]
  public async Task RBACCheckNullTest()
  {
    var client = GetOpaClient();
    var allow = await client.Check("app/rbac/allow", null);
    Assert.False(allow);
  }

  [Fact]
  public async Task RBACCheckBoolTest()
  {
    var client = GetOpaClient();
    var allow = await client.Check("app/rbac/allow", true);
    Assert.False(allow);
  }

  [Fact]
  public async Task RBACCheckDoubleTest()
  {
    var client = GetOpaClient();
    var allow = await client.Check("app/rbac/allow", 42);
    Assert.False(allow);
  }

  [Fact]
  public async Task RBACCheckStringTest()
  {
    var client = GetOpaClient();
    var allow = await client.Check("app/rbac/allow", "alice");
    Assert.False(allow);
  }

  [Fact]
  public async Task RBACCheckListObjTest()
  {
    var client = GetOpaClient();
    var allow = await client.Check("app/rbac/allow", new List<object>() { "A", "B", "C", "D" });
    Assert.False(allow);
  }

  [Fact]
  public async Task RBACCheckAnonymousObjectTest()
  {
    var client = GetOpaClient();

    var allow = await client.Check("app/rbac/allow", new
    {
      user = "alice",
      action = "read",
      _object = "id123",
      type = "dog"
    });

    Assert.True(allow);
  }

  [Fact]
  public async Task DictionaryTypeCoerceTest()
  {
    var client = GetOpaClient();

    var input = new Dictionary<string, string>() {
      { "user", "alice" },
      { "action", "read" },
      { "object", "id123" },
      { "type", "dog" },
    };

    var result = new Dictionary<string, object>();

    try
    {
      result = await client.Evaluate<Dictionary<string, object>>("app/rbac", input);
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new Dictionary<string, object>() {
      { "allow", true },
      { "user_is_admin", true },
      { "user_is_granted", new List<object>()},
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
    Assert.Equal(expected.Count, result.Count);
  }

  [Fact]
  public async Task BooleanInputTypeCoerceTest()
  {
    var client = GetOpaClient();

    var input = false;

    var result = new Dictionary<string, object>();

    try
    {
      result = await client.Evaluate<Dictionary<string, object>>("app/rbac", input);
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new Dictionary<string, object>() {
      { "allow", false },
      { "user_is_granted", new List<object>()},
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
    Assert.Equal(expected.Count, result.Count);
  }

  [Fact]
  public async Task CustomClassInputTypeCoerceTest()
  {
    var client = GetOpaClient();

    var input = new CustomRBACInputObject() { User = "alice", Action = "read", Object = "id123", Type = "dog" };

    var result = new Dictionary<string, object>();

    try
    {
      result = await client.Evaluate<Dictionary<string, object>>("app/rbac", input);
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new Dictionary<string, object>() {
      { "allow", true },
      { "user_is_admin", true },
      { "user_is_granted", new List<object>()},
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
    Assert.Equal(expected.Count, result.Count);
  }

  [Fact]
  public async Task CustomClassOutputTypeCoerceTest()
  {
    var client = GetOpaClient();

    var input = new CustomRBACInputObject() { User = "alice", Action = "read", Object = "id123", Type = "dog" };

    var result = new CustomRBACOutputObject();
    try
    {
      var res = await client.Evaluate<CustomRBACOutputObject>("app/rbac", input);
      if (res is CustomRBACOutputObject value)
      {
        result = value;
      }
      else
      {
        Assert.Fail("Test did not deserialize to a custom C# type properly.");
      }
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new CustomRBACOutputObject()
    {
      Allow = true,
      IsAdmin = true,
      Grants = new List<object>(),
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
  }

  [Fact]
  public async Task BadOutputTypeCoerceTest()
  {
    var client = GetOpaClient();
    var input = new CustomRBACInputObject() { User = "alice", Action = "read", Object = "id123", Type = "dog" };
    await Assert.ThrowsAsync<OpaException>(async () => { var res = await client.Evaluate<bool>("app/rbac", input); });
  }

  [Fact]
  public async Task CustomClassInputTypeCoerceJsonSettingsTest()
  {
    var client = GetOpaClient();

    var input = new { a = "A", b = (object)null!, c = 2 };

    var result = new Dictionary<string, object>();

    try
    {
      result = await client.Evaluate<Dictionary<string, object>>("system/main", input, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new Dictionary<string, object>() {
      { "msg", "this is the default path" },
      { "echo", new Dictionary<string, object>() {
        { "a", "A" },
        { "c", 2 },
      } },
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
    Assert.Equal(expected.Count, result.Count);
  }

  [Fact]
  public async Task AnonymousObjectInputTypeCoerceTest()
  {
    var client = GetOpaClient();

    var input = new { user = "alice", action = "read", _object = "id123", type = "dog" };

    var result = new Dictionary<string, object>();

    try
    {
      result = await client.Evaluate<Dictionary<string, object>>("app/rbac", input);
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine("exception while making request against OPA: " + e.Message);
    }

    var expected = new Dictionary<string, object>() {
      { "allow", true },
      { "user_is_admin", true },
      { "user_is_granted", new List<object>()},
    };

    Assert.NotNull(result);
    Assert.Equivalent(expected, result);
    Assert.Equal(expected.Count, result.Count);
  }

  [Fact]
  public async Task EvaluateDefaultTest()
  {
    var client = GetOpaClient();

    var res = await client.EvaluateDefault<Dictionary<string, object>>(
      new Dictionary<string, object>() {
        { "hello", "world" },
      });

    Assert.Equal(new Dictionary<string, object>() { { "hello", "world" } }, res?.GetValueOrDefault("echo", ""));
  }

  [Fact]
  public async Task EvaluateDefaultWithAnonymousObjectInputTest()
  {
    var client = GetOpaClient();

    var res = await client.EvaluateDefault<Dictionary<string, object>>(new { hello = "world" });

    Assert.Equal(new Dictionary<string, object>() { { "hello", "world" } }, res?.GetValueOrDefault("echo", ""));
  }

  [Fact]
  public async Task EvaluateWithMetadataReturnsDecisionMetadataTest()
  {
    var client = GetOpaClient();

    var input = new Dictionary<string, object>() {
      { "user", "alice" },
      { "action", "read" },
      { "object", "id123" },
      { "type", "dog" },
    };

    var res = await client.EvaluateWithMetadataAsync<bool>("app/rbac/allow", input, provenance: true, metrics: false, ct: TestContext.Current.CancellationToken);

    Assert.True(res.Value);
    Assert.NotNull(res.Provenance);
    Assert.Equal(200, res.StatusCode);
  }

  // ---------- batch-eval tests ----------

  // Helper: assert each entry in the result dict represents a successful eval with the expected value.
  private static void AssertAllSuccess<T>(Dictionary<string, OpaBatchEntry<T>> results, IEnumerable<string> expectedKeys, T expectedValue)
  {
    foreach (var key in expectedKeys)
    {
      Assert.True(results.ContainsKey(key), $"missing key {key}");
      var entry = results[key];
      Assert.True(entry.IsSuccess, $"entry {key} should be a success");
      Assert.Equal(expectedValue, entry.Value);
      Assert.Null(entry.Error);
    }
    Assert.Equal(expectedKeys.Count(), results.Count);
  }

  // Helper: assert a single failure entry has the expected code/message and a 5xx status.
  private static void AssertFailure(OpaBatchEntry<Dictionary<string, object>> entry, string expectedCode, string expectedMessageSubstring)
  {
    Assert.False(entry.IsSuccess);
    Assert.Null(entry.Value);
    Assert.NotNull(entry.Error);
    Assert.Equal(expectedCode, entry.Error.Code);
    Assert.Contains(expectedMessageSubstring, entry.Error.Message);
  }

  [Fact]
  public async Task RBACBatchAllSuccessTest()
  {
    var client = GetEOpaClient();

    var goodInput = new Dictionary<string, object>() {
      { "user", "alice" },
      { "action", "read" },
      { "object", "id123" },
      { "type", "dog" }
    };

    var results = await client.EvaluateBatch<bool>("app/rbac/allow", new Dictionary<string, object?>() {
      {"AAA", goodInput },
      {"BBB", goodInput },
      {"CCC", goodInput },
    });

    AssertAllSuccess(results, ["AAA", "BBB", "CCC"], expectedValue: true);
    Assert.Empty(results.Failures());
  }

  [Fact]
  public async Task RBACBatchMixedTest()
  {
    var client = GetEOpaClient();

    var goodInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 1, 1} },
    };

    var badInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 2, 1} },
    };

    var results = await client.EvaluateBatch<Dictionary<string, object>>("testmod/condfail", new Dictionary<string, object?>() {
      {"AAA", badInput },
      {"BBB", goodInput },
      {"CCC", badInput },
    });

    var expectedSuccessValue = new Dictionary<string, object>() {
      {"p", new Dictionary<string, object>() { { "1", 2 }, { "3", 4 } } }
    };

    Assert.True(results["BBB"].IsSuccess);
    Assert.Equivalent(expectedSuccessValue, results["BBB"].Value);
    Assert.Equal(200, results["BBB"].StatusCode);

    AssertFailure(results["AAA"], "internal_error", "object insert conflict");
    Assert.Equal(500, results["AAA"].StatusCode);
    Assert.Equal(500, results["AAA"].Error!.StatusCode);
    AssertFailure(results["CCC"], "internal_error", "object insert conflict");
    Assert.Equal(500, results["CCC"].StatusCode);
  }

  [Fact]
  public async Task RBACBatchAllFailuresTest()
  {
    var client = GetEOpaClient();

    var badInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 2, 1} },
    };

    var results = await client.EvaluateBatch<Dictionary<string, object>>("testmod/condfail", new Dictionary<string, object?>() {
      {"AAA", badInput },
      {"BBB", badInput },
      {"CCC", badInput },
    });

    Assert.Empty(results.Successes());
    Assert.Equal(3, results.Failures().Count);
    foreach (var key in new[] { "AAA", "BBB", "CCC" })
    {
      AssertFailure(results[key], "internal_error", "object insert conflict");
    }
  }

  [Fact]
  public async Task RBACBatchAllSuccessFallbackTest()
  {
    var client = GetOpaClient();

    var goodInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 1, 1} },
    };

    var results = await client.EvaluateBatch<Dictionary<string, object>>("testmod/condfail", new Dictionary<string, object?>() {
      {"AAA", goodInput },
      {"BBB", goodInput },
      {"CCC", goodInput },
    });

    var expectedValue = new Dictionary<string, object>() {
      {"p", new Dictionary<string, object>() { { "1", 2 }, { "3", 4 } } }
    };

    foreach (var key in new[] { "AAA", "BBB", "CCC" })
    {
      Assert.True(results[key].IsSuccess);
      Assert.Equivalent(expectedValue, results[key].Value);
    }
    Assert.Empty(results.Failures());
  }

  [Fact]
  public async Task RBACBatchMixedFallbackTest()
  {
    var client = GetOpaClient();

    var goodInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 1, 1} },
    };

    var badInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 2, 1} },
    };

    var results = await client.EvaluateBatch<Dictionary<string, object>>("testmod/condfail", new Dictionary<string, object?>() {
      {"AAA", badInput },
      {"BBB", goodInput },
      {"CCC", badInput },
    });

    Assert.True(results["BBB"].IsSuccess);
    Assert.Equal(200, results["BBB"].StatusCode);

    // Note: vanilla OPA emits a different message than EOPA for the same condition.
    AssertFailure(results["AAA"], "internal_error", "error(s) occurred while evaluating query");
    Assert.Equal(500, results["AAA"].StatusCode);
    AssertFailure(results["CCC"], "internal_error", "error(s) occurred while evaluating query");
    Assert.Equal(500, results["CCC"].StatusCode);
  }

  [Fact]
  public async Task RBACBatchAllFailuresFallbackTest()
  {
    var client = GetOpaClient();

    var badInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 2, 1} },
    };

    var results = await client.EvaluateBatch<Dictionary<string, object>>("testmod/condfail", new Dictionary<string, object?>() {
      {"AAA", badInput },
      {"BBB", badInput },
      {"CCC", badInput },
    });

    Assert.Empty(results.Successes());
    foreach (var key in new[] { "AAA", "BBB", "CCC" })
    {
      AssertFailure(results[key], "internal_error", "error(s) occurred while evaluating query");
    }
  }

  // ---------- compile / data-filter tests ----------

  [Fact]
  public async Task GetFiltersTest()
  {
    var client = GetEOpaClient();

    var (filters, masks) = await client.GetFilters("filters/include", new Dictionary<string, object>
    {
        { "user", "caesar" },
        { "tenant", new Dictionary<string, object>
            {
                { "id", 2 },
                { "name", "acmecorp" }
            }
        },
    });

    Assert.Equivalent(new UCASTFilter(
      new UCASTNode(
        type: "compound",
        op: "or",
        value: new List<UCASTNode>
        {
          new(
            type: "compound",
            op: "and",
            value: new List<UCASTNode>
            {
              new(type: "field", op: "eq", field: "tickets.tenant", value: 2),
              new(type: "field", op: "eq", field: "users.name", value: "caesar")
            }
          ),
          new(
            type: "compound",
            op: "and",
            value: new List<UCASTNode>
            {
              new(type: "field", op: "eq", field: "tickets.tenant", value: 2),
              new(type: "field", op: "eq", field: "tickets.assignee", value: null),
              new(type: "field", op: "eq", field: "tickets.resolved", value: false)
            }
          )
        }
      )), filters);
    Assert.Equivalent(new Dictionary<string, object>() {
      { "tickets", new Dictionary<string, object>() {
        {"id", new MaskingFunc() {} },
      }},
    }, masks);
  }

  [Fact]
  public async Task GetFiltersWithMasksTest()
  {
    var client = GetEOpaClient();

    var (_, masks) = await client.GetFilters("filters/include", new Dictionary<string, object>()
    {
        { "user", "bob" },
        { "tenant", new Dictionary<string, object>
            {
                { "id", 2 },
                { "name", "acmecorp" }
            }
        },
    });

    Assert.Equivalent(new Dictionary<string, object>() {
      { "tickets", new Dictionary<string, object>() {
        {"id", new MaskingFunc() { Replace = new() {Value = "***"} } },
      }},
    }, masks);
  }

  [Fact]
  public async Task GetFiltersMultiTargetTest()
  {
    var client = GetEOpaClient();

    var (filters, masks) = await client.GetMultipleFilters("filters/include", new Dictionary<string, object>()
    {
        { "user", "caesar" },
        { "tenant", new Dictionary<string, object>
            {
                { "id", 2 },
                { "name", "acmecorp" }
            }
        },
    }, targetDialects: [
      TargetDialects.SqlPostgresql,
      TargetDialects.SqlMysql,
      TargetDialects.SqlSqlserver,
      TargetDialects.SqlSqlite,
      TargetDialects.UcastPrisma,
    ]);

    Assert.Equivalent(new UCASTFilter(
      new UCASTNode(
        type: "compound",
        op: "or",
        value: new List<UCASTNode>
        {
          new(
            type: "compound",
            op: "and",
            value: new List<UCASTNode>
            {
              new(type: "field", op: "eq", field: "tickets.tenant", value: 2),
              new(type: "field", op: "eq", field: "users.name", value: "caesar")
            }
          ),
          new(
            type: "compound",
            op: "and",
            value: new List<UCASTNode>
            {
              new(type: "field", op: "eq", field: "tickets.tenant", value: 2),
              new(type: "field", op: "eq", field: "tickets.assignee", value: null),
              new(type: "field", op: "eq", field: "tickets.resolved", value: false)
            }
          )
        }
      )), filters["ucast"]);
    Assert.Equal("WHERE ((tickets.tenant = E'2' AND users.name = E'caesar') OR (tickets.tenant = E'2' AND tickets.assignee IS NULL AND tickets.resolved = FALSE))",
                 filters["postgresql"].ToString());
    Assert.Equal("WHERE ((tickets.tenant = '2' AND users.name = 'caesar') OR (tickets.tenant = '2' AND tickets.assignee IS NULL AND tickets.resolved = FALSE))",
                 filters["mysql"].ToString());
    Assert.Equal("WHERE ((tickets.tenant = N'2' AND users.name = N'caesar') OR (tickets.tenant = N'2' AND tickets.assignee IS NULL AND tickets.resolved = FALSE))",
                 filters["sqlserver"].ToString());
    Assert.Equal("WHERE ((tickets.tenant = '2' AND users.name = 'caesar') OR (tickets.tenant = '2' AND tickets.assignee IS NULL AND tickets.resolved = FALSE))",
                 filters["sqlite"].ToString());
    Assert.Equivalent(new Dictionary<string, object>() {
      { "tickets", new Dictionary<string, object>() {
        {"id", new MaskingFunc() {} },
      }},
    }, masks);
  }

  [Fact]
  public async Task GetFiltersMultiTargetWithMasksTest()
  {
    var client = GetEOpaClient();

    var (_, masks) = await client.GetMultipleFilters("filters/include", new Dictionary<string, object>()
    {
        { "user", "bob" },
        { "tenant", new Dictionary<string, object>
            {
                { "id", 2 },
                { "name", "acmecorp" }
            }
        },
    }, targetDialects: [
      TargetDialects.SqlPostgresql,
      TargetDialects.SqlMysql,
      TargetDialects.SqlSqlserver,
      TargetDialects.SqlSqlite,
      TargetDialects.UcastPrisma,
    ]);

    Assert.Equivalent(new Dictionary<string, object>() {
      { "tickets", new Dictionary<string, object>() {
        {"id", new MaskingFunc() { Replace = new() {Value = "***"} } },
      }},
    }, masks);
  }

  // ---------- logging behavior ----------

  [Fact]
  public async Task LogsExistTest()
  {
    var logger = new ListLogger();
    var client = GetOpaClientWithLogger(logger);

    var badInput = new Dictionary<string, object>() {
      { "x", new List<int> {1, 1, 3} },
      { "y", new List<int> {1, 2, 1} },
    };

    try
    {
      var result = await client.Evaluate<bool>("testmod/condfail", badInput);
    }
    catch (OpaException e)
    {
      _testOutput.WriteLine(e.Message);
    }

    Assert.Single(logger.Logs);
    Assert.Contains("executing policy 'testmod/condfail' failed with exception: ", logger.Logs[0]);
  }

  // ---------- new ctor parameters: serializer, httpClient, bearerTokenSource ----------

  [Fact]
  public async Task SerializerSwapNewtonsoftAndStjProduceEquivalentResultsTest()
  {
    var input = new Dictionary<string, object>() {
      { "user", "alice" },
      { "action", "read" },
      { "object", "id123" },
      { "type", "dog" },
    };

    var newtonsoftClient = new OpaClient(serverUrl: OpaUrl(), serializer: new NewtonsoftOpaSerializer());
    var stjClient = new OpaClient(serverUrl: OpaUrl(), serializer: new SystemTextJsonOpaSerializer());

    var nsAllow = await newtonsoftClient.Check("app/rbac/allow", input);
    var stjAllow = await stjClient.Check("app/rbac/allow", input);

    Assert.True(nsAllow);
    Assert.True(stjAllow);
    Assert.Equal(nsAllow, stjAllow);
  }

  [Fact]
  public async Task BearerTokenSourceIsCalledPerRequestTest()
  {
    int calls = 0;
    string TokenSource()
    {
      calls++;
      return $"token-{calls}";
    }

    var client = new OpaClient(serverUrl: OpaUrl(), bearerTokenSource: TokenSource);

    // OPA happily ignores Authorization headers it doesn't require; the policy
    // should still evaluate normally. This test verifies the SDK invokes the
    // token source for each request without breaking the request flow.
    await client.Check("app/rbac/allow", new Dictionary<string, object>() {
      { "user", "alice" }, { "action", "read" }, { "object", "id123" }, { "type", "dog" }
    });
    await client.Check("app/rbac/allow", new Dictionary<string, object>() {
      { "user", "alice" }, { "action", "read" }, { "object", "id123" }, { "type", "dog" }
    });

    Assert.Equal(2, calls);
  }

  [Fact]
  public async Task CustomHttpClientIsUsedTest()
  {
    var observed = new List<string>();
    var handler = new ObservingHandler(observed);
    using var http = new HttpClient(handler);

    var client = new OpaClient(serverUrl: OpaUrl(), httpClient: http);
    await client.Check("app/rbac/allow", new Dictionary<string, object>() {
      { "user", "alice" }, { "action", "read" }, { "object", "id123" }, { "type", "dog" }
    });

    Assert.NotEmpty(observed);
    Assert.Contains(observed, u => u.Contains("/v1/data/app/rbac/allow"));
  }

  private sealed class ObservingHandler : DelegatingHandler
  {
    private readonly List<string> _seen;
    public ObservingHandler(List<string> seen) : base(new HttpClientHandler()) { _seen = seen; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      _seen.Add(request.RequestUri?.ToString() ?? "");
      return base.SendAsync(request, cancellationToken);
    }
  }
}

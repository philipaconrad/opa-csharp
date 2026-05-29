using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using STJ = System.Text.Json.Serialization;

namespace OpenPolicyAgent.Opa;

/// <summary>
/// Provenance metadata returned by OPA when <c>provenance=true</c> is set on a request.
/// Identifies the running OPA build and any active bundle revisions.
/// </summary>
public sealed class OpaProvenance
{
    [JsonProperty("version")]
    [STJ.JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonProperty("build_commit")]
    [STJ.JsonPropertyName("build_commit")]
    public string? BuildCommit { get; set; }

    [JsonProperty("build_timestamp")]
    [STJ.JsonPropertyName("build_timestamp")]
    public DateTimeOffset? BuildTimestamp { get; set; }

    [JsonProperty("build_host")]
    [STJ.JsonPropertyName("build_host")]
    public string? BuildHost { get; set; }

    [JsonProperty("bundles")]
    [STJ.JsonPropertyName("bundles")]
    public Dictionary<string, OpaBundleRevision>? Bundles { get; set; }
}

/// <summary>
/// A bundle revision identifier reported as part of <see cref="OpaProvenance"/>.
/// </summary>
public sealed class OpaBundleRevision
{
    [JsonProperty("revision")]
    [STJ.JsonPropertyName("revision")]
    public string Revision { get; set; } = "";
}

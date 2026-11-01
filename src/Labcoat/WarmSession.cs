using System.Text.Json.Nodes;

namespace Labcoat;

/// <summary>The warm test host process of a project and its connection.</summary>
internal sealed class WarmSession(MtpHost host, string fingerprint, bool tracing, OutputWatch watch)
{
    public MtpHost Host { get; } = host;
    /// <summary>Notices assemblies of the output folder rewritten in place while the process runs (OutputWatch.cs).</summary>
    public OutputWatch Watch { get; } = watch;
    /// <summary>Trace collection state the process was started with (its LABCOAT_OTLP_* environment).</summary>
    public bool Tracing { get; } = tracing;
    /// <summary>OutputFingerprints.Of of the output directory when the process was started.</summary>
    public string Fingerprint { get; } = fingerprint;
    public MtpConnection? Connection;
    /// <summary>Notification sink of the request in flight (requests are serialized by the op lock).</summary>
    public volatile Action<string, JsonNode?>? Handler;
}

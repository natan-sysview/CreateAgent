using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreateAgent;

[JsonConverter(typeof(AgentProviderIdJsonConverter))]
public readonly record struct AgentProviderId
{
    public AgentProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Provider id cannot be empty.", nameof(value));
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 128 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(
                static character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not '.' and not '-'))
        {
            throw new ArgumentException(
                "Provider id must match ^[a-z0-9][a-z0-9.-]{2,127}$.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum AgentRunState
{
    Created,
    Queued,
    Preparing,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
}

public enum AgentEventKind
{
    RunQueued,
    RunPreparing,
    RunStarted,
    ProviderEvent,
    SessionStarted,
    AgentMessage,
    ToolStarted,
    ToolCompleted,
    Progress,
    Warning,
    Error,
    RunCompleted,
}

public enum AgentOutputChannel
{
    StandardOutput,
    StandardError,
}

public enum AgentInputMode
{
    None,
    StandardInput,
}

public enum AgentOutputProtocol
{
    Text,
    JsonLines,
}

public enum AgentSessionMode
{
    New,
    Resume,
    [JsonStringEnumMemberName("continue-latest")]
    ContinueLatest,
    Fork,
    Ephemeral,
}

public enum AgentPerformanceMode
{
    Normal,
    Medium,
    Fast,
}

public enum AgentWorkspaceCopyMode
{
    Directory,
    Contents,
}

public enum AgentRunTermination
{
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
}

public enum AgentWorkspaceManifestStage
{
    [JsonStringEnumMemberName("before-execution")]
    BeforeExecution,
    [JsonStringEnumMemberName("after-execution")]
    AfterExecution,
}

public sealed record AgentReasoningOptions
{
    public string Effort { get; init; } = "medium";
}

public sealed record AgentSessionOptions
{
    public AgentSessionMode Mode { get; init; } = AgentSessionMode.New;

    public string? Id { get; init; }

}

public sealed record AgentWorkspaceSource
{
    [JsonRequired]
    public required string Path { get; init; }

    public AgentWorkspaceCopyMode Copy { get; init; } =
        AgentWorkspaceCopyMode.Directory;
}

public sealed record AgentWorkspaceRequest
{
    public IReadOnlyList<AgentWorkspaceSource> Sources { get; init; } = [];

    public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];
}

public sealed record AgentRunRequest
{
    [JsonRequired]
    public required string Name { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }

    public string Model { get; init; } = "provider-default";

    public AgentPerformanceMode Performance { get; init; } =
        AgentPerformanceMode.Normal;

    public AgentReasoningOptions Reasoning { get; init; } = new();

    public AgentSessionOptions Session { get; init; } = new();

    [JsonRequired]
    public required string Prompt { get; init; }

    public JsonElement? OutputSchema { get; init; }

    public AgentWorkspaceRequest Workspace { get; init; } = new();

    public IReadOnlyList<string> Tools { get; init; } = [];

    [JsonConverter(typeof(Iso8601TimeSpanJsonConverter))]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    public bool Debug { get; init; }
}

public sealed record AgentEvent(
    string RunId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    AgentEventKind Kind,
    string Name,
    string? Message = null,
    double? Progress = null,
    AgentOutputChannel? Stream = null,
    string? NativePayloadJson = null);

public sealed record AgentRunSnapshot(
    string RunId,
    AgentRunState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? WorkspacePath,
    string? LastMessage);

public sealed record AgentArtifact(
    string Name,
    string Path,
    string Sha256,
    long SizeBytes);

public sealed record AgentRunResult(
    string RunId,
    AgentRunState State,
    AgentRunTermination Termination,
    int? ExitCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string WorkspacePath,
    string AuditPath,
    string? FinalMessage,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AgentArtifact> Artifacts)
{
    public string AuditDirectory { get; init; } = string.Empty;

    public AgentRunExecutionPlan? ExecutionPlan { get; init; }

    public JsonElement? StructuredOutput { get; init; }
}

public sealed record AgentRunExecutionPlan(
    string SchemaVersion,
    string RunId,
    string RequestName,
    AgentProviderId ProviderId,
    string? ProviderVersion,
    string? RequestedModel,
    string PerformanceMode,
    string ReasoningEffort,
    AgentSessionMode SessionMode,
    string? RequestedSessionId,
    string CreateAgentVersion,
    string FrameworkDescription,
    string OperatingSystemDescription,
    string ProcessArchitecture,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset PreparedAtUtc,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> EnvironmentVariableNames,
    string WorkspacePath,
    string WorkingDirectory);

public sealed record AgentProviderCapabilities(
    AgentProviderId ProviderId,
    string? Version,
    bool IsAvailable,
    bool SupportsNonInteractiveExecution,
    bool SupportsStructuredEvents,
    bool SupportsSessionResume,
    bool SupportsNativeSkills,
    bool SupportsProviderInstructions,
    bool SupportsModelSelection,
    IReadOnlySet<string> SupportedPerformanceModes,
    IReadOnlySet<string> SupportedReasoningEfforts,
    string? Diagnostic = null);

public sealed record AgentWorkspace(
    string RunId,
    string RunDirectory,
    string WorkspaceDirectory,
    string WorkingDirectory,
    string AuditDirectory,
    string ResultPath);

public sealed record AgentCliLaunchPlan(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    AgentInputMode InputMode,
    AgentOutputProtocol OutputProtocol,
    long MaxOutputBytes = 52_428_800)
{
    public IReadOnlyList<string> TemporaryFiles { get; init; } = [];
}

public sealed record AgentProcessOutput(
    long Sequence,
    DateTimeOffset TimestampUtc,
    AgentOutputChannel Stream,
    string Text);

public sealed record AgentProcessResult(
    int ExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc);

public sealed record AgentDecodedEvent(
    AgentEventKind Kind,
    string Name,
    string? Message = null,
    double? Progress = null,
    AgentOutputChannel? Stream = null,
    string? NativePayloadJson = null);

public sealed record FailureBundle(
    string ArchivePath,
    string Sha256,
    long SizeBytes);

public sealed record CreateAgentOptions
{
    public required string StorageRoot { get; init; }

    public int MaxConcurrentRuns { get; init; } =
        Math.Clamp(Environment.ProcessorCount, 1, 4);

    public int EventChannelCapacity { get; init; } = 1024;

    public int ProcessOutputChannelCapacity { get; init; } = 1024;

    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxOutputBytes { get; init; } = 52_428_800;

    public bool RetainSuccessfulWorkspace { get; init; } = true;

    public bool QuarantineOnFailure { get; init; } = true;

    public IReadOnlyList<string> SecretsToRedact { get; init; } = [];
}

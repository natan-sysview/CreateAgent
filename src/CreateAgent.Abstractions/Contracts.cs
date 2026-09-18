namespace CreateAgent;

public interface IAgentWorkerManager : IAsyncDisposable
{
    Task<IAgentRunHandle> StartAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    bool TryGetRun(string runId, out IAgentRunHandle? handle);
}

public interface IAgentRunHandle
{
    string RunId { get; }

    AgentRunState State { get; }

    IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);

    Task<AgentRunSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        string reason,
        CancellationToken cancellationToken = default);

    Task<AgentRunResult> Completion { get; }
}

public interface IAgentCliAdapter
{
    AgentProviderId ProviderId { get; }

    Task<AgentProviderCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default);

    Task MaterializeAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<AgentCliLaunchPlan> CreateLaunchPlanAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default);

    AgentDecodedEvent DecodeOutput(AgentProcessOutput output);
}

public interface IAgentProcessRunner
{
    Task<AgentProcessResult> RunAsync(
        AgentCliLaunchPlan launchPlan,
        string? standardInput,
        Func<AgentProcessOutput, CancellationToken, ValueTask> onOutput,
        CancellationToken cancellationToken = default);
}

public interface IAgentWorkspaceProvider
{
    Task<AgentWorkspace> PrepareAsync(
        string runId,
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentWorkspace> PrepareResumeAsync(
        string runId,
        AgentRunRequest request,
        string workspacePath,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AgentWorkspace>(
            new NotSupportedException(
                "This workspace provider does not support session resume."));

    Task CleanupSuccessfulWorkspaceAsync(
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default);
}

public interface IAgentSessionLease : IAsyncDisposable
{
    AgentSessionRecord Session { get; }
}

public interface IAgentSessionStore
{
    Task SaveAsync(
        AgentSessionRecord session,
        CancellationToken cancellationToken = default);

    Task<IAgentSessionLease> AcquireAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public interface IAgentRunAudit : IAsyncDisposable
{
    string AuditPath { get; }

    string AuditDirectory { get; }

    Task WriteRequestAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    Task WriteEventAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default);

    Task CaptureWorkspaceManifestAsync(
        AgentWorkspaceManifestStage stage,
        CancellationToken cancellationToken = default);

    Task WriteResultAsync(
        AgentRunResult result,
        CancellationToken cancellationToken = default);
}

public interface IAgentRunAuditFactory
{
    Task<IAgentRunAudit> CreateAsync(
        AgentWorkspace workspace,
        bool debug,
        IReadOnlyList<string> secretsToRedact,
        CancellationToken cancellationToken = default);
}

public interface IAgentFailureBundler
{
    Task<FailureBundle> BundleAsync(
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default);
}

public interface ISecretRedactor
{
    string Redact(string value, IReadOnlyList<string> secrets);
}

public sealed class CreateAgentValidationException : Exception
{
    public CreateAgentValidationException(IReadOnlyList<string> errors)
        : base($"CreateAgent request is invalid: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public sealed class AgentProviderUnavailableException : Exception
{
    public AgentProviderUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class AgentSessionUnavailableException : Exception
{
    public AgentSessionUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class AgentSessionBusyException : Exception
{
    public AgentSessionBusyException(string message)
        : base(message)
    {
    }

    public AgentSessionBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

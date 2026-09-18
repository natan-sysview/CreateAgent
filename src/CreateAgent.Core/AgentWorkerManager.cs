using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CreateAgent.Core;

public sealed class AgentWorkerManager : IAgentWorkerManager, IDisposable
{
    private readonly Dictionary<string, IAgentCliAdapter> _adapters;
    private readonly IAgentProcessRunner _processRunner;
    private readonly IAgentWorkspaceProvider _workspaceProvider;
    private readonly IAgentRunAuditFactory _auditFactory;
    private readonly IAgentFailureBundler _failureBundler;
    private readonly IAgentSessionStore _sessionStore;
    private readonly CreateAgentOptions _options;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentDictionary<string, AgentRunHandle> _runs =
        new(StringComparer.Ordinal);
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private bool _disposed;

    public AgentWorkerManager(
        IEnumerable<IAgentCliAdapter> adapters,
        IAgentProcessRunner processRunner,
        IAgentWorkspaceProvider workspaceProvider,
        IAgentRunAuditFactory auditFactory,
        IAgentFailureBundler failureBundler,
        CreateAgentOptions options,
        IAgentSessionStore? sessionStore = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(auditFactory);
        ArgumentNullException.ThrowIfNull(failureBundler);
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxConcurrentRuns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxConcurrentRuns must be greater than zero.");
        }

        if (options.EventChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "EventChannelCapacity must be greater than zero.");
        }

        if (options.QueueTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "QueueTimeout must be greater than zero.");
        }

        if (options.MaxOutputBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxOutputBytes must be at least 1024.");
        }

        _adapters = new Dictionary<string, IAgentCliAdapter>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            _adapters.Add(adapter.ProviderId.Value, adapter);
            var shortName = adapter.ProviderId.Value.Split('.')[^1];
            _adapters.TryAdd(shortName, adapter);
        }
        _processRunner = processRunner;
        _workspaceProvider = workspaceProvider;
        _auditFactory = auditFactory;
        _failureBundler = failureBundler;
        _sessionStore = sessionStore ?? new InMemoryAgentSessionStore();
        _options = options;
        _concurrencyGate = new SemaphoreSlim(
            options.MaxConcurrentRuns,
            options.MaxConcurrentRuns);
    }

    public async Task<IAgentRunHandle> StartAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRunRequestValidation.Validate(request);

        if (!_adapters.ContainsKey(request.ProviderName))
        {
            throw new AgentProviderUnavailableException(
                $"No adapter is registered for provider '{request.ProviderName}'.");
        }

        AgentRunHandle handle;
        lock (_disposeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var runId = CreateRunId();
            handle = new AgentRunHandle(runId, _options.EventChannelCapacity);
            if (!_runs.TryAdd(runId, handle))
            {
                handle.Dispose();
                throw new InvalidOperationException($"Could not register run '{runId}'.");
            }
        }

        handle.SetState(AgentRunState.Queued);
        var queuedEvent = handle.CreateEvent(
            new AgentDecodedEvent(
                AgentEventKind.RunQueued,
                "run.queued",
                "Run accepted and queued."));
        await handle.PublishAsync(queuedEvent, cancellationToken).ConfigureAwait(false);

        _ = ExecuteRunAsync(handle, request);
        return handle;
    }

    public bool TryGetRun(string runId, out IAgentRunHandle? handle)
    {
        if (_runs.TryGetValue(runId, out var concrete))
        {
            handle = concrete;
            return true;
        }

        handle = null;
        return false;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        foreach (var handle in _runs.Values)
        {
            if (!handle.Completion.IsCompleted)
            {
                await handle.CancelAsync("CreateAgent manager disposed.")
                    .ConfigureAwait(false);
            }
        }

        await Task.WhenAll(_runs.Values.Select(static handle => handle.Completion))
            .ConfigureAwait(false);

        foreach (var handle in _runs.Values)
        {
            handle.Dispose();
        }

        _concurrencyGate.Dispose();
    }

    private async Task ExecuteRunAsync(
        AgentRunHandle handle,
        AgentRunRequest request)
    {
        AgentWorkspace? workspace = null;
        IAgentRunAudit? audit = null;
        AgentProcessResult? processResult = null;
        AgentRunExecutionPlan? executionPlan = null;
        AgentCliLaunchPlan? launchPlan = null;
        JsonElement? structuredOutput = null;
        IAgentSessionLease? sessionLease = null;
        var errors = new List<string>();
        var artifacts = new List<AgentArtifact>();
        var gateEntered = false;
        var state = AgentRunState.Failed;
        var termination = AgentRunTermination.Failed;

        try
        {
            if (!await _concurrencyGate
                    .WaitAsync(_options.QueueTimeout, handle.ExecutionToken)
                    .ConfigureAwait(false))
            {
                throw new AgentQueueTimeoutException(
                    $"Run exceeded queue timeout of {_options.QueueTimeout}.");
            }

            gateEntered = true;

            handle.SetState(AgentRunState.Preparing);
            var adapter = _adapters[request.ProviderName];
            var capabilities = await adapter.ProbeAsync(handle.ExecutionToken)
                .ConfigureAwait(false);
            if (!capabilities.IsAvailable ||
                !capabilities.SupportsNonInteractiveExecution)
            {
                throw new AgentProviderUnavailableException(
                    capabilities.Diagnostic ??
                    $"Provider '{adapter.ProviderId}' is not available for non-interactive execution.");
            }

            if (request.Session.Mode == AgentSessionMode.Resume)
            {
                if (!capabilities.SupportsSessionResume)
                {
                    throw new InvalidOperationException(
                        $"Provider '{adapter.ProviderId}' does not support session resume.");
                }

                sessionLease = await _sessionStore
                    .AcquireAsync(
                        adapter.ProviderId,
                        request.Session.Id!,
                        handle.ExecutionToken)
                    .ConfigureAwait(false);
                handle.SetSessionId(sessionLease.Session.SessionId);
                workspace = await _workspaceProvider
                    .PrepareResumeAsync(
                        handle.RunId,
                        request,
                        sessionLease.Session.WorkspacePath,
                        handle.ExecutionToken)
                    .ConfigureAwait(false);
            }
            else
            {
                workspace = await _workspaceProvider
                    .PrepareAsync(handle.RunId, request, handle.ExecutionToken)
                    .ConfigureAwait(false);
            }
            handle.SetWorkspace(workspace.WorkspaceDirectory);

            audit = await _auditFactory
                .CreateAsync(
                    workspace,
                    request.Debug,
                    _options.SecretsToRedact,
                    handle.ExecutionToken)
                .ConfigureAwait(false);
            await audit.WriteRequestAsync(request, handle.ExecutionToken)
                .ConfigureAwait(false);

            await EmitAsync(
                    handle,
                    audit,
                    new AgentDecodedEvent(
                        AgentEventKind.RunPreparing,
                        "run.preparing",
                        "Provider validated and workspace prepared."),
                    handle.ExecutionToken)
                .ConfigureAwait(false);

            await adapter.MaterializeAsync(request, workspace, handle.ExecutionToken)
                .ConfigureAwait(false);
            launchPlan = await adapter
                .CreateLaunchPlanAsync(request, workspace, handle.ExecutionToken)
                .ConfigureAwait(false);
            launchPlan = launchPlan with { MaxOutputBytes = _options.MaxOutputBytes };
            var prompt = BuildPrompt(request);
            executionPlan = CreateExecutionPlan(
                handle,
                request,
                capabilities,
                workspace,
                launchPlan);
            if (audit is not null)
            {
                await audit.CaptureWorkspaceManifestAsync(
                        AgentWorkspaceManifestStage.BeforeExecution,
                        handle.ExecutionToken)
                    .ConfigureAwait(false);
            }

            handle.SetState(AgentRunState.Running);
            await EmitAsync(
                    handle,
                    audit,
                    new AgentDecodedEvent(
                        AgentEventKind.RunStarted,
                        "run.started",
                        $"Provider '{adapter.ProviderId}' started."),
                    handle.ExecutionToken)
                .ConfigureAwait(false);

            using var timeoutCancellation = new CancellationTokenSource(request.Timeout);
            using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                handle.ExecutionToken,
                timeoutCancellation.Token);

            try
            {
                processResult = await _processRunner
                    .RunAsync(
                        launchPlan,
                        launchPlan.InputMode == AgentInputMode.StandardInput ? prompt : null,
                        async (output, token) =>
                        {
                            var decoded = adapter.DecodeOutput(output);
                            await CaptureSessionAsync(
                                    handle,
                                    request,
                                    adapter.ProviderId,
                                    workspace,
                                    decoded,
                                    token)
                                .ConfigureAwait(false);
                            await EmitAsync(handle, audit, decoded, token).ConfigureAwait(false);
                        },
                        executionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeoutCancellation.IsCancellationRequested &&
                      !handle.ExecutionToken.IsCancellationRequested)
            {
                state = AgentRunState.TimedOut;
                termination = AgentRunTermination.TimedOut;
                errors.Add($"Run exceeded timeout of {request.Timeout}.");
            }

            if (state != AgentRunState.TimedOut)
            {
                handle.ExecutionToken.ThrowIfCancellationRequested();
                if (processResult?.ExitCode == 0)
                {
                    if (string.IsNullOrWhiteSpace(handle.FinalMessage))
                    {
                        throw new InvalidOperationException(
                            "Provider completed without the required final agent output.");
                    }

                    if (request.OutputSchema is not null)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(handle.FinalMessage);
                            structuredOutput = document.RootElement.Clone();
                        }
                        catch (JsonException exception)
                        {
                            throw new InvalidOperationException(
                                "Provider final output is not valid JSON.",
                                exception);
                        }
                    }

                    state = AgentRunState.Succeeded;
                    termination = AgentRunTermination.Succeeded;
                }
                else
                {
                    state = AgentRunState.Failed;
                    termination = AgentRunTermination.Failed;
                    errors.Add($"Provider process exited with code {processResult?.ExitCode}.");
                }
            }
        }
        catch (AgentQueueTimeoutException exception)
        {
            state = AgentRunState.TimedOut;
            termination = AgentRunTermination.TimedOut;
            errors.Add(exception.Message);
        }
        catch (OperationCanceledException)
        {
            state = AgentRunState.Cancelled;
            termination = AgentRunTermination.Cancelled;
            errors.Add(handle.CancellationReason ?? "Run cancelled.");
        }
        catch (Exception exception)
        {
            state = AgentRunState.Failed;
            termination = AgentRunTermination.Failed;
            errors.Add(exception.Message);

            if (audit is not null)
            {
                try
                {
                    await EmitAsync(
                            handle,
                            audit,
                            new AgentDecodedEvent(
                                AgentEventKind.Error,
                                "run.error",
                                exception.Message),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception auditException)
                {
                    errors.Add(
                        $"Audit error event could not be persisted: {auditException.Message}");
                }
            }
        }
        finally
        {
            if (launchPlan is not null)
            {
                foreach (var temporaryFile in launchPlan.TemporaryFiles)
                {
                    try
                    {
                        if (Directory.Exists(temporaryFile))
                        {
                            Directory.Delete(temporaryFile, recursive: true);
                        }
                        else
                        {
                            File.Delete(temporaryFile);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add(
                            $"Temporary provider file could not be deleted: {exception.Message}");
                        state = AgentRunState.Failed;
                        termination = AgentRunTermination.Failed;
                    }
                }
            }

            if (gateEntered)
            {
                _concurrencyGate.Release();
            }
        }

        if (audit is not null && workspace is not null)
        {
            try
            {
                await audit.CaptureWorkspaceManifestAsync(
                        AgentWorkspaceManifestStage.AfterExecution,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(
                    $"Workspace file changes could not be persisted: {exception.Message}");
                if (state == AgentRunState.Succeeded)
                {
                    state = AgentRunState.Failed;
                    termination = AgentRunTermination.Failed;
                }
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        var workspacePath = workspace?.WorkspaceDirectory ?? string.Empty;
        var auditPath = audit?.AuditPath ?? string.Empty;
        var snapshot = await handle.GetSnapshotAsync().ConfigureAwait(false);
        var result = new AgentRunResult(
            handle.RunId,
            state,
            termination,
            processResult?.ExitCode,
            handle.CreatedAtUtc,
            processResult?.StartedAtUtc ?? snapshot.StartedAtUtc,
            finishedAt,
            workspacePath,
            auditPath,
            request.OutputSchema is null ? handle.FinalMessage : null,
            errors,
            artifacts)
        {
            SessionId = handle.SessionId,
            AuditDirectory = audit?.AuditDirectory ?? string.Empty,
            ExecutionPlan = executionPlan,
            StructuredOutput = structuredOutput,
        };

        try
        {
            if (audit is not null)
            {
                await EmitAsync(
                        handle,
                        audit,
                        new AgentDecodedEvent(
                            AgentEventKind.RunCompleted,
                            "run.completed",
                            $"Run completed with state {state}."),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                await audit.WriteResultAsync(result, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                var completedEvent = handle.CreateEvent(
                    new AgentDecodedEvent(
                        AgentEventKind.RunCompleted,
                        "run.completed",
                        $"Run completed with state {state}."));
                await handle.PublishAsync(completedEvent, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (workspace is not null &&
                state != AgentRunState.Succeeded &&
                _options.QuarantineOnFailure)
            {
                try
                {
                    var bundle = await _failureBundler
                        .BundleAsync(workspace, CancellationToken.None)
                        .ConfigureAwait(false);
                    artifacts.Add(
                        new AgentArtifact(
                            "failure-workspace",
                            bundle.ArchivePath,
                            bundle.Sha256,
                            bundle.SizeBytes));
                    result = result with { Artifacts = artifacts };
                    if (audit is not null)
                    {
                        await audit.WriteResultAsync(result, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add($"Failure archive could not be created: {exception.Message}");
                    result = result with { Errors = errors };
                    if (audit is not null)
                    {
                        await audit.WriteResultAsync(result, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }

            if (workspace is not null &&
                state == AgentRunState.Succeeded &&
                !_options.RetainSuccessfulWorkspace)
            {
                if (!string.IsNullOrWhiteSpace(handle.SessionId))
                {
                    await _sessionStore
                        .DeleteAsync(
                            _adapters[request.ProviderName].ProviderId,
                            handle.SessionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                await _workspaceProvider
                    .CleanupSuccessfulWorkspaceAsync(workspace, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            errors.Add($"Run finalization failed: {exception.Message}");
            result = result with
            {
                State = AgentRunState.Failed,
                Termination = AgentRunTermination.Failed,
                Errors = errors,
                Artifacts = artifacts,
            };
        }
        finally
        {
            if (audit is not null)
            {
                try
                {
                    await audit.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add($"Audit disposal failed: {exception.Message}");
                    result = result with
                    {
                        State = AgentRunState.Failed,
                        Termination = AgentRunTermination.Failed,
                        Errors = errors,
                    };
                }
            }

            if (sessionLease is not null)
            {
                try
                {
                    await sessionLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    errors.Add($"Session lease disposal failed: {exception.Message}");
                    result = result with
                    {
                        State = AgentRunState.Failed,
                        Termination = AgentRunTermination.Failed,
                        Errors = errors,
                    };
                }
            }

            handle.Complete(result);
        }
    }

    private static string BuildPrompt(AgentRunRequest request)
    {
        if (request.Workspace.ReadOnlyPaths.Count == 0)
        {
            return request.Prompt;
        }

        return string.Concat(
            request.Prompt,
            "\n\nRead-only reference paths available for this task. Read them when needed, but never modify them:\n",
            JsonSerializer.Serialize(request.Workspace.ReadOnlyPaths));
    }

    private static async Task EmitAsync(
        AgentRunHandle handle,
        IAgentRunAudit? audit,
        AgentDecodedEvent decoded,
        CancellationToken cancellationToken)
    {
        var agentEvent = handle.CreateEvent(decoded);
        if (audit is not null)
        {
            await audit.WriteEventAsync(agentEvent, cancellationToken).ConfigureAwait(false);
        }

        await handle.PublishAsync(agentEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureSessionAsync(
        AgentRunHandle handle,
        AgentRunRequest request,
        AgentProviderId providerId,
        AgentWorkspace workspace,
        AgentDecodedEvent decoded,
        CancellationToken cancellationToken)
    {
        if (decoded.Kind != AgentEventKind.SessionStarted)
        {
            return;
        }

        var sessionId = decoded.Message;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' emitted a session event without an identifier.");
        }

        if (request.Session.Mode == AgentSessionMode.Resume &&
            !string.Equals(request.Session.Id, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' resumed session '{sessionId}' instead of requested session '{request.Session.Id}'.");
        }

        handle.SetSessionId(sessionId);
        if (request.Session.Mode == AgentSessionMode.New)
        {
            await _sessionStore
                .SaveAsync(
                    new AgentSessionRecord(
                        sessionId,
                        providerId,
                        workspace.WorkspaceDirectory),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string CreateRunId() =>
        $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";

    private static AgentRunExecutionPlan CreateExecutionPlan(
        AgentRunHandle handle,
        AgentRunRequest request,
        AgentProviderCapabilities capabilities,
        AgentWorkspace workspace,
        AgentCliLaunchPlan launchPlan) =>
        new(
            "1.0",
            handle.RunId,
            request.Name,
            capabilities.ProviderId,
            capabilities.Version,
            request.Model,
            request.Performance.ToString().ToLowerInvariant(),
            request.Reasoning.Effort,
            request.Session.Mode,
            request.Session.Id,
            typeof(AgentWorkerManager).Assembly.GetName().Version?.ToString() ??
            "unknown",
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            handle.CreatedAtUtc,
            DateTimeOffset.UtcNow,
            launchPlan.ExecutablePath,
            launchPlan.Arguments,
            launchPlan.Environment.Keys.Order(StringComparer.Ordinal).ToArray(),
            workspace.WorkspaceDirectory,
            launchPlan.WorkingDirectory);
}

internal sealed class AgentQueueTimeoutException : Exception
{
    public AgentQueueTimeoutException(string message)
        : base(message)
    {
    }
}

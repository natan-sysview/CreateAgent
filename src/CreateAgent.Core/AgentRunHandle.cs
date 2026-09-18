using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CreateAgent.Core;

internal sealed class AgentRunHandle : IAgentRunHandle, IDisposable
{
    private readonly Channel<AgentEvent> _events;
    private readonly CancellationTokenSource _runCancellation = new();
    private readonly TaskCompletionSource<AgentRunResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _snapshotLock = new();
    private int _state = (int)AgentRunState.Created;
    private long _sequence;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _finishedAtUtc;
    private string? _workspacePath;
    private string? _lastMessage;
    private string? _finalMessage;
    private string? _sessionId;
    private string? _cancellationReason;

    public AgentRunHandle(string runId, int eventChannelCapacity)
    {
        RunId = runId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        _events = Channel.CreateBounded<AgentEvent>(
            new BoundedChannelOptions(eventChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
    }

    public string RunId { get; }

    public AgentRunState State => (AgentRunState)Volatile.Read(ref _state);

    public DateTimeOffset CreatedAtUtc { get; }

    public CancellationToken ExecutionToken => _runCancellation.Token;

    public string? CancellationReason
    {
        get
        {
            lock (_snapshotLock)
            {
                return _cancellationReason;
            }
        }
    }

    public string? FinalMessage
    {
        get
        {
            lock (_snapshotLock)
            {
                return _finalMessage;
            }
        }
    }

    public string? SessionId
    {
        get
        {
            lock (_snapshotLock)
            {
                return _sessionId;
            }
        }
    }

    public Task<AgentRunResult> Completion => _completion.Task;

    public async IAsyncEnumerable<AgentEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var agentEvent in _events.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return agentEvent;
        }
    }

    public Task<AgentRunSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_snapshotLock)
        {
            return Task.FromResult(
                new AgentRunSnapshot(
                    RunId,
                    State,
                    CreatedAtUtc,
                    _startedAtUtc,
                    _finishedAtUtc,
                    _workspacePath,
                    _lastMessage));
        }
    }

    public Task CancelAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_snapshotLock)
        {
            _cancellationReason ??= string.IsNullOrWhiteSpace(reason)
                ? "Cancelled by consumer."
                : reason.Trim();
        }

        _runCancellation.Cancel();
        return Task.CompletedTask;
    }

    public void SetState(AgentRunState state)
    {
        Volatile.Write(ref _state, (int)state);

        lock (_snapshotLock)
        {
            if (state == AgentRunState.Running)
            {
                _startedAtUtc ??= DateTimeOffset.UtcNow;
            }

            if (state is AgentRunState.Succeeded
                or AgentRunState.Failed
                or AgentRunState.Cancelled
                or AgentRunState.TimedOut)
            {
                _finishedAtUtc ??= DateTimeOffset.UtcNow;
            }
        }
    }

    public void SetWorkspace(string workspacePath)
    {
        lock (_snapshotLock)
        {
            _workspacePath = workspacePath;
        }
    }

    public void SetSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                "Provider emitted an empty session identifier.");
        }

        lock (_snapshotLock)
        {
            if (_sessionId is not null &&
                !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Provider changed session id from '{_sessionId}' to '{sessionId}' during one run.");
            }

            _sessionId = sessionId;
        }
    }

    public AgentEvent CreateEvent(AgentDecodedEvent decoded)
    {
        var agentEvent = new AgentEvent(
            RunId,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            decoded.Kind,
            decoded.Name,
            decoded.Message,
            decoded.Progress,
            decoded.Stream,
            decoded.NativePayloadJson);

        if (!string.IsNullOrWhiteSpace(decoded.Message))
        {
            lock (_snapshotLock)
            {
                _lastMessage = decoded.Message;
                if (decoded.Kind == AgentEventKind.AgentMessage)
                {
                    _finalMessage = decoded.Message;
                }
            }
        }

        return agentEvent;
    }

    public ValueTask PublishAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_events.Writer.TryWrite(agentEvent))
        {
            throw new InvalidOperationException("The run event stream is already closed.");
        }

        return ValueTask.CompletedTask;
    }

    public void Complete(AgentRunResult result)
    {
        SetState(result.State);
        _events.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    public void Dispose()
    {
        _runCancellation.Dispose();
    }
}

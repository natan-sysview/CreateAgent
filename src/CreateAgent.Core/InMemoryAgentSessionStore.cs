using System.Collections.Concurrent;

namespace CreateAgent.Core;

internal sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSessionRecord> _sessions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _leases =
        new(StringComparer.Ordinal);

    public Task SaveAsync(
        AgentSessionRecord session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = _sessions.GetOrAdd(session.SessionId, session);
        if (existing != session)
        {
            throw new InvalidOperationException(
                $"Session '{session.SessionId}' is already registered to a different provider or workspace.");
        }

        return Task.CompletedTask;
    }

    public async Task<IAgentSessionLease> AcquireAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new AgentSessionUnavailableException(
                $"Session '{sessionId}' is not registered by this manager.");
        }

        if (session.ProviderId != providerId)
        {
            throw new AgentSessionUnavailableException(
                $"Session '{sessionId}' belongs to provider '{session.ProviderId}', not '{providerId}'.");
        }

        if (!Directory.Exists(session.WorkspacePath))
        {
            throw new AgentSessionUnavailableException(
                $"The workspace for this session no longer exists: {session.WorkspacePath}");
        }

        var gate = _leases.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentSessionBusyException(
                $"Session '{sessionId}' is already running.");
        }

        return new Lease(session, gate);
    }

    public Task DeleteAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryGetValue(sessionId, out var session) &&
            session.ProviderId == providerId)
        {
            _sessions.TryRemove(
                new KeyValuePair<string, AgentSessionRecord>(sessionId, session));
        }

        return Task.CompletedTask;
    }

    private sealed class Lease : IAgentSessionLease
    {
        private SemaphoreSlim? _gate;

        public Lease(AgentSessionRecord session, SemaphoreSlim gate)
        {
            Session = session;
            _gate = gate;
        }

        public AgentSessionRecord Session { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

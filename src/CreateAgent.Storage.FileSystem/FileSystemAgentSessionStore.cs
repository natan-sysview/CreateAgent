using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace CreateAgent.Storage.FileSystem;

public sealed class FileSystemAgentSessionStore : IAgentSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        CreateAgentJson.CreateSerializerOptions(writeIndented: true);

    private readonly string _runsRoot;
    private readonly string _sessionsRoot;

    public FileSystemAgentSessionStore(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            throw new ArgumentException("Storage root cannot be empty.", nameof(storageRoot));
        }

        var normalizedRoot = Path.GetFullPath(storageRoot);
        _runsRoot = Path.Combine(normalizedRoot, "runs");
        _sessionsRoot = Path.Combine(normalizedRoot, "sessions");
        CreatePrivateDirectory(_sessionsRoot);
    }

    public async Task SaveAsync(
        AgentSessionRecord session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionId(session.SessionId);
        var normalized = session with
        {
            WorkspacePath = ValidateWorkspacePath(session.WorkspacePath),
        };
        var targetPath = GetRecordPath(session.SessionId);
        if (File.Exists(targetPath))
        {
            await EnsureSameRecordAsync(targetPath, normalized, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            FileSystemSecurity.MakeFilePrivate(temporaryPath);
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
                FileSystemSecurity.MakeFilePrivate(targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                await EnsureSameRecordAsync(targetPath, normalized, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<IAgentSessionLease> AcquireAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var recordPath = GetRecordPath(sessionId);
        if (!File.Exists(recordPath))
        {
            throw Missing(sessionId);
        }

        var lockPath = await AcquireLockAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        FileStream stream;
        try
        {
            EnsureNotLink(new FileInfo(recordPath));
            stream = new FileStream(
                recordPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            ReleaseLock(lockPath);
            throw Missing(sessionId);
        }
        catch (IOException exception)
        {
            ReleaseLock(lockPath);
            throw new AgentSessionBusyException(
                $"Session '{sessionId}' is already running.",
                exception);
        }
        catch
        {
            ReleaseLock(lockPath);
            throw;
        }

        try
        {
            var session = await JsonSerializer.DeserializeAsync<AgentSessionRecord>(
                              stream,
                              JsonOptions,
                              cancellationToken)
                          .ConfigureAwait(false) ??
                          throw new InvalidDataException(
                              $"Session registry entry '{sessionId}' is empty.");
            if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Session registry entry '{sessionId}' has an invalid identifier.");
            }

            if (session.ProviderId != providerId)
            {
                throw new AgentSessionUnavailableException(
                    $"Session '{sessionId}' belongs to provider '{session.ProviderId}', not '{providerId}'.");
            }

            session = session with
            {
                WorkspacePath = ValidateWorkspacePath(session.WorkspacePath),
            };
            return new FileSystemAgentSessionLease(session, stream, lockPath);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            ReleaseLock(lockPath);
            throw;
        }
    }

    public Task DeleteAsync(
        AgentProviderId providerId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRecordPath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private async Task EnsureSameRecordAsync(
        string path,
        AgentSessionRecord expected,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureNotLink(new FileInfo(path));
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await JsonSerializer.DeserializeAsync<AgentSessionRecord>(
                             stream,
                             JsonOptions,
                             cancellationToken)
                         .ConfigureAwait(false) ??
                         throw new InvalidDataException("Session registry entry is empty.");
            if (actual.ProviderId != expected.ProviderId ||
                !string.Equals(actual.SessionId, expected.SessionId, StringComparison.Ordinal) ||
                !PathsEqual(
                    ValidateWorkspacePath(actual.WorkspacePath),
                    expected.WorkspacePath))
            {
                throw new InvalidOperationException(
                    $"Session '{expected.SessionId}' is already registered to a different provider or workspace.");
            }
        }
        catch (IOException exception)
        {
            throw new AgentSessionBusyException(
                $"Session '{expected.SessionId}' is already running.",
                exception);
        }
    }

    private string ValidateWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidDataException("Session workspace path is empty.");
        }

        var normalized = Path.GetFullPath(workspacePath);
        if (!IsChildPath(normalized, _runsRoot) ||
            !string.Equals(
                Path.GetFileName(normalized),
                "workspace",
                PathComparison))
        {
            throw new InvalidDataException(
                $"Session workspace is outside the configured runs directory: {normalized}");
        }

        if (!Directory.Exists(normalized))
        {
            throw new AgentSessionUnavailableException(
                $"The workspace for this session no longer exists: {normalized}");
        }

        var current = new DirectoryInfo(normalized);
        var normalizedRunsRoot = Path.GetFullPath(_runsRoot);
        while (true)
        {
            EnsureNotLink(current);
            if (PathsEqual(current.FullName, normalizedRunsRoot))
            {
                break;
            }

            current = current.Parent ??
                      throw new InvalidDataException(
                          $"Session workspace is outside the configured runs directory: {normalized}");
        }

        return normalized;
    }

    private string GetRecordPath(string sessionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return Path.Combine(_sessionsRoot, Convert.ToHexStringLower(bytes) + ".json");
    }

    private async Task<string> AcquireLockAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var lockPath = GetRecordPath(sessionId) + ".lock";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = false;
            try
            {
                await using var stream = new FileStream(
                    lockPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                created = true;
                var process = Process.GetCurrentProcess();
                await JsonSerializer.SerializeAsync(
                        stream,
                        new SessionLock(
                            Environment.ProcessId,
                            process.StartTime.ToUniversalTime(),
                            Environment.MachineName),
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                FileSystemSecurity.MakeFilePrivate(lockPath);
                return lockPath;
            }
            catch (IOException) when (!created && File.Exists(lockPath))
            {
                if (attempt == 0 && await IsStaleLockAsync(lockPath, cancellationToken)
                                        .ConfigureAwait(false))
                {
                    File.Delete(lockPath);
                    continue;
                }

                throw new AgentSessionBusyException(
                    $"Session '{sessionId}' is already running.");
            }
            catch
            {
                if (created)
                {
                    ReleaseLock(lockPath);
                }

                throw;
            }
        }

        throw new AgentSessionBusyException(
            $"Session '{sessionId}' is already running.");
    }

    private static async Task<bool> IsStaleLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var owner = await JsonSerializer.DeserializeAsync<SessionLock>(
                            stream,
                            JsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
            if (owner is null ||
                !string.Equals(
                    owner.MachineName,
                    Environment.MachineName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(owner.ProcessId);
                return process.StartTime.ToUniversalTime() != owner.ProcessStartedAtUtc;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(lockPath) >
                   TimeSpan.FromMinutes(1);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ReleaseLock(string lockPath)
    {
        try
        {
            File.Delete(lockPath);
        }
        catch (FileNotFoundException)
        {
            // The session was already invalidated.
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));
        }

        if (sessionId.Length > 512)
        {
            throw new ArgumentException(
                "Session id cannot exceed 512 characters.",
                nameof(sessionId));
        }
    }

    private static AgentSessionUnavailableException Missing(string sessionId) =>
        new(
            $"Session '{sessionId}' is not registered. Only sessions created by this CreateAgent storage can be resumed.");

    private static bool IsChildPath(string path, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent);
        var parentWithSeparator = normalizedParent.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedParent
            : normalizedParent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSeparator, PathComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void EnsureNotLink(FileSystemInfo info)
    {
        if (info.LinkTarget is not null ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Session registry rejected a symbolic link: {info.FullName}");
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        FileSystemSecurity.MakeDirectoryPrivate(path);
    }

    private sealed class FileSystemAgentSessionLease : IAgentSessionLease
    {
        private FileStream? _stream;
        private string? _lockPath;

        public FileSystemAgentSessionLease(
            AgentSessionRecord session,
            FileStream stream,
            string lockPath)
        {
            Session = session;
            _stream = stream;
            _lockPath = lockPath;
        }

        public AgentSessionRecord Session { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stream, null) is { } stream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (Interlocked.Exchange(ref _lockPath, null) is { } lockPath)
            {
                ReleaseLock(lockPath);
            }
        }
    }

    private sealed record SessionLock(
        int ProcessId,
        DateTime ProcessStartedAtUtc,
        string MachineName);
}

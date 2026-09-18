using CreateAgent.Storage.FileSystem;

namespace CreateAgent.Tests;

public sealed class FileSystemAgentSessionStoreTests
{
    [Fact]
    public async Task SessionCanBeAcquiredAfterStoreRestart()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var workspace = CreateWorkspace(storage, "first");
        var provider = new AgentProviderId("test.provider");
        var firstStore = new FileSystemAgentSessionStore(storage);
        await firstStore.SaveAsync(
            new AgentSessionRecord("session-1", provider, workspace));

        var restartedStore = new FileSystemAgentSessionStore(storage);
        await using var lease = await restartedStore.AcquireAsync(
            provider,
            "session-1");

        Assert.Equal("session-1", lease.Session.SessionId);
        Assert.Equal(provider, lease.Session.ProviderId);
        Assert.Equal(workspace, lease.Session.WorkspacePath);
        var record = Assert.Single(
            Directory.GetFiles(Path.Combine(storage, "sessions"), "*.json"));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(record));
    }

    [Fact]
    public async Task RepeatedSessionEventIsIdempotentButAnotherWorkspaceIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var provider = new AgentProviderId("test.provider");
        var workspace = CreateWorkspace(storage, "first");
        var store = new FileSystemAgentSessionStore(storage);
        var session = new AgentSessionRecord("session-1", provider, workspace);

        await store.SaveAsync(session);
        await store.SaveAsync(session);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(
                session with { WorkspacePath = CreateWorkspace(storage, "second") }));
        Assert.Contains("different", exception.Message);
    }

    [Fact]
    public async Task SameSessionCannotBeAcquiredTwice()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var provider = new AgentProviderId("test.provider");
        var store = new FileSystemAgentSessionStore(storage);
        await store.SaveAsync(
            new AgentSessionRecord(
                "session-1",
                provider,
                CreateWorkspace(storage, "first")));
        await using var lease = await store.AcquireAsync(provider, "session-1");

        await Assert.ThrowsAsync<AgentSessionBusyException>(
            () => store.AcquireAsync(provider, "session-1"));
    }

    [Fact]
    public async Task AbandonedSessionLockIsRecovered()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var provider = new AgentProviderId("test.provider");
        var store = new FileSystemAgentSessionStore(storage);
        await store.SaveAsync(
            new AgentSessionRecord(
                "session-1",
                provider,
                CreateWorkspace(storage, "first")));
        var record = Assert.Single(
            Directory.GetFiles(Path.Combine(storage, "sessions"), "*.json"));
        var lockPath = record + ".lock";
        await File.WriteAllTextAsync(
            lockPath,
            $$"""
              {"processId":2147483000,"processStartedAtUtc":"2020-01-01T00:00:00Z","machineName":"{{Environment.MachineName}}"}
              """);

        await using (var lease = await store.AcquireAsync(provider, "session-1"))
        {
            Assert.Equal("session-1", lease.Session.SessionId);
        }

        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task ProviderMismatchIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var store = new FileSystemAgentSessionStore(storage);
        await store.SaveAsync(
            new AgentSessionRecord(
                "session-1",
                new AgentProviderId("test.first"),
                CreateWorkspace(storage, "first")));

        var exception = await Assert.ThrowsAsync<AgentSessionUnavailableException>(
            () => store.AcquireAsync(
                new AgentProviderId("test.second"),
                "session-1"));

        Assert.Contains("belongs to provider", exception.Message);
    }

    [Fact]
    public async Task MissingWorkspaceIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var workspace = CreateWorkspace(storage, "first");
        var provider = new AgentProviderId("test.provider");
        var store = new FileSystemAgentSessionStore(storage);
        await store.SaveAsync(
            new AgentSessionRecord("session-1", provider, workspace));
        Directory.Delete(workspace, recursive: true);

        var exception = await Assert.ThrowsAsync<AgentSessionUnavailableException>(
            () => store.AcquireAsync(provider, "session-1"));

        Assert.Contains("no longer exists", exception.Message);
    }

    [Fact]
    public async Task WorkspaceOutsideStorageIsRejected()
    {
        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var outside = temporary.CreateDirectory("outside/workspace");
        var store = new FileSystemAgentSessionStore(storage);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(
                new AgentSessionRecord(
                    "session-1",
                    new AgentProviderId("test.provider"),
                    outside)));

        Assert.Contains("outside", exception.Message);
    }

    [Fact]
    public async Task SymbolicLinkWorkspaceIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var storage = temporary.CreateDirectory("storage");
        var realWorkspace = temporary.CreateDirectory("real-workspace");
        var run = Path.Combine(storage, "runs", "2026-09-10", "linked");
        Directory.CreateDirectory(run);
        var linkedWorkspace = Path.Combine(run, "workspace");
        Directory.CreateSymbolicLink(linkedWorkspace, realWorkspace);
        var store = new FileSystemAgentSessionStore(storage);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(
                new AgentSessionRecord(
                    "session-1",
                    new AgentProviderId("test.provider"),
                    linkedWorkspace)));

        Assert.Contains("symbolic link", exception.Message);
    }

    private static string CreateWorkspace(string storage, string runName)
    {
        var workspace = Path.Combine(
            storage,
            "runs",
            "2026-09-10",
            runName,
            "workspace");
        Directory.CreateDirectory(workspace);
        return workspace;
    }
}

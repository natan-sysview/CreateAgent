using System.Text.Json;
using CreateAgent.Core;
using CreateAgent.Runtime.Processes;
using CreateAgent.Storage.FileSystem;

namespace CreateAgent.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "createagent-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string relativePath)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class FakeCliAdapter : IAgentCliAdapter
{
    private readonly string _mode;
    private readonly int _delayMilliseconds;
    private readonly string? _createdFile;
    private readonly int _eventCount;
    private readonly bool _available;
    private readonly string? _sessionIdOverride;

    public FakeCliAdapter(
        string mode = "success",
        int delayMilliseconds = 10,
        string? createdFile = null,
        int eventCount = 0,
        bool available = true,
        string? sessionIdOverride = null)
    {
        _mode = mode;
        _delayMilliseconds = delayMilliseconds;
        _createdFile = createdFile;
        _eventCount = eventCount;
        _available = available;
        _sessionIdOverride = sessionIdOverride;
    }

    public AgentProviderId ProviderId { get; } = new("test.fake");

    public Task<AgentProviderCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new AgentProviderCapabilities(
                ProviderId,
                "1.0-test",
                _available,
                _available,
                true,
                true,
                false,
                false,
                false,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));
    }

    public Task MaterializeAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<AgentCliLaunchPlan> CreateLaunchPlanAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = new List<string>
        {
            "--mode",
            _mode,
            "--delay-ms",
            _delayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--session-id",
            _sessionIdOverride ?? (request.Session.Mode == AgentSessionMode.Resume
                ? request.Session.Id!
                : $"fake-session-{workspace.RunId}"),
        };
        if (_createdFile is not null)
        {
            arguments.Add("--create-file");
            arguments.Add(_createdFile);
        }

        if (_eventCount > 0)
        {
            arguments.Add("--event-count");
            arguments.Add(
                _eventCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        string? temporarySchemaPath = null;
        if (request.OutputSchema is not null)
        {
            temporarySchemaPath = System.IO.Path.Combine(
                workspace.AuditDirectory,
                ".output-schema.tmp.json");
            File.WriteAllText(
                temporarySchemaPath,
                request.OutputSchema.Value.GetRawText());
        }

        return Task.FromResult(
            new AgentCliLaunchPlan(
                FakeCliExecutable(),
                arguments,
                workspace.WorkingDirectory,
                new Dictionary<string, string>(StringComparer.Ordinal),
                AgentInputMode.StandardInput,
                AgentOutputProtocol.JsonLines)
            {
                TemporaryFiles = temporarySchemaPath is null
                    ? []
                    : [temporarySchemaPath],
            });
    }

    public AgentDecodedEvent DecodeOutput(AgentProcessOutput output)
    {
        if (output.Stream == AgentOutputChannel.StandardError)
        {
            return new AgentDecodedEvent(
                AgentEventKind.Warning,
                "provider.stderr",
                output.Text,
                Stream: output.Stream);
        }

        using var document = JsonDocument.Parse(output.Text);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString()!;
        var nativeJson = root.GetRawText();

        if (type == "thread.started")
        {
            return new AgentDecodedEvent(
                AgentEventKind.SessionStarted,
                type,
                root.GetProperty("thread_id").GetString(),
                NativePayloadJson: nativeJson);
        }

        if (type == "error")
        {
            return new AgentDecodedEvent(
                AgentEventKind.Error,
                type,
                root.GetProperty("message").GetString(),
                NativePayloadJson: nativeJson);
        }

        if (type is "item.started" or "item.completed")
        {
            var item = root.GetProperty("item");
            var itemType = item.GetProperty("type").GetString();
            if (type == "item.completed" && itemType == "agent_message")
            {
                return new AgentDecodedEvent(
                    AgentEventKind.AgentMessage,
                    "agent.message",
                    item.GetProperty("text").GetString(),
                    NativePayloadJson: nativeJson);
            }

            return new AgentDecodedEvent(
                type == "item.started"
                    ? AgentEventKind.ToolStarted
                    : AgentEventKind.ToolCompleted,
                type,
                item.TryGetProperty("command", out var command)
                    ? command.GetString()
                    : itemType,
                NativePayloadJson: nativeJson);
        }

        return new AgentDecodedEvent(
            AgentEventKind.ProviderEvent,
            type,
            NativePayloadJson: nativeJson);
    }

    private static string FakeCliExecutable() =>
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "CreateAgent.Testing.FakeCli.exe"
                : "CreateAgent.Testing.FakeCli");
}

internal static class TestManagerFactory
{
    public static AgentWorkerManager Create(
        string storageRoot,
        IAgentCliAdapter adapter,
        int maxConcurrentRuns = 2,
        int eventChannelCapacity = 1024,
        long maxOutputBytes = 52_428_800,
        bool retainSuccessfulWorkspace = true,
        bool quarantineOnFailure = true,
        IReadOnlyList<string>? secretsToRedact = null,
        TimeSpan? queueTimeout = null)
    {
        var runner = new SystemProcessRunner();
        return new AgentWorkerManager(
            [adapter],
            runner,
            new FileSystemWorkspaceProvider(storageRoot),
            new JsonLineAuditFactory(),
            new ZipFailureBundler(storageRoot),
            new CreateAgentOptions
            {
                StorageRoot = storageRoot,
                MaxConcurrentRuns = maxConcurrentRuns,
                EventChannelCapacity = eventChannelCapacity,
                MaxOutputBytes = maxOutputBytes,
                RetainSuccessfulWorkspace = retainSuccessfulWorkspace,
                QuarantineOnFailure = quarantineOnFailure,
                SecretsToRedact = secretsToRedact ?? [],
                QueueTimeout = queueTimeout ?? TimeSpan.FromMinutes(5),
            },
            new FileSystemAgentSessionStore(storageRoot));
    }

    public static AgentRunRequest Request(
        string sourcePath,
        string prompt,
        TimeSpan? timeout = null) =>
        new()
        {
            Name = "CreateAgent test run",
            ProviderName = "fake",
            Prompt = prompt,
            Workspace = new AgentWorkspaceRequest
            {
                Sources =
                [
                    new AgentWorkspaceSource
                    {
                        Path = sourcePath,
                        Copy = AgentWorkspaceCopyMode.Contents,
                    },
                ],
            },
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };

    public static async Task<List<AgentEvent>> CollectEventsAsync(
        IAsyncEnumerable<AgentEvent> source)
    {
        var events = new List<AgentEvent>();
        await foreach (var agentEvent in source.ConfigureAwait(false))
        {
            events.Add(agentEvent);
        }

        return events;
    }
}

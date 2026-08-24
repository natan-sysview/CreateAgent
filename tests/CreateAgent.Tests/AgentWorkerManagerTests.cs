using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CreateAgent.Tests;

public sealed class AgentWorkerManagerTests
{
    private static readonly string[] StandardAuditFiles =
        ["events.jsonl", "input.json", "output.json"];

    [Fact]
    public async Task SuccessfulRunStreamsEventsAndPersistsRedactedAudit()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(source, "input.txt"),
            "workspace input");
        var secret = "super-secret-test-value";
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(createdFile: "artifacts/result.txt"),
            secretsToRedact: [secret]);
        var request = TestManagerFactory.Request(
            source,
            $"complete this work using {secret}");

        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        var events = await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.Equal(AgentRunTermination.Succeeded, result.Termination);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.ExecutionPlan);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Artifacts);
        Assert.True(File.Exists(System.IO.Path.Combine(result.WorkspacePath, "input.txt")));
        Assert.True(
            File.Exists(
                System.IO.Path.Combine(
                    result.WorkspacePath,
                    "artifacts",
                    "result.txt")));
        Assert.Contains(events, static value => value.Kind == AgentEventKind.ToolStarted);
        Assert.Contains(events, static value => value.Kind == AgentEventKind.AgentMessage);
        Assert.Equal(AgentEventKind.RunCompleted, events[^1].Kind);
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(static value => (long)value),
            events.Select(static value => value.Sequence));

        var runDirectory = Directory.GetParent(result.WorkspacePath)!.FullName;
        Assert.Matches(
            "^createagent-test-run-[0-9]{6}$",
            System.IO.Path.GetFileName(runDirectory));
        Assert.Matches(
            "^[0-9]{4}-[0-9]{2}-[0-9]{2}$",
            Directory.GetParent(runDirectory)!.Name);
        var requestJson = await File.ReadAllTextAsync(
            System.IO.Path.Combine(result.AuditDirectory, "input.json"));
        var eventsJson = await File.ReadAllTextAsync(result.AuditPath);
        var resultJson = await File.ReadAllTextAsync(
            System.IO.Path.Combine(result.AuditDirectory, "output.json"));
        Assert.Equal(
            System.IO.Path.Combine(runDirectory, "audit"),
            result.AuditDirectory);
        Assert.True(
            File.Exists(System.IO.Path.Combine(result.AuditDirectory, "input.json")));
        Assert.True(
            File.Exists(System.IO.Path.Combine(result.AuditDirectory, "output.json")));
        Assert.Equal(
            StandardAuditFiles,
            Directory.GetFiles(result.AuditDirectory)
                .Select(System.IO.Path.GetFileName)
                .OrderBy(static value => value, StringComparer.Ordinal));
        Assert.False(File.Exists(System.IO.Path.Combine(runDirectory, "request.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(runDirectory, "result.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(runDirectory, "last-message.txt")));
        Assert.False(
            File.Exists(
                System.IO.Path.Combine(
                    result.AuditDirectory,
                    "execution-trace.jsonl")));
        Assert.False(File.Exists(System.IO.Path.Combine(result.AuditDirectory, "run.json")));
        Assert.DoesNotContain(secret, requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, eventsJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("agentDefinition", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupportedBehavior", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("audit", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"succeeded\"", resultJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"executionPlan\"", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredOutputIsReturnedAndPersistedWithoutTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "structured"));
        var request = TestManagerFactory.Request(
            source,
            "Devuelve una persona como JSON.") with
        {
            OutputSchema = PersonOutputSchema(),
            Debug = true,
        };

        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.Null(result.FinalMessage);
        Assert.Equal("Laura", result.StructuredOutput?.GetProperty("nombre").GetString());
        Assert.Equal(
            "laura@example.com",
            result.StructuredOutput?.GetProperty("email").GetString());
        Assert.False(
            File.Exists(
                System.IO.Path.Combine(
                    result.AuditDirectory,
                    ".output-schema.tmp.json")));

        using var persistedResult = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                System.IO.Path.Combine(result.AuditDirectory, "output.json")));
        var persistedOutput = persistedResult.RootElement.GetProperty("structuredOutput");
        Assert.False(persistedResult.RootElement.TryGetProperty("finalMessage", out _));
        Assert.Equal("Méndez", persistedOutput.GetProperty("apellido").GetString());
        Assert.Equal(
            StandardAuditFiles.Append("files.json").Order(StringComparer.Ordinal),
            Directory.GetFiles(result.AuditDirectory)
                .Select(System.IO.Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FailedRunCreatesVerifiableQuarantineArchive()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "fail"));

        var request = TestManagerFactory.Request(
            source,
            "fail in a controlled way") with
        {
            Debug = true,
        };
        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        var events = await eventsTask;

        Assert.Equal(AgentRunState.Failed, result.State);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains(events, static value => value.Kind == AgentEventKind.Error);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("failure-workspace", artifact.Name);
        Assert.True(File.Exists(artifact.Path));
        await using var stream = File.OpenRead(artifact.Path);
        var hash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream));
        Assert.Equal(artifact.Sha256, hash);
        Assert.Equal(new FileInfo(artifact.Path).Length, artifact.SizeBytes);
        using var archive = ZipFile.OpenRead(artifact.Path);
        Assert.Contains(
            archive.Entries,
            static entry => entry.FullName.EndsWith(
                "audit/events.jsonl",
                StringComparison.Ordinal));
        Assert.Contains(
            archive.Entries,
            static entry => entry.FullName.EndsWith(
                "audit/files.json",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            archive.Entries,
            static entry => entry.FullName.EndsWith(
                "audit/execution-trace.jsonl",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            archive.Entries,
            static entry => entry.FullName.EndsWith(
                "audit/error.json",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DebugDiagnosticsPersistMinimalAuditAndFileChanges()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(source, "input.txt"),
            "workspace input");
        var secret = "debug-secret-value";
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(
                mode: "stderr",
                createdFile: "artifacts/result.txt"),
            secretsToRedact: [secret]);
        var request = TestManagerFactory.Request(
            source,
            $"complete debug run using {secret}") with
        {
            Debug = true,
        };

        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        var expectedFiles = new[]
        {
            "events.jsonl",
            "files.json",
            "input.json",
            "output.json",
        };
        Assert.Equal(
            expectedFiles,
            Directory.GetFiles(result.AuditDirectory)
                .Select(System.IO.Path.GetFileName)
                .OrderBy(static value => value, StringComparer.Ordinal));

        var eventsJson = await File.ReadAllTextAsync(
            System.IO.Path.Combine(result.AuditDirectory, "events.jsonl"));
        var output = await File.ReadAllTextAsync(
            System.IO.Path.Combine(result.AuditDirectory, "output.json"));
        Assert.DoesNotContain(secret, eventsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains(
            "fake warning from stderr",
            eventsJson,
            StringComparison.Ordinal);

        using var files = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                System.IO.Path.Combine(result.AuditDirectory, "files.json")));
        var changes = files.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        var created = Assert.Single(changes);
        Assert.Equal("created", created.GetProperty("change").GetString());
        Assert.Equal("artifacts/result.txt", created.GetProperty("path").GetString());
        Assert.Equal(64, created.GetProperty("after").GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public async Task TimeoutKillsProcessAndQuarantinesWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "slow"));

        var handle = await manager.StartAsync(
            TestManagerFactory.Request(
                source,
                "this process should time out",
                timeout: TimeSpan.FromMilliseconds(250)));
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.TimedOut, result.State);
        Assert.Equal(AgentRunTermination.TimedOut, result.Termination);
        Assert.Contains(
            result.Errors,
            static error => error.Contains("timeout", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public async Task InvalidSessionCombinationIsRejectedBeforeCreatingRun()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter());
        var request = TestManagerFactory.Request(source, "validate me") with
        {
            Session = new AgentSessionOptions
            {
                Mode = AgentSessionMode.New,
                Id = "must-not-be-present",
            },
        };

        var exception = await Assert.ThrowsAsync<CreateAgentValidationException>(
            () => manager.StartAsync(request));

        Assert.Contains(
            exception.Errors,
            static error => error.Contains("Session Id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsumerCancellationKillsActiveWorker()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "slow"));
        var handle = await manager.StartAsync(
            TestManagerFactory.Request(source, "cancel this worker"));
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        await WaitForStateAsync(handle, AgentRunState.Running);

        await handle.CancelAsync("cancelled by test");
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Cancelled, result.State);
        Assert.Equal(AgentRunTermination.Cancelled, result.Termination);
        Assert.Contains("cancelled by test", result.Errors);
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public async Task QueueTimeoutDoesNotCreateSecondWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var storage = temporary.CreateDirectory("storage");
        using var manager = TestManagerFactory.Create(
            storage,
            new FakeCliAdapter(delayMilliseconds: 500),
            maxConcurrentRuns: 1,
            queueTimeout: TimeSpan.FromMilliseconds(100));
        var first = await manager.StartAsync(
            TestManagerFactory.Request(source, "first worker"));
        var firstEvents = TestManagerFactory.CollectEventsAsync(first.ReadEventsAsync());
        await WaitForStateAsync(first, AgentRunState.Running);
        var secondRequest = TestManagerFactory.Request(source, "second worker");

        var second = await manager.StartAsync(secondRequest);
        var secondEvents = TestManagerFactory.CollectEventsAsync(second.ReadEventsAsync());
        var secondResult = await second.Completion;
        var firstResult = await first.Completion;
        await Task.WhenAll(firstEvents, secondEvents);

        Assert.Equal(AgentRunState.TimedOut, secondResult.State);
        Assert.Equal(string.Empty, secondResult.WorkspacePath);
        Assert.Equal(AgentRunState.Succeeded, firstResult.State);
        var workspaces = Directory.GetDirectories(
            System.IO.Path.Combine(storage, "runs"),
            "workspace",
            SearchOption.AllDirectories);
        Assert.Single(workspaces);
    }

    [Fact]
    public async Task BoundedEventStreamCannotBlockCompletionWithoutReader()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(eventCount: 1_500),
            eventChannelCapacity: 8);

        var handle = await manager.StartAsync(
            TestManagerFactory.Request(source, "produce many events"));
        var result = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var retainedEvents = await TestManagerFactory.CollectEventsAsync(
            handle.ReadEventsAsync());

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.InRange(retainedEvents.Count, 1, 8);
        Assert.Equal(AgentEventKind.RunCompleted, retainedEvents[^1].Kind);
        Assert.True(
            File.ReadLines(result.AuditPath).Count() > 1_500,
            "The durable audit must retain events dropped from the live bounded stream.");
    }

    [Fact]
    public async Task MissingFinalAgentOutputFailsResultContract()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "no-final"));
        var handle = await manager.StartAsync(
            TestManagerFactory.Request(source, "return no final output"));
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());

        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Failed, result.State);
        Assert.Contains(
            result.Errors,
            static error => error.Contains(
                "final agent output",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SimultaneousStderrIsNormalizedAsWarning()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(mode: "stderr"));
        var handle = await manager.StartAsync(
            TestManagerFactory.Request(source, "write stderr too"));
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());

        var result = await handle.Completion;
        var events = await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.Contains(
            events,
            static value =>
                value.Kind == AgentEventKind.Warning &&
                value.Stream == AgentOutputChannel.StandardError);
    }

    [Fact]
    public async Task OutputLimitTerminatesProviderAndFailsRun()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(eventCount: 100),
            maxOutputBytes: 1024);
        var request = TestManagerFactory.Request(source, "exceed output budget");
        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());

        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Failed, result.State);
        Assert.Contains(
            result.Errors,
            static error => error.Contains(
                "output exceeded",
                StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Artifacts);
    }

    [Fact]
    public async Task UnavailableProviderIsRejectedBeforeWorkspaceCopy()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        var storage = temporary.CreateDirectory("storage");
        using var manager = TestManagerFactory.Create(
            storage,
            new FakeCliAdapter(available: false));
        var handle = await manager.StartAsync(
            TestManagerFactory.Request(source, "provider is unavailable"));
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());

        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Failed, result.State);
        Assert.Equal(string.Empty, result.WorkspacePath);
        Assert.False(Directory.Exists(System.IO.Path.Combine(storage, "runs")));
    }

    [Fact]
    public async Task TwoWorkersCanRunConcurrentlyWhenCapacityAllows()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.CreateDirectory("source");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter(delayMilliseconds: 400),
            maxConcurrentRuns: 2);

        var first = await manager.StartAsync(
            TestManagerFactory.Request(source, "concurrent worker one"));
        var second = await manager.StartAsync(
            TestManagerFactory.Request(source, "concurrent worker two"));
        var firstEvents = TestManagerFactory.CollectEventsAsync(first.ReadEventsAsync());
        var secondEvents = TestManagerFactory.CollectEventsAsync(second.ReadEventsAsync());
        await Task.WhenAll(
            WaitForStateAsync(first, AgentRunState.Running),
            WaitForStateAsync(second, AgentRunState.Running));
        var results = await Task.WhenAll(first.Completion, second.Completion);
        await Task.WhenAll(firstEvents, secondEvents);

        Assert.All(
            results,
            static result => Assert.Equal(AgentRunState.Succeeded, result.State));
        Assert.NotEqual(results[0].WorkspacePath, results[1].WorkspacePath);
    }

    [Fact]
    public async Task WorkspaceSupportsDirectoryAndContentsCopyModes()
    {
        using var temporary = new TemporaryDirectory();
        var skills = temporary.CreateDirectory("sources/skills");
        var data = temporary.CreateDirectory("sources/data");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(skills, "SKILL.md"),
            "skill");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(data, "input.csv"),
            "value");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter());
        var request = new AgentRunRequest
        {
            Name = "workspace copy test",
            ProviderName = "fake",
            Prompt = "validate workspace copies",
            Workspace = new AgentWorkspaceRequest
            {
                Sources =
                [
                    new AgentWorkspaceSource
                    {
                        Path = skills,
                        Copy = AgentWorkspaceCopyMode.Directory,
                    },
                    new AgentWorkspaceSource
                    {
                        Path = data,
                        Copy = AgentWorkspaceCopyMode.Contents,
                    },
                ],
            },
        };

        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.True(File.Exists(System.IO.Path.Combine(result.WorkspacePath, "skills", "SKILL.md")));
        Assert.True(File.Exists(System.IO.Path.Combine(result.WorkspacePath, "input.csv")));
    }

    [Fact]
    public async Task ReadOnlyPathIsNotCopiedAndIsIncludedInThePrompt()
    {
        using var temporary = new TemporaryDirectory();
        var referencePath = temporary.CreateDirectory("large-reference-data");
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(referencePath, "reference.txt"),
            "external data");
        using var manager = TestManagerFactory.Create(
            temporary.CreateDirectory("storage"),
            new FakeCliAdapter());
        var request = new AgentRunRequest
        {
            Name = "read-only reference test",
            ProviderName = "fake",
            Prompt = "inspect the external data",
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [referencePath],
            },
        };

        var handle = await manager.StartAsync(request);
        var eventsTask = TestManagerFactory.CollectEventsAsync(handle.ReadEventsAsync());
        var result = await handle.Completion;
        await eventsTask;

        Assert.Equal(AgentRunState.Succeeded, result.State);
        Assert.Contains(referencePath, result.FinalMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(result.WorkspacePath));
    }

    private static async Task WaitForStateAsync(
        IAgentRunHandle handle,
        AgentRunState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = await handle.GetSnapshotAsync(timeout.Token);
            if (snapshot.State == expected)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Run did not reach state {expected}.");
    }

    private static JsonElement PersonOutputSchema()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "nombre": { "type": "string" },
                "apellido": { "type": "string" },
                "telefono": { "type": "string" },
                "email": { "type": "string" },
                "direccion": { "type": "string" }
              },
              "required": ["nombre", "apellido", "telefono", "email", "direccion"],
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }
}

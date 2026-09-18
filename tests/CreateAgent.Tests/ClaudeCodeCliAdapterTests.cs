using System.Text.Json;
using CreateAgent.Providers.ClaudeCode;
using CreateAgent.Runtime.Processes;

namespace CreateAgent.Tests;

public sealed class ClaudeCodeCliAdapterTests
{
    private static readonly string[] ReadWriteTools = ["Read", "Write"];
    private static readonly string[] RequiredName = ["name"];

    [Fact]
    public async Task ProbesInstalledClaudeWhenAvailable()
    {
        var adapter = new ClaudeCodeCliAdapter(new SystemProcessRunner());

        var capabilities = await adapter.ProbeAsync();
        if (!capabilities.IsAvailable)
        {
            return;
        }

        Assert.True(capabilities.SupportsNonInteractiveExecution);
        Assert.True(capabilities.SupportsStructuredEvents);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.Version));
        Assert.Contains("Claude", capabilities.Version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatesSafeStructuredClaudeLaunchPlan()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkingDirectory, ".mcp.json"),
            "{}");
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Claude adapter test",
            ProviderName = "claude",
            Model = "sonnet",
            Prompt = "test prompt",
            Session = new AgentSessionOptions { Mode = AgentSessionMode.Ephemeral },
            Tools = ReadWriteTools,
            OutputSchema = JsonSerializer.SerializeToElement(
                new
                {
                    type = "object",
                    properties = new { name = new { type = "string" } },
                    required = RequiredName,
                    additionalProperties = false,
                }),
        };

        var plan = await adapter.CreateLaunchPlanAsync(request, workspace);

        Assert.Equal(AgentOutputProtocol.JsonLines, plan.OutputProtocol);
        Assert.Equal(AgentInputMode.StandardInput, plan.InputMode);
        Assert.Contains("-p", plan.Arguments);
        AssertOption(plan.Arguments, "--output-format", "stream-json");
        AssertOption(plan.Arguments, "--permission-mode", "acceptEdits");
        AssertOption(plan.Arguments, "--model", "sonnet");
        AssertOption(plan.Arguments, "--tools", "Read,Write");
        AssertOption(plan.Arguments, "--allowedTools", "Read,Write");
        Assert.Contains("--strict-mcp-config", plan.Arguments);
        Assert.Contains("--no-session-persistence", plan.Arguments);
        AssertOption(
            plan.Arguments,
            "--mcp-config",
            Path.Combine(workspace.WorkingDirectory, ".mcp.json"));
        Assert.Contains("--json-schema", plan.Arguments);
        Assert.Single(plan.TemporaryFiles);
        Assert.All(
            plan.TemporaryFiles,
            path => Assert.StartsWith(workspace.AuditDirectory, path));
        Assert.Contains("CLAUDE_CODE_DEBUG_LOGS_DIR", plan.Environment.Keys);
    }

    [Fact]
    public async Task ProtectsReadOnlyPathsForToolsAndSandboxedCommands()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        var referencePath = temporary.CreateDirectory("large-reference-data");
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Claude read-only reference test",
            ProviderName = "claude",
            Prompt = "read the reference",
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [referencePath],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(request, workspace);

        AssertOption(plan.Arguments, "--add-dir", referencePath);
        AssertOption(plan.Arguments, "--tools", "default");
        var settingsJson = GetOption(plan.Arguments, "--settings");
        using var settings = JsonDocument.Parse(settingsJson);
        Assert.True(
            settings.RootElement.GetProperty("sandbox").GetProperty("enabled").GetBoolean());
        Assert.True(
            settings.RootElement
                .GetProperty("sandbox")
                .GetProperty("failIfUnavailable")
                .GetBoolean());
        Assert.False(
            settings.RootElement
                .GetProperty("sandbox")
                .GetProperty("allowUnsandboxedCommands")
                .GetBoolean());
        Assert.Contains(
            referencePath,
            settings.RootElement
                .GetProperty("sandbox")
                .GetProperty("filesystem")
                .GetProperty("denyWrite")
                .EnumerateArray()
                .Select(static element => element.GetString()));
        Assert.Contains(
            settings.RootElement
                .GetProperty("permissions")
                .GetProperty("deny")
                .EnumerateArray()
                .Select(static element => element.GetString()),
            rule => rule is not null && rule.StartsWith("Edit(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadOnlyFileDoesNotGrantAccessToItsParentDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        var referenceDirectory = temporary.CreateDirectory("reference-files");
        var referenceFile = Path.Combine(referenceDirectory, "protected.txt");
        await File.WriteAllTextAsync(referenceFile, "protected");
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Claude read-only file test",
            ProviderName = "claude",
            Prompt = "read the file",
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [referenceFile],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(request, workspace);

        Assert.DoesNotContain("--add-dir", plan.Arguments);
        using var settings = JsonDocument.Parse(GetOption(plan.Arguments, "--settings"));
        Assert.Contains(
            referenceFile,
            settings.RootElement
                .GetProperty("sandbox")
                .GetProperty("filesystem")
                .GetProperty("denyWrite")
                .EnumerateArray()
                .Select(static element => element.GetString()));
    }

    [Fact]
    public async Task MapsClaudeResumeAndRejectsAmbiguousSessions()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());

        var resume = await adapter.CreateLaunchPlanAsync(
            CreateRequest(new AgentSessionOptions
            {
                Mode = AgentSessionMode.Resume,
                Id = "session-123",
            }),
            workspace);
        AssertOption(resume.Arguments, "--resume", "session-123");
        foreach (var mode in new[]
                 {
                     AgentSessionMode.ContinueLatest,
                     AgentSessionMode.Fork,
                 })
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.CreateLaunchPlanAsync(
                    CreateRequest(new AgentSessionOptions { Mode = mode }),
                    workspace));
            Assert.Contains("workspace", exception.Message);
        }
    }

    [Theory]
    [InlineData(AgentPerformanceMode.Fast, "medium")]
    [InlineData(AgentPerformanceMode.Medium, "medium")]
    [InlineData(AgentPerformanceMode.Normal, "xhigh")]
    public async Task RejectsUnverifiedPerformanceAndReasoningValues(
        AgentPerformanceMode performance,
        string effort)
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Unsupported Claude option",
            ProviderName = "claude",
            Prompt = "test",
            Performance = performance,
            Reasoning = new AgentReasoningOptions { Effort = effort },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateLaunchPlanAsync(request, CreateWorkspace(temporary)));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    public async Task UsesEnvironmentForLegacyClaudeReasoning(string effort)
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Legacy Claude reasoning",
            ProviderName = "claude",
            Prompt = "test",
            Reasoning = new AgentReasoningOptions { Effort = effort },
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        Assert.Equal(effort, plan.Environment["CLAUDE_CODE_EFFORT_LEVEL"]);
        Assert.DoesNotContain("--effort", plan.Arguments);
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task UsesEffortFlagForCurrentClaude(string effort)
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var capabilities = await adapter.ProbeAsync();
        var request = new AgentRunRequest
        {
            Name = "Current Claude options",
            ProviderName = "claude",
            Model = "claude-opus-4-6",
            Reasoning = new AgentReasoningOptions { Effort = effort },
            Prompt = "test",
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        Assert.DoesNotContain("fast", capabilities.SupportedPerformanceModes);
        Assert.Contains(effort, capabilities.SupportedReasoningEfforts);
        AssertOption(plan.Arguments, "--effort", effort);
        Assert.DoesNotContain("CLAUDE_CODE_EFFORT_LEVEL", plan.Environment.Keys);
        using var settings = JsonDocument.Parse(GetOption(plan.Arguments, "--settings"));
        Assert.False(settings.RootElement.GetProperty("fastMode").GetBoolean());
    }

    [Fact]
    public void DecodesClaudeSessionToolsAndStructuredResult()
    {
        var adapter = new ClaudeCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());

        var session = adapter.DecodeOutput(Output(
            """{"type":"system","subtype":"init","session_id":"session-1"}"""));
        var started = adapter.DecodeOutput(Output(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}"""));
        var completed = adapter.DecodeOutput(Output(
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}"""));
        var result = adapter.DecodeOutput(Output(
            """{"type":"result","is_error":false,"structured_output":{"ok":true}}"""));

        Assert.Equal(AgentEventKind.SessionStarted, session.Kind);
        Assert.Equal("session-1", session.Message);
        Assert.Equal(AgentEventKind.ToolStarted, started.Kind);
        Assert.Equal("Read", started.Message);
        Assert.Equal(AgentEventKind.ToolCompleted, completed.Kind);
        Assert.Equal(AgentEventKind.AgentMessage, result.Kind);
        Assert.Equal("{\"ok\":true}", result.Message);
    }

    private static AgentWorkspace CreateWorkspace(TemporaryDirectory temporary)
    {
        var workspacePath = temporary.CreateDirectory("workspace");
        var auditPath = temporary.CreateDirectory("run/audit");
        return new AgentWorkspace(
            "test-run",
            temporary.CreateDirectory("run"),
            workspacePath,
            workspacePath,
            auditPath,
            Path.Combine(auditPath, "output.json"));
    }

    private static AgentRunRequest CreateRequest(AgentSessionOptions session) =>
        new()
        {
            Name = "Claude session test",
            ProviderName = "claude",
            Prompt = "test",
            Session = session,
        };

    private static AgentProcessOutput Output(string text) =>
        new(1, DateTimeOffset.UtcNow, AgentOutputChannel.StandardOutput, text);

    private static void AssertOption(
        IReadOnlyList<string> arguments,
        string option,
        string expected) =>
        Assert.Equal(expected, GetOption(arguments, option));

    private static string GetOption(
        IReadOnlyList<string> arguments,
        string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        throw new Xunit.Sdk.XunitException($"Option '{option}' was not found.");
    }

    private static string FakeCliPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "CreateAgent.Testing.FakeCli.exe"
                : "CreateAgent.Testing.FakeCli");
}

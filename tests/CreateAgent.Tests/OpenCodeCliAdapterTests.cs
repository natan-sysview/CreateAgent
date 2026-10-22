using System.Text.Json;
using CreateAgent.Providers.OpenCode;
using CreateAgent.Runtime.Processes;

namespace CreateAgent.Tests;

public sealed class OpenCodeCliAdapterTests
{
    [Fact]
    public async Task ProbesInstalledOpenCodeWhenAvailable()
    {
        var adapter = new OpenCodeCliAdapter(new SystemProcessRunner());

        var capabilities = await adapter.ProbeAsync();
        if (!capabilities.IsAvailable)
        {
            return;
        }

        Assert.Equal("anomaly.opencode", capabilities.ProviderId.Value);
        Assert.True(capabilities.SupportsNonInteractiveExecution);
        Assert.True(capabilities.SupportsStructuredEvents);
        Assert.True(capabilities.SupportsSessionResume);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.Version));
    }

    [Fact]
    public async Task CreatesDeterministicFlashLaunchPlan()
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = CreateRequest() with
        {
            Debug = true,
            Tools = ["read", "edit"],
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        Assert.Equal(AgentInputMode.StandardInput, plan.InputMode);
        Assert.Equal(AgentOutputProtocol.JsonLines, plan.OutputProtocol);
        Assert.Contains("run", plan.Arguments);
        Assert.Contains("--pure", plan.Arguments);
        AssertOption(plan.Arguments, "--format", "json");
        AssertOption(plan.Arguments, "--model", OpenCodeCliAdapter.FlashModel);
        AssertOption(plan.Arguments, "--variant", "high");
        Assert.Contains("--thinking", plan.Arguments);
        Assert.DoesNotContain(request.Prompt, plan.Arguments);
        Assert.Equal("false", plan.Environment["OPENCODE_AUTO_SHARE"]);
        Assert.Equal("true", plan.Environment["OPENCODE_DISABLE_AUTOUPDATE"]);
        using var config = JsonDocument.Parse(
            plan.Environment["OPENCODE_CONFIG_CONTENT"]);
        var permissions = config.RootElement.GetProperty("permission");
        Assert.Equal("deny", permissions.GetProperty("*").GetString());
        Assert.Equal("allow", permissions.GetProperty("read").GetString());
        Assert.Equal("allow", permissions.GetProperty("edit").GetString());
        Assert.Equal("deny", permissions.GetProperty("question").GetString());
        Assert.Equal(
            "deny",
            permissions.GetProperty("external_directory").GetString());
    }

    [Theory]
    [InlineData(OpenCodeCliAdapter.FlashModel, "low")]
    [InlineData(OpenCodeCliAdapter.FlashModel, "high")]
    [InlineData(OpenCodeCliAdapter.FlashModel, "max")]
    [InlineData(OpenCodeCliAdapter.ProModel, "high")]
    [InlineData(OpenCodeCliAdapter.ProModel, "max")]
    public async Task AcceptsVerifiedModelAndEffortCombinations(
        string model,
        string effort)
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());

        var plan = await adapter.CreateLaunchPlanAsync(
            CreateRequest() with
            {
                Model = model,
                Reasoning = new AgentReasoningOptions { Effort = effort },
            },
            CreateWorkspace(temporary));

        AssertOption(plan.Arguments, "--model", model);
        AssertOption(plan.Arguments, "--variant", effort);
    }

    [Theory]
    [InlineData(OpenCodeCliAdapter.ProModel, "low")]
    [InlineData(OpenCodeCliAdapter.FlashModel, "medium")]
    [InlineData("provider-default", "high")]
    [InlineData("other/model", "high")]
    public async Task RejectsUnsupportedModelAndEffortCombinations(
        string model,
        string effort)
    {
        using var temporary = new TemporaryDirectory();
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.CreateLaunchPlanAsync(
                CreateRequest() with
                {
                    Model = model,
                    Reasoning = new AgentReasoningOptions { Effort = effort },
                },
                CreateWorkspace(temporary)));
    }

    [Fact]
    public async Task ResumesExplicitSessionAndRejectsAmbiguousModes()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());

        var resume = await adapter.CreateLaunchPlanAsync(
            CreateRequest() with
            {
                Session = new AgentSessionOptions
                {
                    Mode = AgentSessionMode.Resume,
                    Id = "session-123",
                },
            },
            workspace);
        AssertOption(resume.Arguments, "--session", "session-123");

        var modes = new[]
        {
            AgentSessionMode.ContinueLatest,
            AgentSessionMode.Fork,
            AgentSessionMode.Ephemeral,
        };

        foreach (var mode in modes)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.CreateLaunchPlanAsync(
                    CreateRequest() with
                    {
                        Session = new AgentSessionOptions
                        {
                            Mode = mode,
                            Id = null,
                        },
                    },
                    workspace));
            Assert.Contains("not verified", exception.Message);
        }
    }

    [Fact]
    public async Task RejectsCapabilitiesThatCannotBeGuaranteed()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = CreateWorkspace(temporary);
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var requests = new[]
        {
            CreateRequest() with { Performance = AgentPerformanceMode.Fast },
            CreateRequest() with
            {
                OutputSchema = JsonSerializer.SerializeToElement(
                    new { type = "object" }),
            },
            CreateRequest() with { Tools = ["question"] },
        };

        foreach (var request in requests)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.CreateLaunchPlanAsync(request, workspace));
        }
    }

    [Fact]
    public async Task AllowsWorkspaceEditsWhileProtectingReadOnlyDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var reference = temporary.CreateDirectory("reference");
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = CreateRequest() with
        {
            Tools = ["read", "edit", "glob", "grep"],
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [reference],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        using var config = JsonDocument.Parse(
            plan.Environment["OPENCODE_CONFIG_CONTENT"]);
        var permissions = config.RootElement.GetProperty("permission");
        Assert.Equal("deny", permissions.GetProperty("*").GetString());
        Assert.Equal("allow", permissions.GetProperty("read").GetString());
        Assert.Equal("allow", permissions.GetProperty("glob").GetString());
        Assert.Equal("allow", permissions.GetProperty("grep").GetString());
        var edit = permissions.GetProperty("edit");
        Assert.Equal("allow", edit.GetProperty("*").GetString());
        Assert.Equal(
            "deny",
            edit.GetProperty(AbsoluteChildrenPattern(reference)).GetString());
        Assert.Equal(
            "deny",
            edit.GetProperty(RelativeSuffixPattern(reference, true)).GetString());
        Assert.Equal("deny", permissions.GetProperty("bash").GetString());
        var external = permissions.GetProperty("external_directory");
        Assert.Equal("deny", external.GetProperty("*").GetString());
        Assert.Equal(
            "allow",
            external.GetProperty(AbsoluteChildrenPattern(reference)).GetString());
    }

    [Fact]
    public async Task KeepsWorkspaceEditsDisabledWhenEditIsNotRequested()
    {
        using var temporary = new TemporaryDirectory();
        var reference = temporary.CreateDirectory("reference");
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = CreateRequest() with
        {
            Tools = ["read"],
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [reference],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        using var config = JsonDocument.Parse(
            plan.Environment["OPENCODE_CONFIG_CONTENT"]);
        var permissions = config.RootElement.GetProperty("permission");
        Assert.Equal("deny", permissions.GetProperty("edit").GetString());
        Assert.Equal("deny", permissions.GetProperty("bash").GetString());
    }

    [Fact]
    public async Task ScopesIndividualReadOnlyFilesToExplicitReadRules()
    {
        using var temporary = new TemporaryDirectory();
        var reference = temporary.CreateDirectory("reference");
        var allowed = Path.Combine(reference, "allowed.json");
        File.WriteAllText(allowed, "{}");
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var request = CreateRequest() with
        {
            Tools = ["read", "edit"],
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [allowed],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(
            request,
            CreateWorkspace(temporary));

        using var config = JsonDocument.Parse(
            plan.Environment["OPENCODE_CONFIG_CONTENT"]);
        var permissions = config.RootElement.GetProperty("permission");
        var external = permissions.GetProperty("external_directory");
        Assert.Equal(
            "allow",
            external.GetProperty(AbsoluteChildrenPattern(reference)).GetString());
        var read = permissions.GetProperty("read");
        Assert.Equal("allow", read.GetProperty("*").GetString());
        Assert.Equal(
            "deny",
            read.GetProperty(RelativeSuffixPattern(reference, true)).GetString());
        Assert.Equal(
            "allow",
            read.GetProperty(RelativeSuffixPattern(allowed, false)).GetString());
        var edit = permissions.GetProperty("edit");
        Assert.Equal("allow", edit.GetProperty("*").GetString());
        Assert.Equal(
            "deny",
            edit.GetProperty(AbsolutePath(allowed)).GetString());
        Assert.Equal(
            "deny",
            edit.GetProperty(RelativeSuffixPattern(allowed, false)).GetString());
        Assert.Equal("deny", permissions.GetProperty("bash").GetString());
    }

    [Fact]
    public async Task RejectsUnsafeReadOnlyPathToolCombinations()
    {
        using var temporary = new TemporaryDirectory();
        var reference = temporary.CreateDirectory("reference");
        var file = Path.Combine(reference, "input.json");
        File.WriteAllText(file, "{}");
        var workspace = CreateWorkspace(temporary);
        var adapter = new OpenCodeCliAdapter(
            new SystemProcessRunner(),
            FakeCliPath());
        var requests = new[]
        {
            CreateRequest() with
            {
                Workspace = new AgentWorkspaceRequest
                {
                    ReadOnlyPaths = [reference],
                },
            },
            CreateRequest() with
            {
                Tools = ["read", "bash"],
                Workspace = new AgentWorkspaceRequest
                {
                    ReadOnlyPaths = [reference],
                },
            },
            CreateRequest() with
            {
                Tools = ["websearch"],
                Workspace = new AgentWorkspaceRequest
                {
                    ReadOnlyPaths = [reference],
                },
            },
            CreateRequest() with
            {
                Tools = ["read", "grep"],
                Workspace = new AgentWorkspaceRequest
                {
                    ReadOnlyPaths = [file],
                },
            },
        };

        foreach (var request in requests)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.CreateLaunchPlanAsync(request, workspace));
        }
    }

    [Fact]
    public void DecodesNativeOpenCodeEvents()
    {
        var adapter = new OpenCodeCliAdapter(new SystemProcessRunner());

        var session = adapter.DecodeOutput(Output(
            """{"type":"step_start","sessionID":"session-1","part":{"type":"step-start"}}"""));
        var reasoning = adapter.DecodeOutput(Output(
            """{"type":"reasoning","part":{"text":"thinking"}}"""));
        var tool = adapter.DecodeOutput(Output(
            """{"type":"tool_use","part":{"tool":"bash","state":{"status":"completed"}}}"""));
        var message = adapter.DecodeOutput(Output(
            """{"type":"text","part":{"text":"completed"}}"""));
        var finished = adapter.DecodeOutput(Output(
            """{"type":"step_finish","part":{"reason":"stop"}}"""));
        var error = adapter.DecodeOutput(Output(
            """{"type":"error","error":{"data":{"message":"provider failed"}}}"""));

        Assert.Equal(AgentEventKind.SessionStarted, session.Kind);
        Assert.Equal("session-1", session.Message);
        Assert.Equal(AgentEventKind.ProviderEvent, reasoning.Kind);
        Assert.Equal("thinking", reasoning.Message);
        Assert.Equal(AgentEventKind.ToolCompleted, tool.Kind);
        Assert.Equal("bash", tool.Message);
        Assert.Equal(AgentEventKind.AgentMessage, message.Kind);
        Assert.Equal("completed", message.Message);
        Assert.Equal(AgentEventKind.Progress, finished.Kind);
        Assert.Equal(1, finished.Progress);
        Assert.Equal(AgentEventKind.Error, error.Kind);
        Assert.Equal("provider failed", error.Message);
    }

    private static AgentRunRequest CreateRequest() =>
        new()
        {
            Name = "OpenCode adapter test",
            ProviderName = "opencode",
            Model = OpenCodeCliAdapter.FlashModel,
            Reasoning = new AgentReasoningOptions { Effort = "high" },
            Prompt = "test prompt",
        };

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

    private static AgentProcessOutput Output(string text) =>
        new(1, DateTimeOffset.UtcNow, AgentOutputChannel.StandardOutput, text);

    private static string AbsoluteChildrenPattern(string path) =>
        string.Concat(
            AbsolutePath(path).TrimEnd('/'),
            "/*");

    private static string AbsolutePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string RelativeSuffixPattern(
        string path,
        bool includeChildren)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var suffix = fullPath[root.Length..]
            .Replace('\\', '/')
            .Trim('/');
        return includeChildren
            ? $"*{suffix}/*"
            : $"*{suffix}";
    }

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

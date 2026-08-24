using CreateAgent.Providers.Codex;
using CreateAgent.Runtime.Processes;
using System.Text.Json;

namespace CreateAgent.Tests;

public sealed class CodexCliAdapterTests
{
    private static readonly string[] RequiredName = ["name"];

    [Fact]
    public async Task ProbesInstalledCodexWhenAvailable()
    {
        var adapter = new CodexCliAdapter(new SystemProcessRunner());

        var capabilities = await adapter.ProbeAsync();
        if (!capabilities.IsAvailable)
        {
            return;
        }

        Assert.True(capabilities.SupportsNonInteractiveExecution);
        Assert.True(capabilities.SupportsStructuredEvents);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.Version));
        Assert.Contains("codex", capabilities.Version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatesNonInteractiveStructuredCodexLaunchPlan()
    {
        using var temporary = new TemporaryDirectory();
        var workspacePath = temporary.CreateDirectory("workspace");
        var workspace = new AgentWorkspace(
            "test-run",
            temporary.CreateDirectory("run"),
            workspacePath,
            workspacePath,
            temporary.CreateDirectory("run/audit"),
            System.IO.Path.Combine(temporary.Path, "run", "audit", "output.json"));
        var adapter = new CodexCliAdapter(new SystemProcessRunner(), FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Codex adapter test",
            ProviderName = "codex",
            Model = "gpt-test",
            Performance = AgentPerformanceMode.Fast,
            Reasoning = new AgentReasoningOptions { Effort = "ultra" },
            Session = new AgentSessionOptions { Mode = AgentSessionMode.Ephemeral },
            Prompt = "test prompt",
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
        Assert.Equal("exec", plan.Arguments[0]);
        Assert.Contains("--json", plan.Arguments);
        Assert.Contains("--ignore-user-config", plan.Arguments);
        Assert.Contains("--ephemeral", plan.Arguments);
        Assert.Contains("gpt-test", plan.Arguments);
        Assert.Contains("approval_policy='never'", plan.Arguments);
        Assert.Contains("model_reasoning_effort='ultra'", plan.Arguments);
        Assert.Contains("service_tier='fast'", plan.Arguments);
        var schemaArgumentIndex = Array.IndexOf(
            plan.Arguments.ToArray(),
            "--output-schema");
        Assert.True(schemaArgumentIndex >= 0);
        var schemaPath = plan.Arguments[schemaArgumentIndex + 1];
        Assert.Equal(Assert.Single(plan.TemporaryFiles), schemaPath);
        Assert.True(File.Exists(schemaPath));
        using (var schema = JsonDocument.Parse(await File.ReadAllTextAsync(schemaPath)))
        {
            Assert.Equal(
                "object",
                schema.RootElement.GetProperty("type").GetString());
        }

        Assert.Empty(Directory.GetFiles(workspacePath));
        Assert.Equal("-", plan.Arguments[^1]);
    }

    [Fact]
    public async Task CreatesReadOnlyPermissionProfileForExternalPaths()
    {
        using var temporary = new TemporaryDirectory();
        var workspacePath = temporary.CreateDirectory("workspace");
        var referencePath = temporary.CreateDirectory("large-reference-data");
        var workspace = new AgentWorkspace(
            "test-run",
            temporary.CreateDirectory("run"),
            workspacePath,
            workspacePath,
            temporary.CreateDirectory("run/audit"),
            System.IO.Path.Combine(temporary.Path, "run", "audit", "output.json"));
        var adapter = new CodexCliAdapter(new SystemProcessRunner(), FakeCliPath());
        var request = new AgentRunRequest
        {
            Name = "Codex read-only reference test",
            ProviderName = "codex",
            Prompt = "read the reference",
            Workspace = new AgentWorkspaceRequest
            {
                ReadOnlyPaths = [referencePath],
            },
        };

        var plan = await adapter.CreateLaunchPlanAsync(request, workspace);

        Assert.DoesNotContain("--sandbox", plan.Arguments);
        Assert.DoesNotContain("--add-dir", plan.Arguments);
        Assert.DoesNotContain(
            plan.Arguments,
            argument => argument.StartsWith("sandbox_mode=", StringComparison.Ordinal));
        Assert.Contains("default_permissions='createagent_readonly'", plan.Arguments);
        Assert.Contains(
            "permissions.createagent_readonly.extends=':workspace'",
            plan.Arguments);
        var fileSystemConfig = Assert.Single(
            plan.Arguments,
            argument => argument.StartsWith(
                "permissions.createagent_readonly.filesystem=",
                StringComparison.Ordinal));
        Assert.Contains(referencePath, fileSystemConfig, StringComparison.Ordinal);
        Assert.Contains("\"read\"", fileSystemConfig, StringComparison.Ordinal);
    }

    private static string FakeCliPath() =>
        System.IO.Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "CreateAgent.Testing.FakeCli.exe"
                : "CreateAgent.Testing.FakeCli");
}

using System.Text.Json;
using global::CreateAgent.Cli;

namespace CreateAgent.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task RequestInitAndValidateFormACompleteWorkflow()
    {
        using var temporary = new TemporaryDirectory();
        var requestPath = Path.Combine(temporary.Path, "request.json");

        var initialized = await RunAsync(
            ["request", "init", "--provider", "codex", "--output", requestPath]);
        var validated = await RunAsync(
            ["request", "validate", requestPath, "--output", "jsonl"]);

        Assert.Equal((int)CliExitCode.Success, initialized.ExitCode);
        Assert.True(File.Exists(requestPath));
        using (var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath)))
        {
            Assert.Equal("codex", request.RootElement.GetProperty("providerName").GetString());
            Assert.False(request.RootElement.TryGetProperty("schemaVersion", out _));
            Assert.False(request.RootElement.TryGetProperty("agentDefinition", out _));
            Assert.False(request.RootElement.TryGetProperty("audit", out _));
            Assert.False(request.RootElement.TryGetProperty("metadata", out _));
        }

        Assert.Equal((int)CliExitCode.Success, validated.ExitCode);
        using var document = JsonDocument.Parse(validated.StandardOutput.Trim());
        Assert.Equal(
            "request-valid",
            document.RootElement.GetProperty("type").GetString());
        Assert.Equal(string.Empty, validated.StandardError);
    }

    [Fact]
    public async Task RequestInitSupportsClaude()
    {
        var initialized = await RunAsync(
            ["request", "init", "--provider", "claude", "--output", "-"]);

        Assert.Equal((int)CliExitCode.Success, initialized.ExitCode);
        using var request = JsonDocument.Parse(initialized.StandardOutput);
        Assert.Equal(
            "claude",
            request.RootElement.GetProperty("providerName").GetString());
    }

    [Fact]
    public async Task RelativeWorkspacePathIsResolvedFromRequestFile()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "source"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "reference"));
        var requestPath = Path.Combine(temporary.Path, "request.json");
        var request = CreateRequest() with
        {
            Workspace = new AgentWorkspaceRequest
            {
                Sources =
                [
                    new AgentWorkspaceSource
                    {
                        Path = "source",
                        Copy = AgentWorkspaceCopyMode.Contents,
                    },
                ],
                ReadOnlyPaths = ["reference"],
            },
        };
        await File.WriteAllTextAsync(
            requestPath,
            CreateAgentJson.SerializeRequest(request));

        var result = await RunAsync(
            ["request", "validate", requestPath, "--output", "jsonl"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
    }

    [Fact]
    public async Task UnknownJsonFieldReturnsValidationExitCode()
    {
        using var temporary = new TemporaryDirectory();
        var requestPath = Path.Combine(temporary.Path, "invalid.json");
        await File.WriteAllTextAsync(
            requestPath,
            """{"name":"bad","providerName":"codex","prompt":"test","unknown":true}""");

        var result = await RunAsync(
            ["request", "validate", requestPath, "--output", "jsonl"]);

        Assert.Equal((int)CliExitCode.ValidationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        using var diagnostic = JsonDocument.Parse(result.StandardError.Trim());
        Assert.Equal(
            "diagnostic",
            diagnostic.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SchemaPrintReturnsEmbeddedJsonSchema()
    {
        var result = await RunAsync(["schema", "print"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
        using var schema = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "urn:createagent:agent-run-request",
            schema.RootElement.GetProperty("$id").GetString());
    }

    [Fact]
    public async Task ProbeUsesConfiguredCodexExecutable()
    {
        var result = await RunAsync(
            [
                "provider",
                "probe",
                "codex",
                "--codex-path",
                FakeCliPath(),
                "--output",
                "jsonl",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
        using var capabilities = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(
            "provider-capabilities",
            capabilities.RootElement.GetProperty("type").GetString());
        Assert.True(
            capabilities.RootElement
                .GetProperty("data")
                .GetProperty("isAvailable")
                .GetBoolean());
    }

    [Fact]
    public async Task ProbeUsesConfiguredClaudeExecutable()
    {
        var result = await RunAsync(
            [
                "provider",
                "probe",
                "claude",
                "--claude-path",
                FakeCliPath(),
                "--output",
                "jsonl",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
        using var capabilities = JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(
            "anthropic.claude",
            capabilities.RootElement
                .GetProperty("data")
                .GetProperty("providerId")
                .GetString());
    }

    [Fact]
    public async Task RunStreamsJsonLinesAndCanBeInspected()
    {
        using var temporary = new TemporaryDirectory();
        var requestPath = Path.Combine(temporary.Path, "request.json");
        var storage = Path.Combine(temporary.Path, "storage");
        await File.WriteAllTextAsync(
            requestPath,
            CreateAgentJson.SerializeRequest(CreateRequest()));

        var run = await RunAsync(
            [
                "run",
                requestPath,
                "--codex-path",
                FakeCliPath(),
                "--storage",
                storage,
                "--output",
                "jsonl",
            ]);

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        var lines = run.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(
            lines,
            static line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.GetProperty("type").GetString() == "event";
            });
        var resultLine = Assert.Single(lines, IsResultLine);
        using var resultJson = JsonDocument.Parse(resultLine);
        var workspace = resultJson.RootElement
            .GetProperty("data")
            .GetProperty("workspacePath")
            .GetString()!;
        var runDirectory = Directory.GetParent(workspace)!.FullName;

        var inspected = await RunAsync(
            ["inspect", runDirectory, "--output", "jsonl"]);

        Assert.Equal((int)CliExitCode.Success, inspected.ExitCode);
        using var inspectedJson = JsonDocument.Parse(inspected.StandardOutput.Trim());
        Assert.Equal(
            "result",
            inspectedJson.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RunExecutesClaudeThroughTheSharedContract()
    {
        using var temporary = new TemporaryDirectory();
        var requestPath = Path.Combine(temporary.Path, "claude-request.json");
        var storage = Path.Combine(temporary.Path, "storage");
        await File.WriteAllTextAsync(
            requestPath,
            CreateAgentJson.SerializeRequest(
                CreateRequest() with { ProviderName = "claude" }));

        var run = await RunAsync(
            [
                "run",
                requestPath,
                "--claude-path",
                FakeCliPath(),
                "--storage",
                storage,
                "--output",
                "jsonl",
            ]);

        Assert.Equal((int)CliExitCode.Success, run.ExitCode);
        Assert.Contains("fake claude completed", run.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.GetFiles(storage, "*", SearchOption.AllDirectories),
            static path => path.EndsWith(
                ".claude-debug.tmp.log",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostTemplateIncludesBothProviderExecutables()
    {
        var result = await RunAsync(["host", "init", "--output", "-"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
        using var host = JsonDocument.Parse(result.StandardOutput);
        var providers = host.RootElement.GetProperty("providers");
        Assert.Equal(
            "codex",
            providers.GetProperty("codex").GetProperty("executable").GetString());
        Assert.Equal(
            "claude",
            providers.GetProperty("claude").GetProperty("executable").GetString());
    }

    [Fact]
    public async Task InvalidOutputFormatReturnsUsageWithoutThrowing()
    {
        var result = await RunAsync(["doctor", "--output", "xml"]);

        Assert.Equal((int)CliExitCode.Usage, result.ExitCode);
        Assert.Contains(
            "Unsupported output format",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostInitRefusesOverwriteWithoutForce()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "host.json");
        var first = await RunAsync(["host", "init", "--output", path]);
        var second = await RunAsync(["host", "init", "--output", path]);
        var forced = await RunAsync(
            ["host", "init", "--output", path, "--force"]);

        Assert.Equal((int)CliExitCode.Success, first.ExitCode);
        Assert.Equal((int)CliExitCode.Usage, second.ExitCode);
        Assert.Equal((int)CliExitCode.Success, forced.ExitCode);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"1.0","providers":null}""")]
    [InlineData("""{"schemaVersion":"1.0","providers":{"codex":null}}""")]
    public async Task InvalidHostProviderConfigurationReturnsValidation(
        string hostJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "host.json");
        await File.WriteAllTextAsync(path, hostJson);

        var result = await RunAsync(
            ["doctor", "--config", path, "--output", "jsonl"]);

        Assert.Equal((int)CliExitCode.ValidationFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        using var diagnostic = JsonDocument.Parse(result.StandardError.Trim());
        Assert.Contains(
            "Providers.Codex.Executable",
            diagnostic.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    private static AgentRunRequest CreateRequest() =>
        new()
        {
            Name = "CLI integration test",
            ProviderName = "codex",
            Prompt = "Complete the fake CLI integration test.",
        };

    private static string FakeCliPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "CreateAgent.Testing.FakeCli.exe"
                : "CreateAgent.Testing.FakeCli");

    private static bool IsResultLine(string line)
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.GetProperty("type").GetString() == "result";
    }

    private static async Task<CliRunResult> RunAsync(string[] args)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var exitCode = await CliApplication.RunAsync(
            args,
            standardOutput,
            standardError);
        return new CliRunResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private sealed record CliRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "createagent-cli-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

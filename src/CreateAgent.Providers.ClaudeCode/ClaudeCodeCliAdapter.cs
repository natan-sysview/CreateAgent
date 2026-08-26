using System.Text.Json;

namespace CreateAgent.Providers.ClaudeCode;

public sealed class ClaudeCodeCliAdapter : IAgentCliAdapter
{
    private static readonly HashSet<string> PerformanceModes =
        new(StringComparer.OrdinalIgnoreCase) { "normal" };
    private static readonly HashSet<string> ReasoningEfforts =
        new(StringComparer.OrdinalIgnoreCase) { "medium" };

    private readonly IAgentProcessRunner _processRunner;
    private readonly string _executable;

    public ClaudeCodeCliAdapter(
        IAgentProcessRunner processRunner,
        string executable = "claude")
    {
        _processRunner = processRunner ??
                         throw new ArgumentNullException(nameof(processRunner));
        _executable = executable;
    }

    public AgentProviderId ProviderId { get; } = new("anthropic.claude");

    public async Task<AgentProviderCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = ExecutableLocator.Find(_executable);
        if (executablePath is null)
        {
            return Unavailable($"Claude executable '{_executable}' was not found in PATH.");
        }

        var output = new List<string>();
        var errors = new List<string>();
        var probeDirectory = Path.Combine(
            Path.GetTempPath(),
            "createagent-claude-probe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        try
        {
            var result = await _processRunner.RunAsync(
                    new AgentCliLaunchPlan(
                        executablePath,
                        ["--version"],
                        Environment.CurrentDirectory,
                        BuildEnvironment(
                            executablePath,
                            Path.Combine(probeDirectory, "debug.log")),
                        AgentInputMode.None,
                        AgentOutputProtocol.Text),
                    null,
                    (line, _) =>
                    {
                        if (line.Stream == AgentOutputChannel.StandardOutput)
                        {
                            output.Add(line.Text);
                        }
                        else
                        {
                            errors.Add(line.Text);
                        }

                        return ValueTask.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                var detail = errors.Count == 0
                    ? null
                    : string.Join(" | ", errors.Take(12));
                return Unavailable(
                    detail is null
                        ? $"Claude --version exited with code {result.ExitCode}."
                        : $"Claude --version exited with code {result.ExitCode}: {detail}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(exception.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(probeDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The probe result is more important than temporary cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // The probe result is more important than temporary cleanup.
            }
        }

        return new AgentProviderCapabilities(
            ProviderId,
            output.FirstOrDefault(),
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            PerformanceModes,
            ReasoningEfforts);
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
        ValidateRequestCapabilities(request);

        var executablePath = ExecutableLocator.Find(_executable) ??
                             throw new AgentProviderUnavailableException(
                                 $"Claude executable '{_executable}' was not found in PATH.");
        var debugPath = Path.Combine(
            workspace.AuditDirectory,
            ".claude-debug.tmp.log");
        var arguments = BuildArguments(request, workspace);

        return Task.FromResult(
            new AgentCliLaunchPlan(
                executablePath,
                arguments,
                workspace.WorkingDirectory,
                BuildEnvironment(executablePath, debugPath),
                AgentInputMode.StandardInput,
                AgentOutputProtocol.JsonLines)
            {
                TemporaryFiles = [debugPath],
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

        try
        {
            using var document = JsonDocument.Parse(output.Text);
            var root = document.RootElement;
            var type = TryGetString(root, "type") ?? "provider.event";
            var nativeJson = root.GetRawText();

            return type switch
            {
                "system" when string.Equals(
                    TryGetString(root, "subtype"),
                    "init",
                    StringComparison.Ordinal) => new AgentDecodedEvent(
                        AgentEventKind.SessionStarted,
                        "session.started",
                        TryGetString(root, "session_id"),
                        NativePayloadJson: nativeJson),
                "assistant" => DecodeAssistant(root, nativeJson),
                "user" => DecodeUser(root, nativeJson),
                "result" => DecodeResult(root, nativeJson),
                _ => new AgentDecodedEvent(
                    AgentEventKind.ProviderEvent,
                    $"claude.{type}",
                    TryGetString(root, "message"),
                    NativePayloadJson: nativeJson),
            };
        }
        catch (JsonException)
        {
            return new AgentDecodedEvent(
                AgentEventKind.ProviderEvent,
                "provider.stdout",
                output.Text,
                Stream: output.Stream);
        }
    }

    private static List<string> BuildArguments(
        AgentRunRequest request,
        AgentWorkspace workspace)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format",
            "stream-json",
            "--verbose",
            "--permission-mode",
            "acceptEdits",
            "--setting-sources",
            "project,local",
            "--settings",
            BuildSafeSettings(request.Workspace.ReadOnlyPaths),
            "--strict-mcp-config",
            "--no-chrome",
        };

        var mcpConfiguration = Path.Combine(workspace.WorkingDirectory, ".mcp.json");
        if (File.Exists(mcpConfiguration))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfiguration);
        }

        if (!string.Equals(
                request.Model,
                "provider-default",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        AddSessionArguments(arguments, request.Session);

        if (request.OutputSchema is not null)
        {
            arguments.Add("--json-schema");
            arguments.Add(request.OutputSchema.Value.GetRawText());
        }

        AddToolArguments(arguments, request.Tools);
        AddReadOnlyDirectories(arguments, request.Workspace.ReadOnlyPaths);
        return arguments;
    }

    private static void AddSessionArguments(
        List<string> arguments,
        AgentSessionOptions session)
    {
        switch (session.Mode)
        {
            case AgentSessionMode.New:
                break;
            case AgentSessionMode.Resume:
                arguments.Add("--resume");
                arguments.Add(session.Id!);
                break;
            case AgentSessionMode.ContinueLatest:
                arguments.Add("--continue");
                break;
            case AgentSessionMode.Fork:
                arguments.Add("--continue");
                arguments.Add("--fork-session");
                break;
            case AgentSessionMode.Ephemeral:
                arguments.Add("--no-session-persistence");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Claude session mode '{session.Mode}'.");
        }
    }

    private static void AddToolArguments(
        List<string> arguments,
        IReadOnlyList<string> tools)
    {
        arguments.Add("--tools");
        if (tools.Count == 0)
        {
            arguments.Add("default");
            return;
        }

        var value = string.Join(",", tools);
        arguments.Add(value);
        arguments.Add("--allowedTools");
        arguments.Add(value);
    }

    private static void AddReadOnlyDirectories(
        List<string> arguments,
        IReadOnlyList<string> readOnlyPaths)
    {
        foreach (var directory in readOnlyPaths
                     .Where(Directory.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            arguments.Add("--add-dir");
            arguments.Add(directory);
        }
    }

    private static string BuildSafeSettings(IReadOnlyList<string> readOnlyPaths)
    {
        var normalizedPaths = readOnlyPaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var settings = new
        {
            permissions = new
            {
                deny = normalizedPaths.Select(ToEditDenyRule).ToArray(),
                disableBypassPermissionsMode = "disable",
            },
            sandbox = new
            {
                enabled = true,
                failIfUnavailable = true,
                autoAllowBashIfSandboxed = true,
                allowUnsandboxedCommands = false,
                filesystem = new
                {
                    denyWrite = normalizedPaths,
                },
            },
        };

        return JsonSerializer.Serialize(settings);
    }

    private static string ToEditDenyRule(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows() &&
            normalized.Length >= 2 &&
            normalized[1] == ':')
        {
            normalized = string.Concat(
                "/",
                char.ToLowerInvariant(normalized[0]),
                normalized[2..]);
        }

        var suffix = Directory.Exists(path) ? "/**" : string.Empty;
        return $"Edit(/{normalized.TrimEnd('/')}{suffix})";
    }

    private static void ValidateRequestCapabilities(AgentRunRequest request)
    {
        if (request.Performance != AgentPerformanceMode.Normal)
        {
            throw new InvalidOperationException(
                "The verified Claude Code baseline only supports normal performance in CreateAgent.");
        }

        if (!ReasoningEfforts.Contains(request.Reasoning.Effort))
        {
            throw new InvalidOperationException(
                "The verified Claude Code baseline only supports the provider-default medium reasoning level in CreateAgent.");
        }
    }

    private static AgentDecodedEvent DecodeAssistant(
        JsonElement root,
        string nativeJson)
    {
        if (!TryGetContent(root, out var content))
        {
            return ProviderEvent("claude.assistant", nativeJson);
        }

        foreach (var item in content.EnumerateArray())
        {
            var itemType = TryGetString(item, "type");
            if (string.Equals(itemType, "tool_use", StringComparison.Ordinal))
            {
                return new AgentDecodedEvent(
                    AgentEventKind.ToolStarted,
                    "claude.tool.started",
                    TryGetString(item, "name"),
                    NativePayloadJson: nativeJson);
            }

            if (string.Equals(itemType, "text", StringComparison.Ordinal))
            {
                return new AgentDecodedEvent(
                    AgentEventKind.ProviderEvent,
                    "claude.assistant",
                    TryGetString(item, "text"),
                    NativePayloadJson: nativeJson);
            }
        }

        return ProviderEvent("claude.assistant", nativeJson);
    }

    private static AgentDecodedEvent DecodeUser(
        JsonElement root,
        string nativeJson)
    {
        if (TryGetContent(root, out var content))
        {
            foreach (var item in content.EnumerateArray())
            {
                if (string.Equals(
                        TryGetString(item, "type"),
                        "tool_result",
                        StringComparison.Ordinal))
                {
                    return new AgentDecodedEvent(
                        AgentEventKind.ToolCompleted,
                        "claude.tool.completed",
                        TryGetString(item, "content"),
                        NativePayloadJson: nativeJson);
                }
            }
        }

        return ProviderEvent("claude.user", nativeJson);
    }

    private static AgentDecodedEvent DecodeResult(
        JsonElement root,
        string nativeJson)
    {
        var isError = root.TryGetProperty("is_error", out var errorProperty) &&
                      errorProperty.ValueKind == JsonValueKind.True;
        if (isError)
        {
            return new AgentDecodedEvent(
                AgentEventKind.Error,
                "claude.result.error",
                TryGetString(root, "result") ?? TryGetString(root, "subtype"),
                NativePayloadJson: nativeJson);
        }

        if (root.TryGetProperty("structured_output", out var structuredOutput))
        {
            return new AgentDecodedEvent(
                AgentEventKind.AgentMessage,
                "agent.message",
                structuredOutput.GetRawText(),
                NativePayloadJson: nativeJson);
        }

        return new AgentDecodedEvent(
            AgentEventKind.AgentMessage,
            "agent.message",
            TryGetString(root, "result"),
            NativePayloadJson: nativeJson);
    }

    private static bool TryGetContent(
        JsonElement root,
        out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var message) &&
               message.ValueKind == JsonValueKind.Object &&
               message.TryGetProperty("content", out content) &&
               content.ValueKind == JsonValueKind.Array;
    }

    private static AgentDecodedEvent ProviderEvent(
        string name,
        string nativeJson) =>
        new(
            AgentEventKind.ProviderEvent,
            name,
            NativePayloadJson: nativeJson);

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private AgentProviderCapabilities Unavailable(string diagnostic) =>
        new(
            ProviderId,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            PerformanceModes,
            ReasoningEfforts,
            diagnostic);

    private static Dictionary<string, string> BuildEnvironment(
        string executablePath,
        string debugLogPath)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath);
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CLAUDE_CODE_DEBUG_LOGS_DIR"] = debugLogPath,
            };
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_DEBUG_LOGS_DIR"] = debugLogPath,
            ["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                ? executableDirectory
                : string.Concat(
                    executableDirectory,
                    Path.PathSeparator,
                    currentPath),
        };
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

using System.Text.Json;

namespace CreateAgent.Providers.Codex;

public sealed class CodexCliAdapter : IAgentCliAdapter
{
    private static readonly HashSet<string> PerformanceModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "normal",
            "fast",
        };
    private static readonly HashSet<string> ReasoningEfforts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "minimal",
            "low",
            "medium",
            "high",
            "xhigh",
            "max",
            "ultra",
        };

    private readonly IAgentProcessRunner _processRunner;
    private readonly string _executable;

    public CodexCliAdapter(
        IAgentProcessRunner processRunner,
        string executable = "codex")
    {
        _processRunner = processRunner ??
                         throw new ArgumentNullException(nameof(processRunner));
        _executable = executable;
    }

    public AgentProviderId ProviderId { get; } = new("openai.codex");

    public async Task<AgentProviderCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = ExecutableLocator.Find(_executable);
        if (executablePath is null)
        {
            return Unavailable($"Codex executable '{_executable}' was not found in PATH.");
        }

        var output = new List<string>();
        try
        {
            var result = await _processRunner
                .RunAsync(
                    new AgentCliLaunchPlan(
                        executablePath,
                        ["--version"],
                        Environment.CurrentDirectory,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        AgentInputMode.None,
                        AgentOutputProtocol.Text),
                    null,
                    (line, _) =>
                    {
                        if (line.Stream == AgentOutputChannel.StandardOutput)
                        {
                            output.Add(line.Text);
                        }

                        return ValueTask.CompletedTask;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return Unavailable($"Codex --version exited with code {result.ExitCode}.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(exception.Message);
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

    public async Task<AgentCliLaunchPlan> CreateLaunchPlanAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = ExecutableLocator.Find(_executable) ??
                             throw new AgentProviderUnavailableException(
                                 $"Codex executable '{_executable}' was not found in PATH.");
        string? outputSchemaPath = null;
        try
        {
            if (request.OutputSchema is not null)
            {
                outputSchemaPath = Path.Combine(
                    workspace.AuditDirectory,
                    ".output-schema.tmp.json");
            }

            var arguments = BuildArguments(request, outputSchemaPath);
            if (outputSchemaPath is not null)
            {
                await File.WriteAllTextAsync(
                        outputSchemaPath,
                        request.OutputSchema!.Value.GetRawText(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new AgentCliLaunchPlan(
                executablePath,
                arguments,
                workspace.WorkingDirectory,
                new Dictionary<string, string>(StringComparer.Ordinal),
                AgentInputMode.StandardInput,
                AgentOutputProtocol.JsonLines)
            {
                TemporaryFiles = outputSchemaPath is null ? [] : [outputSchemaPath],
            };
        }
        catch
        {
            if (outputSchemaPath is not null)
            {
                File.Delete(outputSchemaPath);
            }

            throw;
        }
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
                "thread.started" => new AgentDecodedEvent(
                    AgentEventKind.SessionStarted,
                    type,
                    TryGetString(root, "thread_id"),
                    NativePayloadJson: nativeJson),
                "item.started" => DecodeItem(root, type, started: true, nativeJson),
                "item.completed" => DecodeItem(root, type, started: false, nativeJson),
                "error" or "turn.failed" => new AgentDecodedEvent(
                    AgentEventKind.Error,
                    type,
                    TryGetString(root, "message") ??
                    TryGetNestedString(root, "error", "message") ??
                    output.Text,
                    NativePayloadJson: nativeJson),
                "turn.completed" => new AgentDecodedEvent(
                    AgentEventKind.Progress,
                    type,
                    "Codex turn completed.",
                    Progress: 1,
                    NativePayloadJson: nativeJson),
                _ => new AgentDecodedEvent(
                    AgentEventKind.ProviderEvent,
                    type,
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
        string? outputSchemaPath)
    {
        var arguments = new List<string> { "exec" };
        ValidateRequestCapabilities(request);
        var isResume = request.Session.Mode is AgentSessionMode.Resume
            or AgentSessionMode.ContinueLatest;

        if (isResume)
        {
            arguments.Add("resume");
            if (request.Session.Mode == AgentSessionMode.Resume)
            {
                arguments.Add(request.Session.Id!);
            }
            else
            {
                arguments.Add("--last");
            }
        }

        arguments.Add("--json");
        arguments.Add("--skip-git-repo-check");

        arguments.Add("--ignore-user-config");

        if (!string.Equals(
                request.Model,
                "provider-default",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        if (request.Workspace.ReadOnlyPaths.Count > 0)
        {
            AddReadOnlyPermissions(arguments, request.Workspace.ReadOnlyPaths);
        }
        else if (isResume)
        {
            AddConfigOverride(arguments, "sandbox_mode", "workspace-write");
        }
        else
        {
            arguments.Add("--sandbox");
            arguments.Add("workspace-write");
        }

        AddConfigOverride(arguments, "approval_policy", "never");

        if (!ReasoningEfforts.Contains(request.Reasoning.Effort))
        {
            throw new InvalidOperationException(
                $"Unsupported Codex reasoning effort '{request.Reasoning.Effort}'.");
        }

        AddConfigOverride(
            arguments,
            "model_reasoning_effort",
            request.Reasoning.Effort);

        if (request.Performance == AgentPerformanceMode.Fast)
        {
            AddConfigOverride(arguments, "service_tier", "fast");
        }
        else if (request.Performance == AgentPerformanceMode.Medium)
        {
            throw new InvalidOperationException(
                "Codex does not provide a distinct medium performance mode.");
        }

        if (request.Session.Mode == AgentSessionMode.Ephemeral)
        {
            arguments.Add("--ephemeral");
        }

        if (outputSchemaPath is not null)
        {
            arguments.Add("--output-schema");
            arguments.Add(outputSchemaPath);
        }

        arguments.Add("-");
        return arguments;
    }

    private static AgentDecodedEvent DecodeItem(
        JsonElement root,
        string eventName,
        bool started,
        string nativeJson)
    {
        if (!root.TryGetProperty("item", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            return new AgentDecodedEvent(
                AgentEventKind.ProviderEvent,
                eventName,
                NativePayloadJson: nativeJson);
        }

        var itemType = TryGetString(item, "type") ?? "item";
        if (!started && string.Equals(itemType, "agent_message", StringComparison.Ordinal))
        {
            return new AgentDecodedEvent(
                AgentEventKind.AgentMessage,
                "agent.message",
                TryGetString(item, "text") ?? TryGetString(item, "message"),
                NativePayloadJson: nativeJson);
        }

        var isTool = itemType is "command_execution" or "mcp_tool_call" or "web_search";
        return new AgentDecodedEvent(
            isTool
                ? started
                    ? AgentEventKind.ToolStarted
                    : AgentEventKind.ToolCompleted
                : AgentEventKind.ProviderEvent,
            $"{itemType}.{(started ? "started" : "completed")}",
            TryGetString(item, "command") ??
            TryGetString(item, "name") ??
            TryGetString(item, "text"),
            NativePayloadJson: nativeJson);
    }

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

    private static string? TryGetNestedString(
        JsonElement element,
        string objectName,
        string propertyName) =>
        element.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? TryGetString(nested, propertyName)
            : null;

    private static void AddConfigOverride(
        List<string> arguments,
        string key,
        string value)
    {
        arguments.Add("--config");
        arguments.Add(
            string.Concat(
                key,
                "='",
                value.Replace("'", "''", StringComparison.Ordinal),
                "'"));
    }

    private static void AddReadOnlyPermissions(
        List<string> arguments,
        IReadOnlyList<string> paths)
    {
        const string profileName = "createagent_readonly";
        AddConfigOverride(arguments, "default_permissions", profileName);
        AddConfigOverride(
            arguments,
            $"permissions.{profileName}.extends",
            ":workspace");

        var entries = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Select(path => $"{ToTomlString(path)}={ToTomlString("read")}");
        arguments.Add("--config");
        arguments.Add(
            $"permissions.{profileName}.filesystem={{{string.Join(",", entries)}}}");
    }

    private static string ToTomlString(string value) =>
        string.Concat(
            "\"",
            value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal),
            "\"");

    private static void ValidateRequestCapabilities(AgentRunRequest request)
    {
        if (request.Session.Mode == AgentSessionMode.Fork)
        {
            throw new InvalidOperationException(
                "Codex exec does not expose non-interactive session fork in the v1 baseline.");
        }

        if (request.Tools.Count > 0)
        {
            throw new InvalidOperationException(
                "Tool profiles require a host resolver and are not materialized by the Codex v1 adapter.");
        }
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
}

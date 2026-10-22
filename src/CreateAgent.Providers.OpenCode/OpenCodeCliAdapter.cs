using System.Text.Json;

namespace CreateAgent.Providers.OpenCode;

public sealed class OpenCodeCliAdapter : IAgentCliAdapter
{
    public const string FlashModel = "deepseek/deepseek-flash";
    public const string ProModel = "deepseek/deepseek-v4-pro";

    private static readonly HashSet<string> PerformanceModes =
        new(StringComparer.OrdinalIgnoreCase) { "normal" };
    private static readonly HashSet<string> ReasoningEfforts =
        new(StringComparer.OrdinalIgnoreCase) { "low", "high", "max" };
    private static readonly HashSet<string> ReadOnlyPathCompatibleTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "read",
            "edit",
            "glob",
            "grep",
            "list",
            "webfetch",
            "websearch",
        };
    private static readonly HashSet<string> DirectoryOnlyTools =
        new(StringComparer.OrdinalIgnoreCase) { "glob", "grep", "list" };
    private static readonly Dictionary<string, IReadOnlySet<string>>
        ModelEfforts = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [FlashModel] = new HashSet<string>(
                ["low", "high", "max"],
                StringComparer.OrdinalIgnoreCase),
            [ProModel] = new HashSet<string>(
                ["high", "max"],
                StringComparer.OrdinalIgnoreCase),
        };

    private readonly IAgentProcessRunner _processRunner;
    private readonly string _executable;

    public OpenCodeCliAdapter(
        IAgentProcessRunner processRunner,
        string executable = "opencode")
    {
        _processRunner = processRunner ??
                         throw new ArgumentNullException(nameof(processRunner));
        _executable = executable;
    }

    public AgentProviderId ProviderId { get; } = new("anomaly.opencode");

    public async Task<AgentProviderCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = ExecutableLocator.Find(_executable);
        if (executablePath is null)
        {
            return Unavailable(
                $"OpenCode executable '{_executable}' was not found in PATH.");
        }

        var output = new List<string>();
        var errors = new List<string>();
        try
        {
            var result = await _processRunner.RunAsync(
                    new AgentCliLaunchPlan(
                        executablePath,
                        ["--version"],
                        Environment.CurrentDirectory,
                        BuildEnvironment(executablePath, null, []),
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
                        ? $"OpenCode --version exited with code {result.ExitCode}."
                        : $"OpenCode --version exited with code {result.ExitCode}: {detail}");
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

    public Task<AgentCliLaunchPlan> CreateLaunchPlanAsync(
        AgentRunRequest request,
        AgentWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequestCapabilities(request);
        var executablePath = ExecutableLocator.Find(_executable) ??
                             throw new AgentProviderUnavailableException(
                                 $"OpenCode executable '{_executable}' was not found in PATH.");

        var arguments = new List<string>
        {
            "run",
            "--pure",
            "--format",
            "json",
            "--model",
            request.Model,
            "--variant",
            request.Reasoning.Effort,
            "--dir",
            workspace.WorkingDirectory,
            "--title",
            request.Name,
        };
        if (request.Debug)
        {
            arguments.Add("--thinking");
        }

        if (request.Session.Mode == AgentSessionMode.Resume)
        {
            arguments.Add("--session");
            arguments.Add(request.Session.Id!);
        }

        return Task.FromResult(
            new AgentCliLaunchPlan(
                executablePath,
                arguments,
                workspace.WorkingDirectory,
                BuildEnvironment(
                    executablePath,
                    request.Tools,
                    request.Workspace.ReadOnlyPaths),
                AgentInputMode.StandardInput,
                AgentOutputProtocol.JsonLines));
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
                "step_start" => new AgentDecodedEvent(
                    AgentEventKind.SessionStarted,
                    "session.started",
                    TryGetString(root, "sessionID"),
                    NativePayloadJson: nativeJson),
                "reasoning" => new AgentDecodedEvent(
                    AgentEventKind.ProviderEvent,
                    "opencode.reasoning",
                    TryGetNestedString(root, "part", "text"),
                    NativePayloadJson: nativeJson),
                "text" => new AgentDecodedEvent(
                    AgentEventKind.AgentMessage,
                    "agent.message",
                    TryGetNestedString(root, "part", "text"),
                    NativePayloadJson: nativeJson),
                "tool_use" => DecodeTool(root, nativeJson),
                "step_finish" => new AgentDecodedEvent(
                    AgentEventKind.Progress,
                    "opencode.step.finished",
                    TryGetNestedString(root, "part", "reason"),
                    Progress: string.Equals(
                        TryGetNestedString(root, "part", "reason"),
                        "stop",
                        StringComparison.Ordinal)
                        ? 1
                        : null,
                    NativePayloadJson: nativeJson),
                "error" => new AgentDecodedEvent(
                    AgentEventKind.Error,
                    "opencode.error",
                    TryGetString(root, "message") ??
                    TryGetNestedString(root, "error", "data", "message") ??
                    TryGetNestedString(root, "error", "message") ??
                    output.Text,
                    NativePayloadJson: nativeJson),
                _ => new AgentDecodedEvent(
                    AgentEventKind.ProviderEvent,
                    $"opencode.{type}",
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

    private static AgentDecodedEvent DecodeTool(
        JsonElement root,
        string nativeJson)
    {
        var tool = TryGetNestedString(root, "part", "tool");
        var status = TryGetNestedString(root, "part", "state", "status");
        return status switch
        {
            "pending" or "running" => new AgentDecodedEvent(
                AgentEventKind.ToolStarted,
                "opencode.tool.started",
                tool,
                NativePayloadJson: nativeJson),
            "error" => new AgentDecodedEvent(
                AgentEventKind.Error,
                "opencode.tool.error",
                TryGetNestedString(root, "part", "state", "error") ?? tool,
                NativePayloadJson: nativeJson),
            _ => new AgentDecodedEvent(
                AgentEventKind.ToolCompleted,
                "opencode.tool.completed",
                tool,
                NativePayloadJson: nativeJson),
        };
    }

    private static void ValidateRequestCapabilities(AgentRunRequest request)
    {
        if (!ModelEfforts.TryGetValue(request.Model, out var modelEfforts))
        {
            throw new InvalidOperationException(
                $"OpenCode model '{request.Model}' is not supported. Use '{FlashModel}' or '{ProModel}'.");
        }

        if (request.Performance != AgentPerformanceMode.Normal)
        {
            throw new InvalidOperationException(
                "The verified OpenCode DeepSeek models only support normal performance in CreateAgent. Choose Flash explicitly through model when speed is required.");
        }

        if (!modelEfforts.Contains(request.Reasoning.Effort))
        {
            throw new InvalidOperationException(
                $"OpenCode model '{request.Model}' does not support reasoning effort '{request.Reasoning.Effort}'.");
        }

        if (request.OutputSchema is not null)
        {
            throw new InvalidOperationException(
                "OpenCode CLI 1.18.30 does not provide a verified native output schema option.");
        }

        ValidateReadOnlyPaths(request);

        if (request.Session.Mode is not AgentSessionMode.New and not AgentSessionMode.Resume)
        {
            throw new InvalidOperationException(
                "OpenCode supports session.mode 'new' and explicit 'resume' in CreateAgent. Ephemeral, continue-latest and fork are not verified.");
        }

        if (request.Tools.Any(
                static tool => tool.Equals("question", StringComparison.OrdinalIgnoreCase) ||
                               tool.Equals(
                                   "external_directory",
                                   StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "OpenCode tools 'question' and 'external_directory' are reserved by the non-interactive adapter.");
        }
    }

    private static void ValidateReadOnlyPaths(AgentRunRequest request)
    {
        if (request.Workspace.ReadOnlyPaths.Count == 0)
        {
            return;
        }

        if (request.Tools.Count == 0)
        {
            throw new InvalidOperationException(
                "OpenCode read-only paths require an explicit tools allowlist. Use query tools and optionally 'edit' for workspace-only changes.");
        }

        var unsupported = request.Tools
            .Where(tool => !ReadOnlyPathCompatibleTools.Contains(tool))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"OpenCode read-only paths cannot be combined with tools that can bypass path permissions: {string.Join(", ", unsupported)}.");
        }

        if (!request.Tools.Any(
                static tool =>
                    tool.Equals("read", StringComparison.OrdinalIgnoreCase) ||
                    tool.Equals("glob", StringComparison.OrdinalIgnoreCase) ||
                    tool.Equals("grep", StringComparison.OrdinalIgnoreCase) ||
                    tool.Equals("list", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "OpenCode read-only paths require at least one local query tool: read, glob, grep or list.");
        }

        if (request.Workspace.ReadOnlyPaths.Any(File.Exists) &&
            request.Tools.Any(DirectoryOnlyTools.Contains))
        {
            throw new InvalidOperationException(
                "OpenCode individual read-only files support the 'read' tool only. Glob, grep and list require directory paths.");
        }
    }

    private static Dictionary<string, string> BuildEnvironment(
        string executablePath,
        IReadOnlyList<string>? tools,
        IReadOnlyList<string> readOnlyPaths)
    {
        var permissions = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["*"] = tools is null || tools.Count == 0 ? "allow" : "deny",
        };
        if (tools is not null)
        {
            foreach (var tool in tools.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                permissions[tool] = "allow";
            }
        }

        permissions["question"] = "deny";
        if (readOnlyPaths.Count == 0)
        {
            permissions["external_directory"] = "deny";
        }
        else
        {
            permissions["edit"] = tools is not null &&
                                  tools.Contains(
                                      "edit",
                                      StringComparer.OrdinalIgnoreCase)
                ? BuildEditRules(readOnlyPaths)
                : "deny";
            permissions["bash"] = "deny";
            permissions["external_directory"] =
                BuildExternalDirectoryRules(readOnlyPaths);

            var filePaths = readOnlyPaths
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToArray();
            if (filePaths.Length > 0)
            {
                permissions["read"] = BuildFileReadRules(
                    readOnlyPaths,
                    filePaths);
            }
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_AUTO_SHARE"] = "false",
            ["OPENCODE_CLIENT"] = "createagent",
            ["OPENCODE_CONFIG_CONTENT"] = JsonSerializer.Serialize(
                new { permission = permissions }),
            ["OPENCODE_DISABLE_AUTOUPDATE"] = "true",
            ["OPENCODE_DISABLE_TERMINAL_TITLE"] = "true",
        };
        var executableDirectory = Path.GetDirectoryName(executablePath);
        var currentPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                ? executableDirectory
                : string.Concat(
                    executableDirectory,
                    Path.PathSeparator,
                    currentPath);
        }

        return environment;
    }

    private static Dictionary<string, string> BuildEditRules(
        IReadOnlyList<string> readOnlyPaths)
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "allow",
        };
        foreach (var path in readOnlyPaths
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            var isDirectory = Directory.Exists(path);
            rules[isDirectory
                ? ToAbsoluteChildrenPattern(path)
                : NormalizeForOpenCode(path)] = "deny";
            rules[ToRelativeSuffixPattern(path, isDirectory)] = "deny";
        }

        return rules;
    }

    private static Dictionary<string, string> BuildExternalDirectoryRules(
        IReadOnlyList<string> readOnlyPaths)
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "deny",
        };
        foreach (var path in readOnlyPaths
                     .Select(Path.GetFullPath)
                     .Distinct(PathComparer))
        {
            var directory = Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path)!;
            rules[ToAbsoluteChildrenPattern(directory)] = "allow";
        }

        return rules;
    }

    private static Dictionary<string, string> BuildFileReadRules(
        IReadOnlyList<string> readOnlyPaths,
        IReadOnlyList<string> filePaths)
    {
        var directoryPaths = readOnlyPaths
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        var fileParents = filePaths
            .Select(Path.GetDirectoryName)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Where(parent => !directoryPaths.Any(directory =>
                IsWithinDirectory(parent, directory)))
            .Distinct(PathComparer)
            .ToArray();

        var rules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["*"] = "allow",
        };
        foreach (var parent in fileParents)
        {
            rules[ToRelativeSuffixPattern(parent, includeChildren: true)] =
                "deny";
        }

        foreach (var directory in directoryPaths)
        {
            rules[ToRelativeSuffixPattern(directory, includeChildren: true)] =
                "allow";
        }

        foreach (var file in filePaths)
        {
            rules[ToRelativeSuffixPattern(file, includeChildren: false)] =
                "allow";
        }

        return rules;
    }

    private static string ToAbsoluteChildrenPattern(string path) =>
        string.Concat(NormalizeForOpenCode(path).TrimEnd('/'), "/*");

    private static string ToRelativeSuffixPattern(
        string path,
        bool includeChildren)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var suffix = fullPath[root.Length..]
            .Replace('\\', '/')
            .Trim('/');
        if (suffix.Length == 0)
        {
            return "*";
        }

        return includeChildren
            ? $"*{suffix}/*"
            : $"*{suffix}";
    }

    private static string NormalizeForOpenCode(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith(
                    string.Concat("..", Path.DirectorySeparatorChar),
                    StringComparison.Ordinal) &&
                !relative.StartsWith(
                    string.Concat("..", Path.AltDirectorySeparatorChar),
                    StringComparison.Ordinal));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
        params string[] path)
    {
        foreach (var propertyName in path)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
    }
}

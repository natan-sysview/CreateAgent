using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CreateAgent.Core;
using CreateAgent.Providers.Codex;
using CreateAgent.Runtime.Processes;

namespace CreateAgent.Cli;

public static class CliApplication
{
    private static readonly HashSet<string> CommonValueOptions =
        new(StringComparer.Ordinal)
        {
            "--config",
            "--storage",
            "--codex-path",
            "--max-concurrent",
            "--event-capacity",
            "--process-output-capacity",
            "--output",
        };

    private static readonly HashSet<string> HelpFlag =
        new(StringComparer.Ordinal) { "--help" };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        CliOutput output;
        try
        {
            output = new CliOutput(
                TryDetectFormat(args),
                standardOutput,
                standardError);
        }
        catch (CliUsageException exception)
        {
            output = new CliOutput(
                CliOutputFormat.Human,
                standardOutput,
                standardError);
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }

        try
        {
            if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
            {
                await standardOutput.WriteLineAsync(HelpText).ConfigureAwait(false);
                return (int)CliExitCode.Success;
            }

            if (args[0] is "--version" or "version")
            {
                await standardOutput.WriteLineAsync(GetVersion()).ConfigureAwait(false);
                return (int)CliExitCode.Success;
            }

            return args[0] switch
            {
                "run" => await RunAgentAsync(args.Skip(1).ToArray(), output, cancellationToken)
                    .ConfigureAwait(false),
                "request" => await RunRequestCommandAsync(
                        args.Skip(1).ToArray(),
                        output,
                        standardOutput,
                        cancellationToken)
                    .ConfigureAwait(false),
                "host" => await RunHostCommandAsync(
                        args.Skip(1).ToArray(),
                        output,
                        standardOutput,
                        cancellationToken)
                    .ConfigureAwait(false),
                "provider" => await RunProviderCommandAsync(
                        args.Skip(1).ToArray(),
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                "doctor" => await RunDoctorAsync(
                        args.Skip(1).ToArray(),
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                "schema" => await RunSchemaCommandAsync(
                        args.Skip(1).ToArray(),
                        standardOutput,
                        cancellationToken)
                    .ConfigureAwait(false),
                "inspect" => await RunInspectAsync(
                        args.Skip(1).ToArray(),
                        output,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new CliUsageException(
                    $"Unknown command '{args[0]}'. Run 'createagent help'."),
            };
        }
        catch (CliUsageException exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }
        catch (CreateAgentValidationException exception)
        {
            foreach (var error in exception.Errors)
            {
                await output.WriteDiagnosticAsync("error", error).ConfigureAwait(false);
            }

            return (int)CliExitCode.ValidationFailed;
        }
        catch (JsonException exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.ValidationFailed;
        }
        catch (FileNotFoundException exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.ValidationFailed;
        }
        catch (DirectoryNotFoundException exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.ValidationFailed;
        }
        catch (AgentProviderUnavailableException exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.ProviderUnavailable;
        }
        catch (OperationCanceledException)
        {
            await output.WriteDiagnosticAsync("warning", "Operation cancelled.")
                .ConfigureAwait(false);
            return (int)CliExitCode.Cancelled;
        }
        catch (Exception exception)
        {
            await output.WriteDiagnosticAsync("error", exception.Message)
                .ConfigureAwait(false);
            return (int)CliExitCode.InternalError;
        }
    }

    private static async Task<int> RunAgentAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(args, CommonValueOptions, HelpFlag);
        if (parsed.HasFlag("--help"))
        {
            throw new CliUsageException(
                "Usage: createagent run <request.json> [host options] [--output human|jsonl]");
        }

        RequirePositionals(parsed, 1, "run <request.json>");
        var request = await RequestFileLoader
            .LoadAsync(parsed.Positionals[0], cancellationToken)
            .ConfigureAwait(false);
        AgentRunRequestValidation.Validate(request);
        var host = await CliHostConfigurationLoader
            .LoadAsync(parsed, cancellationToken)
            .ConfigureAwait(false);

        await using var manager = CreateAgentRuntime.CreateDefault(
            host.ToCreateAgentOptions(),
            host.CodexExecutable);
        var handle = await manager.StartAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var runHandle = (IAgentRunHandle)state!;
                _ = runHandle.CancelAsync("Cancelled by CLI host.");
            },
            handle);

        var eventTask = ConsumeEventsAsync(handle, output);
        var result = await handle.Completion.ConfigureAwait(false);
        await eventTask.ConfigureAwait(false);
        await output.WriteResultAsync(result).ConfigureAwait(false);
        return MapRunExitCode(result.State);
    }

    private static async Task<int> RunRequestCommandAsync(
        string[] args,
        CliOutput output,
        TextWriter standardOutput,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException(
                "Usage: createagent request <init|validate> ...");
        }

        return args[0] switch
        {
            "init" => await InitializeRequestAsync(
                    args.Skip(1).ToArray(),
                    output,
                    standardOutput,
                    cancellationToken)
                .ConfigureAwait(false),
            "validate" => await ValidateRequestAsync(
                    args.Skip(1).ToArray(),
                    output,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new CliUsageException(
                $"Unknown request command '{args[0]}'."),
        };
    }

    private static async Task<int> InitializeRequestAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        TextWriter standardOutput,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(
            args,
            new HashSet<string>(StringComparer.Ordinal) { "--provider", "--output" },
            new HashSet<string>(StringComparer.Ordinal) { "--force", "--help" });
        if (parsed.HasFlag("--help"))
        {
            throw new CliUsageException(
                "Usage: createagent request init --provider codex [--output request.json] [--force]");
        }

        RequirePositionals(parsed, 0, "request init");
        var provider = parsed.GetValue("--provider") ?? "codex";
        var json = CreateAgentJson.SerializeRequest(
            RequestTemplateFactory.Create(provider));
        var path = parsed.GetValue("--output");
        if (path is null || path == "-")
        {
            await standardOutput.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await AtomicFileWriter
                .WriteAsync(
                    path,
                    json,
                    parsed.HasFlag("--force"),
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteStatusAsync(
                    "request-created",
                    $"Request template written to {Path.GetFullPath(path)}.")
                .ConfigureAwait(false);
        }

        return (int)CliExitCode.Success;
    }

    private static async Task<int> ValidateRequestAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(
            args,
            new HashSet<string>(StringComparer.Ordinal) { "--output" },
            HelpFlag);
        RequirePositionals(parsed, 1, "request validate <request.json>");
        var request = await RequestFileLoader
            .LoadAsync(parsed.Positionals[0], cancellationToken)
            .ConfigureAwait(false);
        AgentRunRequestValidation.Validate(request);
        await output.WriteStatusAsync(
                "request-valid",
                "Request is valid.",
                new
                {
                    request.Name,
                    provider = request.ProviderName,
                })
            .ConfigureAwait(false);
        return (int)CliExitCode.Success;
    }

    private static async Task<int> RunHostCommandAsync(
        string[] args,
        CliOutput output,
        TextWriter standardOutput,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "init")
        {
            throw new CliUsageException(
                "Usage: createagent host init [--output host.json] [--force]");
        }

        var parsed = CliArguments.Parse(
            args.Skip(1).ToArray(),
            new HashSet<string>(StringComparer.Ordinal) { "--output" },
            new HashSet<string>(StringComparer.Ordinal) { "--force", "--help" });
        RequirePositionals(parsed, 0, "host init");
        var json = CliHostConfigurationLoader.SerializeTemplate();
        var path = parsed.GetValue("--output");
        if (path is null || path == "-")
        {
            await standardOutput.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await AtomicFileWriter
                .WriteAsync(
                    path,
                    json,
                    parsed.HasFlag("--force"),
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteStatusAsync(
                    "host-created",
                    $"Host configuration written to {Path.GetFullPath(path)}.")
                .ConfigureAwait(false);
        }

        return (int)CliExitCode.Success;
    }

    private static async Task<int> RunProviderCommandAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(args, CommonValueOptions, HelpFlag);
        RequirePositionals(parsed, 2, "provider probe <codex|openai.codex>");
        if (parsed.Positionals[0] != "probe")
        {
            throw new CliUsageException(
                $"Unknown provider command '{parsed.Positionals[0]}'.");
        }

        EnsureCodexProvider(parsed.Positionals[1]);
        var host = await CliHostConfigurationLoader
            .LoadAsync(parsed, cancellationToken)
            .ConfigureAwait(false);
        var runner = new SystemProcessRunner(
            host.ProcessOutputChannelCapacity);
        var adapter = new CodexCliAdapter(runner, host.CodexExecutable);
        var capabilities = await adapter.ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteCapabilitiesAsync(capabilities).ConfigureAwait(false);
        return capabilities.IsAvailable
            ? (int)CliExitCode.Success
            : (int)CliExitCode.ProviderUnavailable;
    }

    private static async Task<int> RunDoctorAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(args, CommonValueOptions, HelpFlag);
        RequirePositionals(parsed, 0, "doctor");
        var host = await CliHostConfigurationLoader
            .LoadAsync(parsed, cancellationToken)
            .ConfigureAwait(false);
        Directory.CreateDirectory(host.StorageRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                host.StorageRoot,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        var runner = new SystemProcessRunner(host.ProcessOutputChannelCapacity);
        var capabilities = await new CodexCliAdapter(runner, host.CodexExecutable)
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteStatusAsync(
                "doctor",
                capabilities.IsAvailable
                    ? "CreateAgent host is ready."
                    : "CreateAgent host requires attention.",
                new
                {
                    runtime = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    storageRoot = host.StorageRoot,
                    provider = capabilities,
                })
            .ConfigureAwait(false);
        if (output.Format == CliOutputFormat.Human)
        {
            await output.WriteCapabilitiesAsync(capabilities).ConfigureAwait(false);
        }

        return capabilities.IsAvailable
            ? (int)CliExitCode.Success
            : (int)CliExitCode.ProviderUnavailable;
    }

    private static async Task<int> RunSchemaCommandAsync(
        string[] args,
        TextWriter standardOutput,
        CancellationToken cancellationToken)
    {
        if (args.Length != 1 || args[0] != "print")
        {
            throw new CliUsageException("Usage: createagent schema print");
        }

        await standardOutput.WriteAsync(
                await SchemaResource.ReadAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        return (int)CliExitCode.Success;
    }

    private static async Task<int> RunInspectAsync(
        IReadOnlyList<string> args,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var parsed = CliArguments.Parse(
            args,
            new HashSet<string>(StringComparer.Ordinal) { "--output" },
            HelpFlag);
        RequirePositionals(parsed, 1, "inspect <run-directory|output.json>");
        var suppliedPath = Path.GetFullPath(parsed.Positionals[0]);
        var resultPath = Directory.Exists(suppliedPath)
            ? Path.Combine(suppliedPath, "audit", "output.json")
            : suppliedPath;
        if (!File.Exists(resultPath))
        {
            throw new FileNotFoundException("Run result was not found.", resultPath);
        }

        var json = await File.ReadAllTextAsync(resultPath, cancellationToken)
            .ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<AgentRunResult>(
                         json,
                         CreateAgentJson.CreateSerializerOptions()) ??
                     throw new JsonException("Run result is empty.");
        await output.WriteResultAsync(result).ConfigureAwait(false);
        return MapRunExitCode(result.State);
    }

    private static async Task ConsumeEventsAsync(
        IAgentRunHandle handle,
        CliOutput output)
    {
        await foreach (var agentEvent in handle.ReadEventsAsync().ConfigureAwait(false))
        {
            await output.WriteEventAsync(agentEvent).ConfigureAwait(false);
        }
    }

    private static void RequirePositionals(
        CliArguments arguments,
        int count,
        string usage)
    {
        if (arguments.Positionals.Count != count)
        {
            throw new CliUsageException($"Usage: createagent {usage}");
        }
    }

    private static void EnsureCodexProvider(string provider)
    {
        if (provider is not ("codex" or "openai.codex"))
        {
            throw new CliUsageException(
                $"Provider '{provider}' is not supported by this CLI version.");
        }
    }

    private static CliOutputFormat TryDetectFormat(IReadOnlyList<string> args)
    {
        var supportsFormat =
            args.Count > 0 &&
            (args[0] is "run" or "doctor" or "provider" or "inspect" ||
             args.Count > 1 && args[0] == "request" && args[1] == "validate");
        if (!supportsFormat)
        {
            return CliOutputFormat.Human;
        }

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--output=", StringComparison.Ordinal))
            {
                return CliOutput.ParseFormat(argument["--output=".Length..]);
            }

            if (argument == "--output" && index + 1 < args.Count)
            {
                return CliOutput.ParseFormat(args[index + 1]);
            }
        }

        return CliOutputFormat.Human;
    }

    private static int MapRunExitCode(AgentRunState state) =>
        state switch
        {
            AgentRunState.Succeeded => (int)CliExitCode.Success,
            AgentRunState.Cancelled => (int)CliExitCode.Cancelled,
            AgentRunState.TimedOut => (int)CliExitCode.TimedOut,
            _ => (int)CliExitCode.RunFailed,
        };

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
        "unknown";

    private const string HelpText =
        """
        CreateAgent CLI - reference host for CreateAgent

        Usage:
          createagent request init --provider codex [--output request.json] [--force]
          createagent request validate <request.json> [--output human|jsonl]
          createagent host init [--output host.json] [--force]
          createagent provider probe <codex|openai.codex> [host options]
          createagent doctor [host options]
          createagent schema print
          createagent run <request.json> [host options] [--output human|jsonl]
          createagent inspect <run-directory|output.json> [--output human|jsonl]
          createagent --version

        Host options:
          --config <host.json>
          --storage <directory>
          --codex-path <executable>
          --max-concurrent <number>
          --event-capacity <number>
          --process-output-capacity <number>

        Exit codes:
          0 success, 1 run failed, 2 usage, 3 provider unavailable,
          4 validation failed, 70 internal error, 124 timed out, 130 cancelled
        """;
}

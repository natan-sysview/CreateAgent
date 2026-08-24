using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace CreateAgent.Runtime.Processes;

public sealed class SystemProcessRunner : IAgentProcessRunner
{
    private readonly int _outputChannelCapacity;

    public SystemProcessRunner(int outputChannelCapacity = 1024)
    {
        if (outputChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputChannelCapacity),
                "Output channel capacity must be greater than zero.");
        }

        _outputChannelCapacity = outputChannelCapacity;
    }

    public async Task<AgentProcessResult> RunAsync(
        AgentCliLaunchPlan launchPlan,
        string? standardInput,
        Func<AgentProcessOutput, CancellationToken, ValueTask> onOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchPlan);
        ArgumentNullException.ThrowIfNull(onOutput);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = launchPlan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = launchPlan.InputMode == AgentInputMode.StandardInput,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (IsWindowsBatchFile(launchPlan.ExecutablePath))
        {
            startInfo.FileName =
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(
                BuildWindowsBatchCommand(
                    launchPlan.ExecutablePath,
                    launchPlan.Arguments));
        }
        else
        {
            startInfo.FileName = launchPlan.ExecutablePath;
            foreach (var argument in launchPlan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var variable in launchPlan.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Process '{launchPlan.ExecutablePath}' could not be started.");
        }

        var outputChannel = Channel.CreateBounded<AgentProcessOutput>(
            new BoundedChannelOptions(_outputChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        long outputSequence = 0;
        long outputBytes = 0;

        var stdoutTask = ReadStreamAsync(
            process.StandardOutput,
            AgentOutputChannel.StandardOutput,
            outputChannel.Writer,
            () => Interlocked.Increment(ref outputSequence),
            launchPlan.MaxOutputBytes,
            bytes => Interlocked.Add(ref outputBytes, bytes));
        var stderrTask = ReadStreamAsync(
            process.StandardError,
            AgentOutputChannel.StandardError,
            outputChannel.Writer,
            () => Interlocked.Increment(ref outputSequence),
            launchPlan.MaxOutputBytes,
            bytes => Interlocked.Add(ref outputBytes, bytes));
        var consumeTask = ConsumeOutputAsync(outputChannel.Reader, onOutput);

        if (launchPlan.InputMode == AgentInputMode.StandardInput)
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        try
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var firstCompleted = await Task
                .WhenAny(exitTask, stdoutTask, stderrTask, consumeTask)
                .ConfigureAwait(false);
            if (firstCompleted != exitTask && firstCompleted.IsFaulted)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                outputChannel.Writer.TryComplete(
                    firstCompleted.Exception?.GetBaseException());
                await firstCompleted.ConfigureAwait(false);
            }

            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            finally
            {
                outputChannel.Writer.TryComplete();
                await consumeTask.ConfigureAwait(false);
            }
        }

        return new AgentProcessResult(
            process.ExitCode,
            startedAt,
            DateTimeOffset.UtcNow);
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        AgentOutputChannel stream,
        ChannelWriter<AgentProcessOutput> writer,
        Func<long> nextSequence,
        long maxOutputBytes,
        Func<long, long> addOutputBytes)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var totalBytes = addOutputBytes(Encoding.UTF8.GetByteCount(line) + 1L);
            if (totalBytes > maxOutputBytes)
            {
                throw new AgentOutputLimitExceededException(
                    $"Provider output exceeded the limit of {maxOutputBytes} bytes.");
            }

            await writer.WriteAsync(
                    new AgentProcessOutput(
                        nextSequence(),
                        DateTimeOffset.UtcNow,
                        stream,
                        line),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task ConsumeOutputAsync(
        ChannelReader<AgentProcessOutput> reader,
        Func<AgentProcessOutput, CancellationToken, ValueTask> onOutput)
    {
        await foreach (var output in reader.ReadAllAsync(CancellationToken.None)
                           .ConfigureAwait(false))
        {
            await onOutput(output, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static bool IsWindowsBatchFile(string executablePath) =>
        OperatingSystem.IsWindows() &&
        Path.GetExtension(executablePath) is ".cmd" or ".bat";

    private static string BuildWindowsBatchCommand(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var values = new List<string>(arguments.Count + 1) { executablePath };
        values.AddRange(arguments);
        foreach (var value in values)
        {
            if (value.IndexOfAny(['\0', '\r', '\n', '"', '%', '!']) >= 0)
            {
                throw new InvalidOperationException(
                    "Windows batch launch contains an unsafe command interpreter character.");
            }
        }

        return string.Concat(
            "\"",
            string.Join(
                " ",
                values.Select(static value => string.Concat("\"", value, "\""))),
            "\"");
    }
}

public sealed class AgentOutputLimitExceededException : Exception
{
    public AgentOutputLimitExceededException(string message)
        : base(message)
    {
    }
}

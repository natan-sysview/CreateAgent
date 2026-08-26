using System.Text.Json;

namespace CreateAgent.Testing.FakeCli;

public sealed class FakeCliMarker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.Out.WriteLine("fake-cli 1.0");
            return 0;
        }

        if (args.Contains("-p", StringComparer.Ordinal) &&
            string.Equals(
                GetOption(args, "--output-format"),
                "stream-json",
                StringComparison.Ordinal))
        {
            return await RunClaudeAsync(args).ConfigureAwait(false);
        }

        var mode = GetOption(args, "--mode") ?? "success";
        var delayMilliseconds = int.TryParse(
            GetOption(args, "--delay-ms"),
            out var parsedDelay)
            ? parsedDelay
            : 10;
        var resultFile = GetOption(args, "--create-file");
        var eventCount = int.TryParse(
            GetOption(args, "--event-count"),
            out var parsedEventCount)
            ? parsedEventCount
            : 0;
        var prompt = await Console.In.ReadToEndAsync().ConfigureAwait(false);

        WriteEvent(new
        {
            type = "thread.started",
            thread_id = "fake-session-001",
        });
        WriteEvent(new { type = "turn.started" });
        if (string.Equals(mode, "invalid", StringComparison.Ordinal))
        {
            Console.Out.WriteLine("this is not valid json");
        }

        for (var index = 0; index < eventCount; index++)
        {
            WriteEvent(
                new
                {
                    type = "test.progress",
                    message = new string('x', 64),
                    index,
                });
        }

        WriteEvent(
            new
            {
                type = "item.started",
                item = new
                {
                    type = "command_execution",
                    command = "fake-tool",
                },
            });

        if (string.Equals(mode, "slow", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        }

        if (string.Equals(mode, "stderr", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("fake warning from stderr")
                .ConfigureAwait(false);
        }

        WriteEvent(
            new
            {
                type = "item.completed",
                item = new
                {
                    type = "command_execution",
                    command = "fake-tool",
                },
            });

        if (resultFile is not null)
        {
            var resultPath = Path.GetFullPath(resultFile);
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(
                    resultPath,
                    "created by fake provider")
                .ConfigureAwait(false);
        }

        if (string.Equals(mode, "fail", StringComparison.Ordinal))
        {
            WriteEvent(new { type = "error", message = "fake provider failure" });
            return 17;
        }

        if (!string.Equals(mode, "no-final", StringComparison.Ordinal))
        {
            var finalText = string.Equals(mode, "structured", StringComparison.Ordinal)
                ? """
                  {"nombre":"Laura","apellido":"Méndez","telefono":"555-0102","email":"laura@example.com","direccion":"Avenida Central 123"}
                  """
                : $"fake completed: {prompt.Trim()}";
            WriteEvent(
                new
                {
                    type = "item.completed",
                    item = new
                    {
                        type = "agent_message",
                        text = finalText,
                    },
                });
        }

        WriteEvent(new { type = "turn.completed" });
        return 0;
    }

    private static async Task<int> RunClaudeAsync(string[] args)
    {
        var prompt = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        WriteEvent(
            new
            {
                type = "system",
                subtype = "init",
                session_id = "fake-claude-session-001",
            });
        WriteEvent(
            new
            {
                type = "assistant",
                message = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "tool_use",
                            name = "Write",
                        },
                    },
                },
            });
        WriteEvent(
            new
            {
                type = "user",
                message = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "tool_result",
                            content = "File written successfully",
                        },
                    },
                },
            });

        if (GetOption(args, "--json-schema") is not null)
        {
            WriteEvent(
                new
                {
                    type = "result",
                    subtype = "success",
                    is_error = false,
                    structured_output = new
                    {
                        nombre = "Claude",
                        estado = "funcionando",
                    },
                });
        }
        else
        {
            WriteEvent(
                new
                {
                    type = "result",
                    subtype = "success",
                    is_error = false,
                    result = $"fake claude completed: {prompt.Trim()}",
                });
        }

        return 0;
    }

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void WriteEvent<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value));
}

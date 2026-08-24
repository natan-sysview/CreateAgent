using System.Text.Json;
using Spectre.Console;

namespace CreateAgent.Cli;

internal enum CliOutputFormat
{
    Human,
    JsonLines,
}

internal sealed class CliOutput
{
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private readonly IAnsiConsole _humanOutput;
    private readonly IAnsiConsole _humanError;
    private readonly JsonSerializerOptions _jsonOptions =
        CreateAgentJson.CreateSerializerOptions();

    public CliOutput(
        CliOutputFormat format,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        Format = format;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _humanOutput = CreateConsole(standardOutput);
        _humanError = CreateConsole(standardError);
    }

    public CliOutputFormat Format { get; }

    public static CliOutputFormat ParseFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "human" => CliOutputFormat.Human,
            "jsonl" => CliOutputFormat.JsonLines,
            _ => throw new CliUsageException(
                $"Unsupported output format '{value}'. Use human or jsonl."),
        };

    public async Task WriteEventAsync(AgentEvent agentEvent)
    {
        if (Format == CliOutputFormat.JsonLines)
        {
            await WriteJsonLineAsync(
                    _standardOutput,
                    new { type = "event", data = agentEvent })
                .ConfigureAwait(false);
            return;
        }

        var color = agentEvent.Kind switch
        {
            AgentEventKind.Error => "red",
            AgentEventKind.Warning => "yellow",
            AgentEventKind.AgentMessage => "green",
            AgentEventKind.ToolStarted or AgentEventKind.ToolCompleted => "blue",
            AgentEventKind.RunCompleted => "bold",
            _ => "grey",
        };
        var message = Markup.Escape(agentEvent.Message ?? agentEvent.Name);
        _humanOutput.MarkupLine(
            $"[grey]{agentEvent.Sequence,5}[/] [{color}]{Markup.Escape(agentEvent.Kind.ToString())}[/] {message}");
    }

    public async Task WriteResultAsync(AgentRunResult result)
    {
        if (Format == CliOutputFormat.JsonLines)
        {
            await WriteJsonLineAsync(
                    _standardOutput,
                    new { type = "result", data = result })
                .ConfigureAwait(false);
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value");
        table.AddRow("Run", Markup.Escape(result.RunId));
        table.AddRow("State", Markup.Escape(result.State.ToString()));
        table.AddRow(
            "Exit code",
            result.ExitCode?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ??
            "n/a");
        table.AddRow("Workspace", Markup.Escape(EmptyAsDash(result.WorkspacePath)));
        table.AddRow("Audit", Markup.Escape(EmptyAsDash(result.AuditPath)));
        table.AddRow(
            "Artifacts",
            result.Artifacts.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow(
            "Duration",
            (result.FinishedAtUtc - result.CreatedAtUtc).ToString("c"));
        _humanOutput.Write(table);

        if (!string.IsNullOrWhiteSpace(result.FinalMessage))
        {
            _humanOutput.MarkupLine(
                $"[bold green]Final output[/]: {Markup.Escape(result.FinalMessage)}");
        }
        else if (result.StructuredOutput is { } structuredOutput)
        {
            var json = JsonSerializer.Serialize(structuredOutput, _jsonOptions);
            _humanOutput.MarkupLine(
                $"[bold green]Final output[/]: {Markup.Escape(json)}");
        }

        foreach (var error in result.Errors)
        {
            _humanError.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
        }
    }

    public async Task WriteCapabilitiesAsync(AgentProviderCapabilities capabilities)
    {
        if (Format == CliOutputFormat.JsonLines)
        {
            await WriteJsonLineAsync(
                    _standardOutput,
                    new { type = "provider-capabilities", data = capabilities })
                .ConfigureAwait(false);
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Capability")
            .AddColumn("Value");
        table.AddRow("Provider", Markup.Escape(capabilities.ProviderId.Value));
        table.AddRow("Available", YesNo(capabilities.IsAvailable));
        table.AddRow("Version", Markup.Escape(capabilities.Version ?? "unknown"));
        table.AddRow(
            "Non-interactive",
            YesNo(capabilities.SupportsNonInteractiveExecution));
        table.AddRow("Structured events", YesNo(capabilities.SupportsStructuredEvents));
        table.AddRow("Session resume", YesNo(capabilities.SupportsSessionResume));
        table.AddRow("Native skills", YesNo(capabilities.SupportsNativeSkills));
        table.AddRow("Model selection", YesNo(capabilities.SupportsModelSelection));
        _humanOutput.Write(table);

        if (!string.IsNullOrWhiteSpace(capabilities.Diagnostic))
        {
            _humanError.MarkupLine(
                $"[yellow]Diagnostic:[/] {Markup.Escape(capabilities.Diagnostic)}");
        }
    }

    public async Task WriteStatusAsync(string type, string message, object? data = null)
    {
        if (Format == CliOutputFormat.JsonLines)
        {
            await WriteJsonLineAsync(
                    _standardOutput,
                    new { type, message, data })
                .ConfigureAwait(false);
            return;
        }

        _humanOutput.MarkupLine($"[green]OK[/] {Markup.Escape(message)}");
    }

    public async Task WriteDiagnosticAsync(string level, string message)
    {
        if (Format == CliOutputFormat.JsonLines)
        {
            await WriteJsonLineAsync(
                    _standardError,
                    new { type = "diagnostic", level, message })
                .ConfigureAwait(false);
            return;
        }

        var color = level == "warning" ? "yellow" : "red";
        _humanError.MarkupLine(
            $"[{color}]{Markup.Escape(level.ToUpperInvariant())}:[/] {Markup.Escape(message)}");
    }

    private async Task WriteJsonLineAsync(TextWriter writer, object value)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, _jsonOptions))
            .ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static IAnsiConsole CreateConsole(TextWriter writer) =>
        AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(writer),
                Ansi = AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.Detect,
                Interactive = InteractionSupport.No,
            });

    private static string YesNo(bool value) => value ? "[green]yes[/]" : "[red]no[/]";

    private static string EmptyAsDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;
}

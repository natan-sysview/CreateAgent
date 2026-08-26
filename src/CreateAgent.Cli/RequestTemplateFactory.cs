namespace CreateAgent.Cli;

internal static class RequestTemplateFactory
{
    public static AgentRunRequest Create(string provider)
    {
        var providerName = provider.ToLowerInvariant() switch
        {
            "codex" or "openai.codex" => "codex",
            "claude" or "anthropic.claude" => "claude",
            _ => throw new CliUsageException(
                $"Provider '{provider}' is not supported by this CLI version."),
        };

        return new AgentRunRequest
        {
            Name = "example-agent-run",
            ProviderName = providerName,
            Prompt = "Describe the objective for this agent run.",
        };
    }
}

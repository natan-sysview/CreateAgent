namespace CreateAgent.Cli;

internal static class RequestTemplateFactory
{
    public static AgentRunRequest Create(string provider)
    {
        var (providerName, model, effort) = provider.ToLowerInvariant() switch
        {
            "codex" or "openai.codex" => ("codex", "provider-default", "medium"),
            "claude" or "anthropic.claude" =>
                ("claude", "provider-default", "medium"),
            "opencode" or "anomaly.opencode" =>
                ("opencode", "deepseek/deepseek-flash", "high"),
            _ => throw new CliUsageException(
                $"Provider '{provider}' is not supported by this CLI version."),
        };

        return new AgentRunRequest
        {
            Name = "example-agent-run",
            ProviderName = providerName,
            Model = model,
            Reasoning = new AgentReasoningOptions { Effort = effort },
            Prompt = "Describe the objective for this agent run.",
        };
    }
}

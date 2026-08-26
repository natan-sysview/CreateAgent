using CreateAgent.Core;
using CreateAgent.Providers.ClaudeCode;
using CreateAgent.Providers.Codex;
using CreateAgent.Runtime.Processes;
using CreateAgent.Storage.FileSystem;

namespace CreateAgent;

public static class CreateAgentRuntime
{
    public static IAgentWorkerManager CreateDefault(
        CreateAgentOptions options,
        string codexExecutable = "codex",
        string claudeExecutable = "claude")
    {
        ArgumentNullException.ThrowIfNull(options);

        var processRunner = new SystemProcessRunner(options.ProcessOutputChannelCapacity);
        var workspaceProvider = new FileSystemWorkspaceProvider(options.StorageRoot);
        var auditFactory = new JsonLineAuditFactory();
        var failureBundler = new ZipFailureBundler(options.StorageRoot);
        var codexAdapter = new CodexCliAdapter(processRunner, codexExecutable);
        var claudeAdapter = new ClaudeCodeCliAdapter(processRunner, claudeExecutable);

        return new AgentWorkerManager(
            [codexAdapter, claudeAdapter],
            processRunner,
            workspaceProvider,
            auditFactory,
            failureBundler,
            options);
    }

    public static IAgentWorkerManager Create(
        CreateAgentOptions options,
        IEnumerable<IAgentCliAdapter> adapters,
        IAgentProcessRunner processRunner,
        IAgentWorkspaceProvider workspaceProvider,
        IAgentRunAuditFactory auditFactory,
        IAgentFailureBundler failureBundler) =>
        new AgentWorkerManager(
            adapters,
            processRunner,
            workspaceProvider,
            auditFactory,
            failureBundler,
            options);
}

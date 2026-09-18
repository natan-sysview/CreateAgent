using CreateAgent.Core;
using CreateAgent.Providers.ClaudeCode;
using CreateAgent.Providers.Codex;
using CreateAgent.Providers.OpenCode;
using CreateAgent.Runtime.Processes;
using CreateAgent.Storage.FileSystem;

namespace CreateAgent;

public static class CreateAgentRuntime
{
    public static IAgentWorkerManager CreateDefault(
        CreateAgentOptions options,
        string codexExecutable = "codex",
        string claudeExecutable = "claude")
        => CreateDefault(
            options,
            codexExecutable,
            claudeExecutable,
            "opencode");

    public static IAgentWorkerManager CreateDefault(
        CreateAgentOptions options,
        string codexExecutable,
        string claudeExecutable,
        string openCodeExecutable)
    {
        ArgumentNullException.ThrowIfNull(options);

        var processRunner = new SystemProcessRunner(options.ProcessOutputChannelCapacity);
        var workspaceProvider = new FileSystemWorkspaceProvider(options.StorageRoot);
        var sessionStore = new FileSystemAgentSessionStore(options.StorageRoot);
        var auditFactory = new JsonLineAuditFactory();
        var failureBundler = new ZipFailureBundler(options.StorageRoot);
        var codexAdapter = new CodexCliAdapter(processRunner, codexExecutable);
        var claudeAdapter = new ClaudeCodeCliAdapter(processRunner, claudeExecutable);
        var openCodeAdapter = new OpenCodeCliAdapter(
            processRunner,
            openCodeExecutable);

        return new AgentWorkerManager(
            [codexAdapter, claudeAdapter, openCodeAdapter],
            processRunner,
            workspaceProvider,
            auditFactory,
            failureBundler,
            options,
            sessionStore);
    }

    public static IAgentWorkerManager Create(
        CreateAgentOptions options,
        IEnumerable<IAgentCliAdapter> adapters,
        IAgentProcessRunner processRunner,
        IAgentWorkspaceProvider workspaceProvider,
        IAgentRunAuditFactory auditFactory,
        IAgentFailureBundler failureBundler,
        IAgentSessionStore? sessionStore = null) =>
        new AgentWorkerManager(
            adapters,
            processRunner,
            workspaceProvider,
            auditFactory,
            failureBundler,
            options,
            sessionStore ?? new FileSystemAgentSessionStore(options.StorageRoot));
}

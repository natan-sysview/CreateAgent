using System.Text;
using System.Text.Json;

namespace CreateAgent.Storage.FileSystem;

public sealed class JsonLineAuditFactory : IAgentRunAuditFactory
{
    private readonly ISecretRedactor _redactor;

    public JsonLineAuditFactory(ISecretRedactor? redactor = null)
    {
        _redactor = redactor ?? new LiteralSecretRedactor();
    }

    public Task<IAgentRunAudit> CreateAsync(
        AgentWorkspace workspace,
        bool debug,
        IReadOnlyList<string> secretsToRedact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAgentRunAudit audit = new JsonLineAudit(
            workspace,
            debug,
            _redactor,
            secretsToRedact);
        return Task.FromResult(audit);
    }
}

internal sealed class JsonLineAudit : IAgentRunAudit
{
    private static readonly JsonSerializerOptions CompactJsonOptions =
        CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions PrettyJsonOptions =
        CreateJsonOptions(writeIndented: true);

    private readonly AgentWorkspace _workspace;
    private readonly bool _debug;
    private readonly ISecretRedactor _redactor;
    private readonly IReadOnlyList<string> _secrets;
    private readonly WorkspaceManifestWriter _manifestWriter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileStream _eventStream;
    private WorkspaceFileManifest? _beforeManifest;
    private bool _disposed;

    public JsonLineAudit(
        AgentWorkspace workspace,
        bool debug,
        ISecretRedactor redactor,
        IReadOnlyList<string> secrets)
    {
        _workspace = workspace;
        _debug = debug;
        _redactor = redactor;
        _secrets = secrets;
        _manifestWriter = new WorkspaceManifestWriter(workspace.WorkspaceDirectory);
        AuditPath = Path.Combine(workspace.AuditDirectory, "events.jsonl");
        _eventStream = CreateLogStream(AuditPath);
    }

    public string AuditPath { get; }

    public string AuditDirectory => _workspace.AuditDirectory;

    public Task WriteRequestAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(_workspace.AuditDirectory, "input.json"),
            request,
            cancellationToken);

    public async Task WriteEventAsync(
        AgentEvent agentEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(agentEvent, CompactJsonOptions);
        json = _redactor.Redact(json, _secrets);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _eventStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _eventStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CaptureWorkspaceManifestAsync(
        AgentWorkspaceManifestStage stage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_debug)
        {
            return;
        }

        var manifest = await _manifestWriter
            .CreateAsync(stage, cancellationToken)
            .ConfigureAwait(false);
        if (stage == AgentWorkspaceManifestStage.BeforeExecution)
        {
            _beforeManifest = manifest;
            return;
        }

        var changes = WorkspaceManifestWriter.Compare(_beforeManifest, manifest);
        await WriteJsonAtomicallyAsync(
                Path.Combine(_workspace.AuditDirectory, "files.json"),
                changes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task WriteResultAsync(
        AgentRunResult result,
        CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(
            _workspace.ResultPath,
            result,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _eventStream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task WriteJsonAtomicallyAsync<T>(
        string targetPath,
        T value,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = JsonSerializer.Serialize(value, PrettyJsonOptions);
        json = _redactor.Redact(json, _secrets);
        var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            FileSystemSecurity.MakeFilePrivate(temporaryPath);
            File.Move(temporaryPath, targetPath, overwrite: true);
            FileSystemSecurity.MakeFilePrivate(targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
        => CreateAgentJson.CreateSerializerOptions(writeIndented);

    private static FileStream CreateLogStream(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        FileSystemSecurity.MakeFilePrivate(path);
        return stream;
    }
}

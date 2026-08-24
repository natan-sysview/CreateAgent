using System.Text.Json;

namespace CreateAgent.Core;

public static class AgentRunRequestValidation
{
    public static void Validate(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("Name is required.");
        }
        else if (request.Name.Length > 160)
        {
            errors.Add("Name cannot exceed 160 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderName))
        {
            errors.Add("ProviderName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            errors.Add("Model is required.");
        }

        if (!Enum.IsDefined(request.Performance))
        {
            errors.Add($"Performance '{request.Performance}' is not supported.");
        }

        if (string.IsNullOrWhiteSpace(request.Reasoning.Effort))
        {
            errors.Add("Reasoning effort is required.");
        }

        if (!Enum.IsDefined(request.Session.Mode))
        {
            errors.Add($"Session mode '{request.Session.Mode}' is not supported.");
        }
        else if (request.Session.Mode == AgentSessionMode.Resume &&
                 string.IsNullOrWhiteSpace(request.Session.Id))
        {
            errors.Add("Resume session mode requires an Id.");
        }
        else if (request.Session.Mode != AgentSessionMode.Resume &&
                 request.Session.Id is not null)
        {
            errors.Add("Session Id is only valid when mode is resume.");
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            errors.Add("Prompt is required.");
        }

        if (request.OutputSchema is { ValueKind: not JsonValueKind.Object })
        {
            errors.Add("OutputSchema must be a JSON object.");
        }

        foreach (var source in request.Workspace.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Path))
            {
                errors.Add("Workspace source Path is required.");
            }
            else if (!File.Exists(source.Path) && !Directory.Exists(source.Path))
            {
                errors.Add($"Workspace source does not exist: {source.Path}");
            }

            if (!Enum.IsDefined(source.Copy))
            {
                errors.Add($"Workspace copy mode '{source.Copy}' is not supported.");
            }
        }

        foreach (var path in request.Workspace.ReadOnlyPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add("Workspace read-only path is required.");
            }
            else if (!File.Exists(path) && !Directory.Exists(path))
            {
                errors.Add($"Workspace read-only path does not exist: {path}");
            }
        }

        if (request.Tools.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Tool names cannot be empty.");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            errors.Add("Timeout must be greater than zero.");
        }

        if (errors.Count > 0)
        {
            throw new CreateAgentValidationException(errors);
        }
    }
}

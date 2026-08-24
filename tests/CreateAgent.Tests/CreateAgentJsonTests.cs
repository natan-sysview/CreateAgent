using System.Text.Json;

namespace CreateAgent.Tests;

public sealed class CreateAgentJsonTests
{
    [Fact]
    public void DeserializesAndSerializesTheSimpleContract()
    {
        const string json =
            """
            {
              "name": "contract-test",
              "providerName": "codex",
              "model": "gpt-test",
              "performance": "fast",
              "reasoning": {
                "effort": "xhigh"
              },
              "session": {
                "mode": "ephemeral"
              },
              "prompt": "Implement the specification.",
              "outputSchema": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" }
                },
                "required": ["name"],
                "additionalProperties": false
              },
              "workspace": {
                "sources": [],
                "readOnlyPaths": ["/data/reference"]
              },
              "tools": [],
              "timeout": "PT45M",
              "debug": true
            }
            """;

        var request = CreateAgentJson.DeserializeRequest(json);
        var serialized = CreateAgentJson.SerializeRequest(request);

        Assert.Equal("codex", request.ProviderName);
        Assert.Equal("gpt-test", request.Model);
        Assert.Equal(AgentPerformanceMode.Fast, request.Performance);
        Assert.Equal("xhigh", request.Reasoning.Effort);
        Assert.Equal(AgentSessionMode.Ephemeral, request.Session.Mode);
        Assert.Equal(
            "object",
            request.OutputSchema?.GetProperty("type").GetString());
        Assert.Equal(TimeSpan.FromMinutes(45), request.Timeout);
        Assert.True(request.Debug);
        Assert.Equal("/data/reference", Assert.Single(request.Workspace.ReadOnlyPaths));
        Assert.Contains("\"providerName\": \"codex\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"outputSchema\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupportedBehavior", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("agentDefinition", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("audit", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnknownContractFields()
    {
        const string json =
            """
            {
              "name": "invalid",
              "providerName": "codex",
              "prompt": "test",
              "unknownField": true
            }
            """;

        Assert.Throws<JsonException>(() => CreateAgentJson.DeserializeRequest(json));
    }

    [Fact]
    public void MinimalRequestUsesClearDefaults()
    {
        const string json =
            """
            {
              "name": "minimal",
              "providerName": "codex",
              "prompt": "complete the objective"
            }
            """;

        var request = CreateAgentJson.DeserializeRequest(json);

        Assert.Equal("provider-default", request.Model);
        Assert.Equal(AgentPerformanceMode.Normal, request.Performance);
        Assert.Equal("medium", request.Reasoning.Effort);
        Assert.Equal(AgentSessionMode.New, request.Session.Mode);
        Assert.Null(request.OutputSchema);
        Assert.Equal(TimeSpan.FromMinutes(30), request.Timeout);
        Assert.False(request.Debug);
        Assert.Empty(request.Workspace.ReadOnlyPaths);
    }
}

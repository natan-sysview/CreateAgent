using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreateAgent;

public static class CreateAgentJson
{
    public static AgentRunRequest DeserializeRequest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<AgentRunRequest>(
                   json,
                   CreateSerializerOptions()) ??
               throw new JsonException("The CreateAgent request is empty.");
    }

    public static string SerializeRequest(
        AgentRunRequest request,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(
            request,
            CreateSerializerOptions(writeIndented));
    }

    public static JsonSerializerOptions CreateSerializerOptions(
        bool writeIndented = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}

public sealed class AgentProviderIdJsonConverter : JsonConverter<AgentProviderId>
{
    public override AgentProviderId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Provider id must be a JSON string.");
        }

        return new AgentProviderId(
            reader.GetString() ??
            throw new JsonException("Provider id cannot be null."));
    }

    public override void Write(
        Utf8JsonWriter writer,
        AgentProviderId value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed class Iso8601TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Duration must be an ISO 8601 string.");
        }

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(reader.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new JsonException("Duration is not valid ISO 8601.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        TimeSpan value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(System.Xml.XmlConvert.ToString(value));
}

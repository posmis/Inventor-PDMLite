using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdmLite.Link;

/// <summary>Сообщение протокола. Передаётся как одна строка JSON.</summary>
public sealed class LinkMessage
{
    public const string RequestType = "request";
    public const string ResponseType = "response";
    public const string EventType = "event";

    [JsonPropertyName("type")] public string Type { get; set; } = RequestType;

    /// <summary>Missing in the original v1 wire format. New clients declare their protocol explicitly.</summary>
    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProtocolVersion { get; set; }

    /// <summary>Идентификатор запроса; в ответе совпадает с запросом.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>Имя запроса или события.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }

    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;

    [JsonPropertyName("error")] public string? Error { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement? ToPayload(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);

    public T? PayloadAs<T>() =>
        Payload is { } payload && payload.ValueKind != JsonValueKind.Null
            ? payload.Deserialize<T>(JsonOptions)
            : default;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static LinkMessage? Deserialize(string line) => JsonSerializer.Deserialize<LinkMessage>(line, JsonOptions);
}

/// <summary>Ошибка, которую вернула другая сторона.</summary>
public sealed class LinkRemoteException(string message) : Exception(message);

using System.Text.Json.Serialization;

namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record AiToolPlan
{
    [JsonPropertyName("intent")]
    public string Intent { get; init; } = string.Empty;

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; init; } = new();

    [JsonPropertyName("requiresConfirmation")]
    public bool RequiresConfirmation { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;
}

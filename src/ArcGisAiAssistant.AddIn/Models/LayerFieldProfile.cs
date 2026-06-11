namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record LayerFieldProfile(
    string Name,
    string Alias,
    string FieldType,
    bool IsNullable);

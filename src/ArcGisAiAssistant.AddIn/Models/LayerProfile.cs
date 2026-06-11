namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record LayerProfile(
    string Name,
    string LayerType,
    string? GeometryType,
    bool IsVisible,
    bool IsSelectable,
    string? DefinitionQuery,
    string? DataSource,
    long? RowCount,
    IReadOnlyList<LayerFieldProfile> Fields);

namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record AiRequestContext(
    string UserInput,
    string? ActiveMapName,
    IReadOnlyList<string> LayerNames,
    IReadOnlyList<LayerProfile> Layers,
    IReadOnlyList<string> SelectedLayerNames,
    string? ViewExtent);

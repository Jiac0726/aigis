using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGisAiAssistant.AddIn.Models;
using System.IO;

namespace ArcGisAiAssistant.AddIn.ArcGis;

internal sealed class MapCommandService
{
    public Task<ExecutionResult> ExecuteAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        return plan.ToolName switch
        {
            "zoom_to_layer" => ZoomToLayerAsync(plan, cancellationToken),
            "apply_symbology_from_layer" => ApplySymbologyFromLayerAsync(plan, cancellationToken),
            "apply_unique_value_symbology" => ApplySymbologyFromLayerAsync(plan, cancellationToken),
            "export_map_view" => ExportMapViewAsync(plan, cancellationToken),
            "export_layout" => ExportMapViewAsync(plan, cancellationToken),
            _ => Task.FromResult(ExecutionResult.Failure($"Unsupported cartography tool: {plan.ToolName}"))
        };
    }

    private static async Task<ExecutionResult> ZoomToLayerAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.Parameters.TryGetValue("layerName", out var layerName) || string.IsNullOrWhiteSpace(layerName))
        {
            return ExecutionResult.Failure("Missing required parameter: layerName.");
        }

        return await QueuedTask.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mapView = MapView.Active;
            if (mapView is null)
            {
                return ExecutionResult.Failure("No active map view is available.");
            }

            var layers = mapView.Map.GetLayersAsFlattenedList().ToArray();
            var layer = FindLayer(layers, layerName);

            if (layer is null)
            {
                var availableLayers = string.Join(", ", layers.Select(candidate => candidate.Name));
                return ExecutionResult.Failure($"Layer not found: {layerName}. Available layers: {availableLayers}");
            }

            await mapView.ZoomToAsync(layer);
            return ExecutionResult.Success($"Zoomed to layer: {layer.Name}.");
        });
    }

    private static async Task<ExecutionResult> ApplySymbologyFromLayerAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.Parameters.TryGetValue("layerName", out var layerName) || string.IsNullOrWhiteSpace(layerName))
        {
            return ExecutionResult.Failure("Missing required parameter: layerName.");
        }

        if (!plan.Parameters.TryGetValue("symbologyLayer", out var symbologyLayerName) ||
            string.IsNullOrWhiteSpace(symbologyLayerName))
        {
            return ExecutionResult.Failure("Missing required parameter: symbologyLayer. Add a styled template layer to the map or choose an existing styled layer.");
        }

        var layerResolution = await ResolveLayerNamesAsync(layerName, symbologyLayerName);
        if (!string.IsNullOrWhiteSpace(layerResolution.Error))
        {
            return ExecutionResult.Failure(layerResolution.Error);
        }

        var values = Geoprocessing.MakeValueArray(layerResolution.TargetLayerName, layerResolution.TemplateLayerName);
        var environments = Geoprocessing.MakeEnvironmentArray();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Geoprocessing.ExecuteToolAsync(
                "management.ApplySymbologyFromLayer",
                values,
                environments,
                null,
                null,
                GPExecuteToolFlags.None);
            var messages = result.Messages.Select(message => message.Text).ToArray();
            return result.IsFailed
                ? ExecutionResult.Failure("Apply symbology from layer failed.", logs: messages)
                : ExecutionResult.Success($"Applied symbology from {layerResolution.TemplateLayerName} to {layerResolution.TargetLayerName}.", logs: messages);
        }
        catch (Exception ex)
        {
            return ExecutionResult.Failure("Apply symbology from layer failed before completion.", ex.Message);
        }
    }

    private static async Task<ExecutionResult> ExportMapViewAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var outputPath = plan.Parameters.GetValueOrDefault("outputPath");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = BuildDefaultExportPath(plan.Parameters.GetValueOrDefault("outputName"));
        }

        var resolution = TryParseInt(plan.Parameters.GetValueOrDefault("resolution"), 150);
        return await QueuedTask.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapView = MapView.Active;
            if (mapView is null)
            {
                return ExecutionResult.Failure("No active map view is available.");
            }

            var folder = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var pngFormat = new PNGFormat
            {
                OutputFileName = outputPath,
                Resolution = resolution
            };

            mapView.Export(pngFormat);
            return ExecutionResult.Success($"Exported active map view to {outputPath}.", outputPath);
        });
    }

    private static Task<(string? TargetLayerName, string? TemplateLayerName, string? Error)> ResolveLayerNamesAsync(
        string targetLayerName,
        string templateLayerName)
    {
        return QueuedTask.Run(() =>
        {
            var map = MapView.Active?.Map;
            if (map is null)
            {
                return ((string?)null, (string?)null, "No active map is available.");
            }

            var layers = map.GetLayersAsFlattenedList().ToArray();
            var targetLayer = FindLayer(layers, targetLayerName);
            if (targetLayer is null)
            {
                return ((string?)null, (string?)null, $"Layer not found: {targetLayerName}. Available layers: {string.Join(", ", layers.Select(candidate => candidate.Name))}");
            }

            var templateLayer = FindLayer(layers, templateLayerName);
            if (templateLayer is null)
            {
                return ((string?)null, (string?)null, $"Symbology template layer not found: {templateLayerName}. Available layers: {string.Join(", ", layers.Select(candidate => candidate.Name))}");
            }

            return (targetLayer.Name, templateLayer.Name, (string?)null);
        });
    }

    internal static Layer? FindLayer(IEnumerable<Layer> layers, string layerName)
    {
        var normalizedTarget = NormalizeLayerName(layerName);
        var layerArray = layers.ToArray();

        return layerArray.FirstOrDefault(candidate =>
                   string.Equals(candidate.Name, layerName, StringComparison.OrdinalIgnoreCase)) ??
               layerArray.FirstOrDefault(candidate =>
                   string.Equals(NormalizeLayerName(candidate.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase)) ??
               layerArray.FirstOrDefault(candidate =>
               {
                   var normalizedCandidate = NormalizeLayerName(candidate.Name);
                   return normalizedCandidate.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                          normalizedTarget.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
               });
    }

    private static string NormalizeLayerName(string value)
    {
        return value
            .Replace("图层", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("layer", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string BuildDefaultExportPath(string? outputName)
    {
        var folder = Project.Current?.HomeFolderPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var fileName = string.IsNullOrWhiteSpace(outputName) ? "Ai_Map_Export" : outputName;
        fileName = new string(fileName.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray()).Trim('_');
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Ai_Map_Export";
        }

        return Path.Combine(folder, $"{fileName}.png");
    }

    private static int TryParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}

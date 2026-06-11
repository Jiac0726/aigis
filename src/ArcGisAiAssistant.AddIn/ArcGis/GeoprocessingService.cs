using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGisAiAssistant.AddIn.Models;
using ArcGisAiAssistant.AddIn.Services;
using System.IO;

namespace ArcGisAiAssistant.AddIn.ArcGis;

internal sealed class GeoprocessingService
{
    public async Task<ExecutionResult> ExecuteAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        return plan.ToolName.ToLowerInvariant() switch
        {
            "buffer" => await ExecuteBufferAsync(plan, cancellationToken),
            "clip" => await ExecuteClipAsync(plan, cancellationToken),
            "select_by_attribute" => await ExecuteSelectByAttributeAsync(plan, cancellationToken),
            "select_by_location" => await ExecuteSelectByLocationAsync(plan, cancellationToken),
            "intersect" => await ExecuteIntersectAsync(plan, cancellationToken),
            "spatial_join" => await ExecuteSpatialJoinAsync(plan, cancellationToken),
            "summary_statistics" => await ExecuteSummaryStatisticsAsync(plan, cancellationToken),
            _ => ExecutionResult.Failure($"Unsupported analysis tool: {plan.ToolName}")
        };
    }

    private static async Task<ExecutionResult> ExecuteBufferAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerName = plan.Parameters["inputLayer"];
        var distance = NormalizeDistance(plan.Parameters["distance"]);
        var outputPath = BuildOutputPath(plan.Parameters.GetValueOrDefault("outputName"), $"{inputLayerName}_Buffer");
        var (inputLayer, layerError) = await ResolveLayerNameAsync(inputLayerName);
        if (inputLayer is null)
        {
            return ExecutionResult.Failure(layerError ?? $"Layer not found: {inputLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(inputLayer, outputPath, distance);
        return await ExecuteToolAsync("analysis.Buffer", values, outputPath, cancellationToken);
    }

    private static async Task<ExecutionResult> ExecuteClipAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerName = plan.Parameters["inputLayer"];
        var clipLayerName = plan.Parameters["clipLayer"];
        var outputPath = BuildOutputPath(plan.Parameters.GetValueOrDefault("outputName"), $"{inputLayerName}_Clip");
        var (inputLayer, inputLayerError) = await ResolveLayerNameAsync(inputLayerName);
        if (inputLayer is null)
        {
            return ExecutionResult.Failure(inputLayerError ?? $"Layer not found: {inputLayerName}");
        }

        var (clipLayer, clipLayerError) = await ResolveLayerNameAsync(clipLayerName);
        if (clipLayer is null)
        {
            return ExecutionResult.Failure(clipLayerError ?? $"Layer not found: {clipLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(inputLayer, clipLayer, outputPath);
        return await ExecuteToolAsync("analysis.Clip", values, outputPath, cancellationToken);
    }

    private static async Task<ExecutionResult> ExecuteSelectByAttributeAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerName = plan.Parameters["inputLayer"];
        var whereClause = plan.Parameters["whereClause"];
        var selectionType = plan.Parameters.GetValueOrDefault("selectionType", "NEW_SELECTION");
        var (inputLayer, layerError) = await ResolveLayerNameAsync(inputLayerName);
        if (inputLayer is null)
        {
            return ExecutionResult.Failure(layerError ?? $"Layer not found: {inputLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(inputLayer, selectionType, whereClause);
        return await ExecuteToolAsync("management.SelectLayerByAttribute", values, inputLayer, cancellationToken, addOutputsToMap: false);
    }

    private static async Task<ExecutionResult> ExecuteSelectByLocationAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerName = plan.Parameters["inputLayer"];
        var selectingLayerName = plan.Parameters["selectingLayer"];
        var overlapType = plan.Parameters.GetValueOrDefault("overlapType", "INTERSECT");
        var searchDistance = plan.Parameters.GetValueOrDefault("searchDistance", string.Empty);
        var selectionType = plan.Parameters.GetValueOrDefault("selectionType", "NEW_SELECTION");

        var (inputLayer, inputLayerError) = await ResolveLayerNameAsync(inputLayerName);
        if (inputLayer is null)
        {
            return ExecutionResult.Failure(inputLayerError ?? $"Layer not found: {inputLayerName}");
        }

        var (selectingLayer, selectingLayerError) = await ResolveLayerNameAsync(selectingLayerName);
        if (selectingLayer is null)
        {
            return ExecutionResult.Failure(selectingLayerError ?? $"Layer not found: {selectingLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(inputLayer, overlapType, selectingLayer, searchDistance, selectionType);
        return await ExecuteToolAsync("management.SelectLayerByLocation", values, inputLayer, cancellationToken, addOutputsToMap: false);
    }

    private static async Task<ExecutionResult> ExecuteIntersectAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerNames = SplitParameterList(plan.Parameters["inputLayers"]);
        if (inputLayerNames.Count < 2)
        {
            return ExecutionResult.Failure("Intersect requires at least two input layers.");
        }

        var resolvedLayers = new List<string>();
        foreach (var layerName in inputLayerNames)
        {
            var (layer, layerError) = await ResolveLayerNameAsync(layerName);
            if (layer is null)
            {
                return ExecutionResult.Failure(layerError ?? $"Layer not found: {layerName}");
            }

            resolvedLayers.Add(layer);
        }

        var outputPath = BuildOutputPath(plan.Parameters.GetValueOrDefault("outputName"), "Ai_Intersect");
        var joinAttributes = plan.Parameters.GetValueOrDefault("joinAttributes", "ALL");
        var clusterTolerance = plan.Parameters.GetValueOrDefault("clusterTolerance", string.Empty);
        var outputType = plan.Parameters.GetValueOrDefault("outputType", "INPUT");
        var values = Geoprocessing.MakeValueArray(string.Join(";", resolvedLayers), outputPath, joinAttributes, clusterTolerance, outputType);
        return await ExecuteToolAsync("analysis.Intersect", values, outputPath, cancellationToken);
    }

    private static async Task<ExecutionResult> ExecuteSpatialJoinAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var targetLayerName = plan.Parameters["targetLayer"];
        var joinLayerName = plan.Parameters["joinLayer"];
        var outputPath = BuildOutputPath(plan.Parameters.GetValueOrDefault("outputName"), $"{targetLayerName}_SpatialJoin");
        var joinOperation = plan.Parameters.GetValueOrDefault("joinOperation", "JOIN_ONE_TO_ONE");
        var joinType = plan.Parameters.GetValueOrDefault("joinType", "KEEP_ALL");
        var matchOption = plan.Parameters.GetValueOrDefault("matchOption", "INTERSECT");
        var searchRadius = plan.Parameters.GetValueOrDefault("searchRadius", string.Empty);

        var (targetLayer, targetLayerError) = await ResolveLayerNameAsync(targetLayerName);
        if (targetLayer is null)
        {
            return ExecutionResult.Failure(targetLayerError ?? $"Layer not found: {targetLayerName}");
        }

        var (joinLayer, joinLayerError) = await ResolveLayerNameAsync(joinLayerName);
        if (joinLayer is null)
        {
            return ExecutionResult.Failure(joinLayerError ?? $"Layer not found: {joinLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(
            targetLayer,
            joinLayer,
            outputPath,
            joinOperation,
            joinType,
            string.Empty,
            matchOption,
            searchRadius);
        return await ExecuteToolAsync("analysis.SpatialJoin", values, outputPath, cancellationToken);
    }

    private static async Task<ExecutionResult> ExecuteSummaryStatisticsAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        var inputLayerName = plan.Parameters["inputLayer"];
        var statisticsFields = plan.Parameters["statisticsFields"];
        var outputPath = BuildOutputPath(plan.Parameters.GetValueOrDefault("outputName"), $"{inputLayerName}_Summary");
        var caseFields = plan.Parameters.GetValueOrDefault("caseFields", string.Empty);

        var (inputLayer, inputLayerError) = await ResolveLayerNameAsync(inputLayerName);
        if (inputLayer is null)
        {
            return ExecutionResult.Failure(inputLayerError ?? $"Layer not found: {inputLayerName}");
        }

        var values = Geoprocessing.MakeValueArray(inputLayer, outputPath, statisticsFields, caseFields);
        return await ExecuteToolAsync("analysis.Statistics", values, outputPath, cancellationToken);
    }

    private static async Task<ExecutionResult> ExecuteToolAsync(
        string arcgisToolName,
        IReadOnlyList<string> values,
        string outputPath,
        CancellationToken cancellationToken,
        bool addOutputsToMap = true)
    {
        var environments = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Geoprocessing.ExecuteToolAsync(
                arcgisToolName,
                values,
                environments,
                null,
                null,
                addOutputsToMap ? GPExecuteToolFlags.AddOutputsToMap : GPExecuteToolFlags.None);

            IReadOnlyList<string> messages;
            try { messages = result.Messages.Select(m => m.Text).ToArray(); }
            catch { messages = Array.Empty<string>(); }

            var paramsSummary = string.Join(", ", values);
            AuditLogger.Log(arcgisToolName, paramsSummary, !result.IsFailed, result.IsFailed ? $"ErrorCode={result.ErrorCode}" : null);

            if (result.IsFailed)
            {
                var detail = BuildFailureMessage(arcgisToolName, messages);
                var ec = result.ErrorCode != 0 ? $" ErrorCode={result.ErrorCode}" : "";

                // Auto-retry: if output exists, try with _v2 suffix
                if (result.ErrorCode == 725 || detail.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    var retryValues = values.ToArray();
                    for (var vi = 0; vi < retryValues.Length; vi++)
                    {
                        if (retryValues[vi].Contains(outputPath, StringComparison.OrdinalIgnoreCase))
                        {
                            retryValues[vi] = retryValues[vi].Replace(outputPath, outputPath + "_v2");
                            break;
                        }
                    }
                    try
                    {
                        var retryResult = await Geoprocessing.ExecuteToolAsync(
                            arcgisToolName, retryValues, environments, null, null,
                            addOutputsToMap ? GPExecuteToolFlags.AddOutputsToMap : GPExecuteToolFlags.None);
                        IReadOnlyList<string> retryMessages;
                        try { retryMessages = retryResult.Messages.Select(m => m.Text).ToArray(); }
                        catch { retryMessages = Array.Empty<string>(); }
                        if (!retryResult.IsFailed)
                            return ExecutionResult.Success($"{arcgisToolName} completed (retry with _v2).", outputPath + "_v2", retryMessages);
                        return ExecutionResult.Failure($"{detail} (retry also failed){ec}. Values: [{string.Join(", ", values)}]", logs: messages);
                    }
                    catch (Exception retryEx)
                    {
                        return ExecutionResult.Failure($"{detail} (retry exception: {retryEx.Message}){ec}. Values: [{string.Join(", ", values)}]", logs: messages);
                    }
                }

                return ExecutionResult.Failure($"{detail}{ec}. Values: [{string.Join(", ", values)}]", logs: messages);
            }

            return ExecutionResult.Success($"{arcgisToolName} completed.", outputPath, messages);
        }
        catch (Exception ex)
        {
return ExecutionResult.Failure($"{arcgisToolName} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string BuildFailureMessage(string toolName, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return $"{toolName} failed with no ArcGIS messages.";
        }

        var lastError = messages.LastOrDefault();
        return $"{toolName} failed: {lastError}";
    }

    private static IReadOnlyList<string> SplitParameterList(string value)
    {
        return value
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static Task<(string? LayerName, string? Error)> ResolveLayerNameAsync(string layerName)
    {
        return QueuedTask.Run(() =>
        {
            var map = MapView.Active?.Map;
            if (map is null)
            {
                return ((string?)null, "No active map is available.");
            }

            var layers = map.GetLayersAsFlattenedList().ToArray();
            var layer = MapCommandService.FindLayer(layers, layerName);
            if (layer is null)
            {
                return ((string?)null, $"Layer not found: {layerName}. Available layers: {string.Join(", ", layers.Select(candidate => candidate.Name))}");
            }

            var ds = layer.URI ?? layer.Name;
            return (ds, (string?)null);
        });
    }

    private static string BuildOutputPath(string? requestedOutputName, string fallbackName)
    {
        var basePath = GetSandboxGeodatabasePath();
        var outputName = SanitizeDatasetName(string.IsNullOrWhiteSpace(requestedOutputName) ? fallbackName : requestedOutputName);
        return Path.Combine(basePath, outputName);
    }

    private static string GetSandboxGeodatabasePath()
    {
        var projectHome = Project.Current?.HomeFolderPath;
        if (string.IsNullOrWhiteSpace(projectHome))
            throw new InvalidOperationException("Current project does not have a home folder.");

        var sandboxGdb = Path.Combine(projectHome, "AiSandbox.gdb");
        if (!Directory.Exists(sandboxGdb))
        {
            // Create the sandbox geodatabase via GP
            try
            {
                var createValues = Geoprocessing.MakeValueArray(sandboxGdb);
                Geoprocessing.ExecuteToolAsync(
                    "management.CreateFileGDB",
                    createValues,
                    null, null, null,
                    GPExecuteToolFlags.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Fall back to default if sandbox creation fails
                return Project.Current?.DefaultGeodatabasePath
                    ?? throw new InvalidOperationException("Cannot create sandbox geodatabase and no default GDB available.");
            }
        }

        return sandboxGdb;
    }

    private static string NormalizeDistance(string rd){if(string.IsNullOrWhiteSpace(rd))return "0 Meters";var m=System.Text.RegularExpressions.Regex.Match(rd,@"(?<v>\d+(?:\.\d+)?)\s*(?<u>米|m|meter|meters|千米|公里|km|kilometer|kilometers)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(m.Success){var v=m.Groups["v"].Value;return m.Groups["u"].Value.ToLowerInvariant() is "千米" or "公里" or "km" or "kilometer" or "kilometers"?$"{v} Kilometers":$"{v} Meters";}var nm=System.Text.RegularExpressions.Regex.Match(rd,@"\d+(?:\.\d+)?");return nm.Success?$"{nm.Value} Meters":rd;}

    private static string SanitizeDatasetName(string value)
    {
        var result = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

        result = result.Trim('_');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "Ai_Output";
        }

        if (char.IsDigit(result[0]))
        {
            result = $"Ai_{result}";
        }

        return result.Length > 48 ? result[..48] : result;
    }
}

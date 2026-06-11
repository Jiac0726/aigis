using System.Text;
using System.Text.Json;
using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Ai;

internal sealed class PromptOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string BuildPrompt(AiRequestContext context, IReadOnlyList<ConversationTurn>? history = null)
    {
        var prompt = new StringBuilder();
        if (history is { Count: > 0 })
        {
            prompt.AppendLine("=== 历史对话 (当用户说 刚才/上一步/那个结果/之前 时引用这些输出图层) ===");
            foreach (var turn in history.TakeLast(5))
                prompt.AppendLine(turn.ToPromptLine());
            prompt.AppendLine("=== 当前请求 ===");
        }
        prompt.AppendLine("You are an ArcGIS Pro 3.3 add-in planner.");
        prompt.AppendLine("Return only a JSON object matching one of the provided schemas.");
        prompt.AppendLine("Never return executable code.");
        prompt.AppendLine("The response must be valid json.");
        prompt.AppendLine("For a simple request, return a single-step tool plan:");
        prompt.AppendLine("""- {"intent":"cartography","toolName":"zoom_to_layer","parameters":{"layerName":"Roads"},"requiresConfirmation":false,"summary":"Zoom to the Roads layer."}""");
        prompt.AppendLine("""- {"intent":"cartography","toolName":"apply_symbology_from_layer","parameters":{"layerName":"Roads_Near_Schools","symbologyLayer":"Roads_Style"},"requiresConfirmation":false,"summary":"Apply the Roads_Style symbology to Roads_Near_Schools."}""");
        prompt.AppendLine("""- {"intent":"cartography","toolName":"export_map_view","parameters":{"outputName":"analysis_result_map","resolution":"200"},"requiresConfirmation":false,"summary":"Export the active map view to a PNG file."}""");
        prompt.AppendLine("""- {"intent":"analysis","toolName":"buffer","parameters":{"inputLayer":"Schools","distance":"500 Meters","outputName":"Schools_Buffer_500m"},"requiresConfirmation":true,"summary":"Create a 500 meter buffer around Schools."}""");
        prompt.AppendLine("""- {"intent":"analysis","toolName":"clip","parameters":{"inputLayer":"Roads","clipLayer":"Districts","outputName":"Roads_Clip"},"requiresConfirmation":true,"summary":"Clip Roads by Districts."}""");
        prompt.AppendLine("""- {"intent":"analysis","toolName":"select_by_attribute","parameters":{"inputLayer":"Parcels","whereClause":"LANDUSE = 'Commercial'","selectionType":"NEW_SELECTION"},"requiresConfirmation":false,"summary":"Select commercial parcels."}""");
        prompt.AppendLine("""- {"intent":"analysis","toolName":"select_by_location","parameters":{"inputLayer":"Schools","selectingLayer":"Roads","overlapType":"WITHIN_A_DISTANCE","searchDistance":"500 Meters","selectionType":"NEW_SELECTION"},"requiresConfirmation":false,"summary":"Select schools within 500 meters of roads."}""");
        prompt.AppendLine("For a complex request, return a workflow plan:");
        prompt.AppendLine("""{"summary":"Find roads near schools and summarize them by district.","requiresConfirmation":true,"steps":[{"intent":"analysis","toolName":"buffer","parameters":{"inputLayer":"Schools","distance":"500 Meters","outputName":"Schools_Buffer_500m"},"requiresConfirmation":true,"summary":"Create school buffers."},{"intent":"analysis","toolName":"clip","parameters":{"inputLayer":"Roads","clipLayer":"Schools_Buffer_500m","outputName":"Roads_Near_Schools"},"requiresConfirmation":true,"summary":"Clip roads by the school buffer."},{"intent":"analysis","toolName":"spatial_join","parameters":{"targetLayer":"Districts","joinLayer":"Roads_Near_Schools","outputName":"District_Roads_Join","joinOperation":"JOIN_ONE_TO_ONE","joinType":"KEEP_ALL","matchOption":"INTERSECT"},"requiresConfirmation":true,"summary":"Join nearby roads to districts."}]}""");
        prompt.AppendLine("Allowed tools and required parameters:");
        prompt.AppendLine("- zoom_to_layer: layerName");
        prompt.AppendLine("- apply_symbology_from_layer: layerName, symbologyLayer. symbologyLayer must be an existing styled layer in the current map.");
        prompt.AppendLine("- apply_unique_value_symbology: alias of apply_symbology_from_layer and requires layerName, symbologyLayer.");
        prompt.AppendLine("- export_map_view: optional outputName, outputPath, resolution. Exports the active map view as PNG.");
        prompt.AppendLine("- export_layout: alias of export_map_view for now.");
        prompt.AppendLine("- buffer: inputLayer, distance, outputName");
        prompt.AppendLine("- clip: inputLayer, clipLayer, outputName");
        prompt.AppendLine("- select_by_attribute: inputLayer, whereClause, optional selectionType");
        prompt.AppendLine("- select_by_location: inputLayer, selectingLayer, optional overlapType, searchDistance, selectionType");
        prompt.AppendLine("- intersect: inputLayers as semicolon-separated layer names, outputName, optional joinAttributes, clusterTolerance, outputType");
        prompt.AppendLine("- spatial_join: targetLayer, joinLayer, outputName, optional joinOperation, joinType, matchOption, searchRadius");
        prompt.AppendLine("- summary_statistics: inputLayer, statisticsFields, outputName, optional caseFields. statisticsFields format: NAME SUM;POPULATION MEAN");
        prompt.AppendLine();
        prompt.AppendLine("CRITICAL: Always use the EXACT layer names from the current map. For distance parameters, always use English format like '500 Meters' or '2 Kilometers'.");
        prompt.AppendLine("For Chinese distance phrases like 500米, return distance as '500 Meters'.");
        prompt.AppendLine();
        prompt.AppendLine($"User input: {context.UserInput}");
        prompt.AppendLine();
        AppendContext(prompt, context);
        return prompt.ToString();
    }

    public string BuildRepairPrompt(
        AiRequestContext context,
        AiWorkflowPlan workflow,
        IReadOnlyList<WorkflowDiagnostic> diagnostics)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are repairing an ArcGIS Pro 3.3 workflow plan.");
        prompt.AppendLine("Return only corrected JSON. Do not explain outside JSON.");
        prompt.AppendLine("Keep the user intent, but fix layer names, field names, SQL clauses, and output references using the current project context.");
        prompt.AppendLine("Do not invent layer names or field names.");
        prompt.AppendLine("If a referenced layer or field cannot be resolved, replace that step with the closest safe non-destructive step and explain the unresolved requirement in the workflow summary.");
        prompt.AppendLine("The corrected JSON must use the same workflow schema: summary, requiresConfirmation, steps.");
        prompt.AppendLine();
        prompt.AppendLine("Allowed tools:");
        prompt.AppendLine("- zoom_to_layer: layerName");
        prompt.AppendLine("- apply_symbology_from_layer: layerName, symbologyLayer");
        prompt.AppendLine("- export_map_view: optional outputName, outputPath, resolution");
        prompt.AppendLine("- buffer: inputLayer, distance, outputName");
        prompt.AppendLine("- clip: inputLayer, clipLayer, outputName");
        prompt.AppendLine("- select_by_attribute: inputLayer, whereClause, optional selectionType");
        prompt.AppendLine("- select_by_location: inputLayer, selectingLayer, optional overlapType, searchDistance, selectionType");
        prompt.AppendLine("- intersect: inputLayers as semicolon-separated layer names, outputName");
        prompt.AppendLine("- spatial_join: targetLayer, joinLayer, outputName");
        prompt.AppendLine("- summary_statistics: inputLayer, statisticsFields, outputName, optional caseFields");
        prompt.AppendLine();
        prompt.AppendLine($"User input: {context.UserInput}");
        prompt.AppendLine("Current project context:");
        AppendContext(prompt, context);
        prompt.AppendLine();
        prompt.AppendLine("Original workflow JSON:");
        prompt.AppendLine(JsonSerializer.Serialize(workflow, JsonOptions));
        prompt.AppendLine();
        prompt.AppendLine("Diagnostics to repair:");
        foreach (var diagnostic in diagnostics)
        {
            prompt.AppendLine($"- Step {diagnostic.StepNumber} [{diagnostic.Severity}]: {diagnostic.Message} Suggestion: {diagnostic.Suggestion}");
        }

        return prompt.ToString();
    }

    
    public string BuildAnalysisPrompt(AiRequestContext context)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are analyzing an ArcGIS Pro 3.3 GIS project.");
        prompt.AppendLine("Based on the layers below, suggest 2-3 useful multi-step GIS analysis workflows.");
        prompt.AppendLine("Return ONLY valid JSON. No markdown, no code blocks.");
        prompt.AppendLine(@"{""summary"":""project overview"",""suggestions"":[{""title"":""...分析方案标题..."",""description"":""...描述..."",""workflow"":{""summary"":""...workflow描述..."",""requiresConfirmation"":true,""steps"":[{""intent"":""analysis"",""toolName"":""buffer"",""parameters"":{""inputLayer"":""..."",""distance"":""500 Meters"",""outputName"":""...""},,""requiresConfirmation"":true,""summary"":""...""}]}}]}");
        prompt.AppendLine("Allowed tools: buffer, clip, intersect, spatial_join, select_by_attribute, select_by_location, summary_statistics, zoom_to_layer, apply_symbology_from_layer.");
        prompt.AppendLine("Each suggestion.workflow must be a complete executable AiWorkflowPlan with steps.");
        prompt.AppendLine();
        AppendContext(prompt, context);
        return prompt.ToString();
    }

    private static void AppendContext(StringBuilder prompt, AiRequestContext context)
    {
        prompt.AppendLine($"Active map: {context.ActiveMapName ?? "(none)"}");
        prompt.AppendLine($"View extent: {context.ViewExtent ?? "(unknown)"}");
        prompt.AppendLine("Layers:");

        foreach (var layerName in context.LayerNames)
        {
            prompt.AppendLine($"- {layerName}");
        }

        prompt.AppendLine("Layer profiles:");
        foreach (var layer in context.Layers)
        {
            prompt.AppendLine($"- name={layer.Name}; type={layer.LayerType}; geometry={layer.GeometryType ?? "(unknown)"}; visible={layer.IsVisible}; selectable={layer.IsSelectable}; count={layer.RowCount?.ToString() ?? "(unknown)"}; definitionQuery={layer.DefinitionQuery ?? "(none)"}");
            if (layer.Fields.Count == 0)
            {
                prompt.AppendLine("  fields: (unknown)");
                continue;
            }

            prompt.AppendLine("  fields:");
            foreach (var field in layer.Fields)
            {
                prompt.AppendLine($"  - {field.Name} ({field.FieldType}, alias={field.Alias}, nullable={field.IsNullable})");
            }
        }
    }
}
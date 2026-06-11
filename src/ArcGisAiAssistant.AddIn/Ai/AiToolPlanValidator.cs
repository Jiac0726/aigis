using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Ai;

internal static class AiToolPlanValidator
{
    private static readonly HashSet<string> CartographyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "zoom_to_layer",
        "apply_symbology_from_layer",
        "apply_unique_value_symbology",
        "export_map_view",
        "export_layout"
    };

    private static readonly HashSet<string> AnalysisTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "buffer",
        "clip",
        "select_by_attribute",
        "select_by_location",
        "intersect",
        "spatial_join",
        "summary_statistics"
    };

    public static ExecutionResult? Validate(AiToolPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.Intent))
        {
            return ExecutionResult.Failure("AI plan is missing intent.");
        }

        if (string.IsNullOrWhiteSpace(plan.ToolName))
        {
            return ExecutionResult.Failure("AI plan is missing toolName.");
        }

        if (string.Equals(plan.Intent, "cartography", StringComparison.OrdinalIgnoreCase))
        {
            if (!CartographyTools.Contains(plan.ToolName))
            {
                return ExecutionResult.Failure($"Unsupported cartography tool: {plan.ToolName}.");
            }
        }
        else if (string.Equals(plan.Intent, "analysis", StringComparison.OrdinalIgnoreCase))
        {
            if (!AnalysisTools.Contains(plan.ToolName))
            {
                return ExecutionResult.Failure($"Unsupported analysis tool: {plan.ToolName}.");
            }
        }
        else
        {
            return ExecutionResult.Failure($"Unknown AI intent: {plan.Intent}.");
        }

        return plan.ToolName.ToLowerInvariant() switch
        {
            "zoom_to_layer" => Require(plan, "layerName"),
            "apply_symbology_from_layer" => Require(plan, "layerName", "symbologyLayer"),
            "apply_unique_value_symbology" => Require(plan, "layerName", "symbologyLayer"),
            "export_map_view" => null,
            "export_layout" => null,
            "buffer" => Require(plan, "inputLayer", "distance"),
            "clip" => Require(plan, "inputLayer", "clipLayer"),
            "select_by_attribute" => Require(plan, "inputLayer", "whereClause"),
            "select_by_location" => Require(plan, "inputLayer", "selectingLayer"),
            "intersect" => Require(plan, "inputLayers", "outputName"),
            "spatial_join" => Require(plan, "targetLayer", "joinLayer", "outputName"),
            "summary_statistics" => Require(plan, "inputLayer", "statisticsFields", "outputName"),
            _ => null
        };
    }

    private static ExecutionResult? Require(AiToolPlan plan, params string[] parameterNames)
    {
        var missing = parameterNames
            .Where(name => !plan.Parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();

        return missing.Length == 0
            ? null
            : ExecutionResult.Failure($"Missing required parameter(s): {string.Join(", ", missing)}.");
    }
}

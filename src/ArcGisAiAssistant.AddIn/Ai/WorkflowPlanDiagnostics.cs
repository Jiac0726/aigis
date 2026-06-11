using System.Text.RegularExpressions;
using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Ai;

internal static class WorkflowPlanDiagnostics
{
    private static readonly Regex FieldTokenRegex = new(
        @"(?<!['""])\b[A-Za-z_][A-Za-z0-9_]*\b(?=\s*(=|<>|!=|>|<|>=|<=|LIKE|IN|IS)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND",
        "OR",
        "NOT",
        "NULL",
        "LIKE",
        "IN",
        "IS"
    };

    public static IReadOnlyList<WorkflowDiagnostic> Analyze(AiWorkflowPlan workflow, AiRequestContext context)
    {
        var diagnostics = new List<WorkflowDiagnostic>();
        var availableOutputs = new HashSet<string>(context.LayerNames, StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            var stepNumber = index + 1;

            diagnostics.AddRange(AnalyzeLayerReferences(step, stepNumber, context, availableOutputs));
            diagnostics.AddRange(AnalyzeFieldReferences(step, stepNumber, context));

            if (step.Parameters.TryGetValue("outputName", out var outputName) &&
                !string.IsNullOrWhiteSpace(outputName))
            {
                availableOutputs.Add(outputName);
            }
        }

        return diagnostics;
    }

    public static string BuildClarificationMessage(IReadOnlyList<WorkflowDiagnostic> diagnostics)
    {
        var blocking = diagnostics
            .Where(diagnostic => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (blocking.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            blocking.Select(diagnostic => $"Step {diagnostic.StepNumber}: {diagnostic.Message} Suggestion: {diagnostic.Suggestion}"));
    }

    private static IEnumerable<WorkflowDiagnostic> AnalyzeLayerReferences(
        AiToolPlan step,
        int stepNumber,
        AiRequestContext context,
        ISet<string> availableOutputs)
    {
        foreach (var parameterName in GetLayerParameterNames(step.ToolName))
        {
            if (!step.Parameters.TryGetValue(parameterName, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var layerNames = parameterName.Equals("inputLayers", StringComparison.OrdinalIgnoreCase)
                ? SplitList(value)
                : new[] { value };

            foreach (var layerName in layerNames)
            {
                if (availableOutputs.Contains(layerName))
                {
                    continue;
                }

                var nearest = FindNearestLayer(layerName, context.LayerNames);
                yield return new WorkflowDiagnostic(
                    stepNumber,
                    "error",
                    $"Layer '{layerName}' was not found in the current map or earlier workflow outputs.",
                    string.IsNullOrWhiteSpace(nearest)
                        ? $"Choose one of: {string.Join(", ", context.LayerNames)}"
                        : $"Did you mean '{nearest}'?");
            }
        }
    }

    private static IEnumerable<WorkflowDiagnostic> AnalyzeFieldReferences(
        AiToolPlan step,
        int stepNumber,
        AiRequestContext context)
    {
        if (!step.Parameters.TryGetValue("whereClause", out var whereClause) ||
            !step.Parameters.TryGetValue("inputLayer", out var inputLayerName))
        {
            yield break;
        }

        var layer = FindLayer(inputLayerName, context.Layers);
        if (layer is null || layer.Fields.Count == 0)
        {
            yield break;
        }

        var fieldNames = new HashSet<string>(layer.Fields.Select(field => field.Name), StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FieldTokenRegex.Matches(whereClause))
        {
            var candidate = match.Value;
            if (SqlKeywords.Contains(candidate) || fieldNames.Contains(candidate))
            {
                continue;
            }

            var nearest = FindNearestLayer(candidate, layer.Fields.Select(field => field.Name));
            yield return new WorkflowDiagnostic(
                stepNumber,
                "error",
                $"Field '{candidate}' was not found on layer '{layer.Name}'.",
                string.IsNullOrWhiteSpace(nearest)
                    ? $"Use one of these fields: {string.Join(", ", layer.Fields.Take(20).Select(field => field.Name))}"
                    : $"Did you mean field '{nearest}'?");
        }
    }

    private static IReadOnlyList<string> GetLayerParameterNames(string toolName)
    {
        return toolName.ToLowerInvariant() switch
        {
            "zoom_to_layer" => new[] { "layerName" },
            "apply_symbology_from_layer" => new[] { "layerName", "symbologyLayer" },
            "apply_unique_value_symbology" => new[] { "layerName", "symbologyLayer" },
            "buffer" => new[] { "inputLayer" },
            "clip" => new[] { "inputLayer", "clipLayer" },
            "select_by_attribute" => new[] { "inputLayer" },
            "select_by_location" => new[] { "inputLayer", "selectingLayer" },
            "intersect" => new[] { "inputLayers" },
            "spatial_join" => new[] { "targetLayer", "joinLayer" },
            "summary_statistics" => new[] { "inputLayer" },
            _ => Array.Empty<string>()
        };
    }

    private static LayerProfile? FindLayer(string layerName, IReadOnlyList<LayerProfile> layers)
    {
        return layers.FirstOrDefault(layer => string.Equals(layer.Name, layerName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitList(string value)
    {
        return value
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string? FindNearestLayer(string requested, IEnumerable<string> candidates)
    {
        var normalizedRequested = Normalize(requested);
        return candidates
            .Select(candidate => new
            {
                Value = candidate,
                Normalized = Normalize(candidate)
            })
            .Where(candidate => candidate.Normalized.Contains(normalizedRequested, StringComparison.OrdinalIgnoreCase) ||
                                normalizedRequested.Contains(candidate.Normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Normalized.Length)
            .Select(candidate => candidate.Value)
            .FirstOrDefault();
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value, @"[\s_\-：:，,。\.图层字段]", string.Empty);
    }
}

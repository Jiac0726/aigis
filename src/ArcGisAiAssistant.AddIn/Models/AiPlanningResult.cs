namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record AiPlanningResult
{
    public AiToolPlan Plan { get; init; } = new();

    public AiWorkflowPlan Workflow { get; init; } = new();

    public string RawModelResponse { get; init; } = string.Empty;

    public string ParsedJson { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public string FallbackReason { get; init; } = string.Empty;
}

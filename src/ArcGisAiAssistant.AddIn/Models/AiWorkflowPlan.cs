namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record AiWorkflowPlan
{
    public string Summary { get; init; } = string.Empty;

    public bool RequiresConfirmation { get; init; }

    public IReadOnlyList<AiToolPlan> Steps { get; init; } = Array.Empty<AiToolPlan>();
}

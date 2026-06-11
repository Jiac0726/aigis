namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record WorkflowSuggestion(
    string Title,
    string Description,
    AiWorkflowPlan? Workflow)
{
    public string ToDisplayText()
    {
        var steps = Workflow?.Steps.Count ?? 0;
        return $"【{Title}】({steps} steps)`n{Description}";
    }
}

namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record ProjectAnalysisResult(
    string Summary,
    IReadOnlyList<WorkflowSuggestion> Suggestions);

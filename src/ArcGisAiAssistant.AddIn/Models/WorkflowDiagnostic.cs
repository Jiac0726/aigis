namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record WorkflowDiagnostic(
    int StepNumber,
    string Severity,
    string Message,
    string Suggestion);

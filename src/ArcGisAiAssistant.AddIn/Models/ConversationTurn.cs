namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record ConversationTurn(
    string UserInput,
    string WorkflowSummary,
    IReadOnlyList<string> OutputArtifacts,
    bool AllSucceeded,
    DateTime Timestamp)
{
    public string ToPromptLine()
    {
        var status = AllSucceeded ? "OK" : "FAIL";
        var artifacts = OutputArtifacts.Count > 0
            ? $" -> outputs: {string.Join(", ", OutputArtifacts)}"
            : "";
        return $"[{Timestamp:HH:mm}] [{UserInput}] -> {WorkflowSummary} ({status}){artifacts}";
    }
}

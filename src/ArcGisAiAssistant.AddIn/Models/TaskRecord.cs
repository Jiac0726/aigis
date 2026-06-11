namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record TaskRecord(
    string Id,
    DateTime Timestamp,
    string UserInput,
    string WorkflowSummary,
    IReadOnlyList<string> StepResults,
    bool AllSucceeded,
    IReadOnlyList<string> OutputArtifacts,
    string? Error)
{
    public string TimeLabel => Timestamp.ToString("HH:mm");
    public string StatusIcon => AllSucceeded ? "OK" : "FAIL";

    public string DialogueText
    {
        get
        {
            var lines = new List<string>
            {
                $"用户: {UserInput}",
                $"AI:   {WorkflowSummary}"
            };
            for (int i = 0; i < StepResults.Count; i++)
                lines.Add($"  {i + 1}. {StepResults[i]}");
            if (OutputArtifacts.Count > 0)
                lines.Add($"产出: {string.Join(", ", OutputArtifacts)}");
            if (!AllSucceeded && Error is not null)
                lines.Add($"失败: {Error}");
            return string.Join("\n", lines);
        }
    }
}

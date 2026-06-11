namespace ArcGisAiAssistant.AddIn.Models;

internal sealed record ExecutionResult(
    bool Succeeded,
    string Message,
    string? OutputLayerName = null,
    IReadOnlyList<string>? Logs = null,
    string? Error = null)
{
    public static ExecutionResult Success(string message, string? outputLayerName = null, IReadOnlyList<string>? logs = null)
    {
        return new ExecutionResult(true, message, outputLayerName, logs);
    }

    public static ExecutionResult Failure(string message, string? error = null, IReadOnlyList<string>? logs = null)
    {
        return new ExecutionResult(false, message, null, logs, error);
    }
}

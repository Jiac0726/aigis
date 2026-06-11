using System.IO;
using System.Text.Json;


namespace ArcGisAiAssistant.AddIn.Services;

internal sealed class AuditLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcGisAiAssistant", "audit_log.jsonl");

    private static readonly object Lock = new();

    public static void Log(string toolName, string parameters, bool succeeded, string? error)
    {
        var entry = new
        {
            timestamp = DateTime.Now.ToString("O"),
            tool = toolName,
            parameters,
            succeeded,
            error
        };
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
            catch { }
        }
    }
}

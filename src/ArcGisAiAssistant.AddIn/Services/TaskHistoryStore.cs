using System.IO;
using System.Text.Json;

using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Services;

internal sealed class TaskHistoryStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcGisAiAssistant", "task_history.json");

    private const int MaxRecords = 200;

    public List<TaskRecord> Records { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var json = File.ReadAllText(StorePath);
                Records = JsonSerializer.Deserialize<List<TaskRecord>>(json) ?? new();
            }
        }
        catch { Records = new(); }
    }

    public void Save(TaskRecord record)
    {
        Records.Insert(0, record);
        if (Records.Count > MaxRecords)
            Records.RemoveRange(MaxRecords, Records.Count - MaxRecords);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(Records));
        }
        catch { }
    }

    public void Clear()
    {
        Records.Clear();
        try { File.Delete(StorePath); } catch { }
    }
}

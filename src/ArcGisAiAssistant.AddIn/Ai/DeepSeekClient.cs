using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Ai;

internal sealed class DeepSeekClient
{
    private const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
    private const string ModelEnvironmentVariable = "DEEPSEEK_MODEL";
    private const string DefaultModel = "deepseek-v4-pro";
    private static readonly Uri ChatCompletionsEndpoint = new("https://api.deepseek.com/chat/completions");
    private readonly HttpClient _httpClient;

    public DeepSeekClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<AiPlanningResult> CreateToolPlanAsync(string prompt, CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{ApiKeyEnvironmentVariable} is not configured. Checked environment variables and these files: {string.Join("; ", GetDotEnvSearchPaths())}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = GetSetting(ModelEnvironmentVariable) ?? DefaultModel,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are an ArcGIS Pro GIS copilot. You MUST respond with ONLY a valid JSON object. No markdown, no explanations, no code blocks. Just raw JSON."
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            },
            stream = false,
            max_tokens = 2048
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var outputText = ExtractOutputText(json);
        try
        {
            var parsedJson = ExtractToolPlanJson(outputText);
            var workflow = ParseWorkflowPlanJson(parsedJson);
            return new AiPlanningResult
            {
                Plan = workflow.Steps.FirstOrDefault() ?? new AiToolPlan(),
                Workflow = workflow,
                RawModelResponse = outputText,
                ParsedJson = parsedJson
            };
        }
        catch (InvalidOperationException ex) when (RuleBasedPlanFallback.TryCreate(prompt, out var fallbackPlan))
        {
            return new AiPlanningResult
            {
                Plan = fallbackPlan,
                Workflow = CreateSingleStepWorkflow(fallbackPlan),
                RawModelResponse = outputText,
                UsedFallback = true,
                FallbackReason = ex.Message
            };
        }
    }

    


    public async Task<ProjectAnalysisResult> AnalyzeProjectAsync(string prompt, CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{ApiKeyEnvironmentVariable} is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = GetSetting(ModelEnvironmentVariable) ?? DefaultModel,
            messages = new[]
            {
                new { role = "system", content = "You produce only valid JSON for GIS project analysis. No markdown." },
                new { role = "user", content = prompt }
            },
            stream = false,
            max_tokens = 2048
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var outputText = ExtractOutputText(json);
        var parsedJson = ExtractToolPlanJson(outputText);

        using var doc = JsonDocument.Parse(parsedJson);
        var root = doc.RootElement;

        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
        var suggestions = new List<WorkflowSuggestion>();

        if (root.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                AiWorkflowPlan? wf = null;
                if (item.TryGetProperty("workflow", out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    try { wf = ParseWorkflowPlanJson(w.GetRawText()); } catch { }
                }
                suggestions.Add(new WorkflowSuggestion(title, desc, wf));
            }
        }

        return new ProjectAnalysisResult(summary, suggestions);
    }

    private static string ExtractOutputText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices))
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message))
                {
                    continue;
                }

                if (message.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? string.Empty;
                }
            }
        }

        throw new InvalidOperationException("Could not read JSON tool plan from DeepSeek response.");
    }

    private static AiToolPlan ParseToolPlan(string outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("DeepSeek returned an empty tool plan.");
        }

        var json = ExtractToolPlanJson(outputText);
        return ParseToolPlanJson(json);
    }

    private static AiToolPlan ParseToolPlanJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseToolPlanElement(document.RootElement);
    }

    private static AiWorkflowPlan ParseWorkflowPlanJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("steps", out var stepsElement) &&
            stepsElement.ValueKind == JsonValueKind.Array)
        {
            var steps = stepsElement
                .EnumerateArray()
                .Select(ParseToolPlanElement)
                .ToArray();
            var summary = root.TryGetProperty("summary", out var summaryElement) &&
                          summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var requiresConfirmation = root.TryGetProperty("requiresConfirmation", out var confirmationElement) &&
                                       confirmationElement.ValueKind == JsonValueKind.True;

            return new AiWorkflowPlan
            {
                Summary = string.IsNullOrWhiteSpace(summary)
                    ? string.Join(" Then ", steps.Select(step => step.Summary))
                    : summary,
                RequiresConfirmation = requiresConfirmation || steps.Any(step => step.RequiresConfirmation),
                Steps = steps
            };
        }

        return CreateSingleStepWorkflow(ParseToolPlanElement(root));
    }

    private static AiToolPlan ParseToolPlanElement(JsonElement root)
    {
        var intent = GetRequiredString(root, "intent");
        var toolName = GetRequiredString(root, "toolName");
        var summary = GetRequiredString(root, "summary");
        var requiresConfirmation = root.TryGetProperty("requiresConfirmation", out var confirmationElement) &&
                                   confirmationElement.ValueKind == JsonValueKind.True;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("parameters", out var parametersElement) &&
            parametersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parametersElement.EnumerateObject())
            {
                parameters[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText()
                };
            }
        }

        return new AiToolPlan
        {
            Intent = intent,
            ToolName = toolName,
            Summary = summary,
            RequiresConfirmation = requiresConfirmation,
            Parameters = parameters
        };
    }

    private static AiWorkflowPlan CreateSingleStepWorkflow(AiToolPlan plan)
    {
        return new AiWorkflowPlan
        {
            Summary = plan.Summary,
            RequiresConfirmation = plan.RequiresConfirmation,
            Steps = new[] { plan }
        };
    }

    private static string ExtractToolPlanJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        if (TryExtractRawJsonCandidate(trimmed, out var candidate))
        {
            trimmed = candidate;
        }

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(
                $"DeepSeek returned empty response. Full text: {CreatePreview(text)}");
        }

        using var document = JsonDocument.Parse(trimmed);
        if (TryFindToolPlanElement(document.RootElement, out var planElement))
        {
            return planElement.GetRawText();
        }

        throw new InvalidOperationException(
            $"DeepSeek response did not contain a JSON tool plan. Response preview: {CreatePreview(text)}");
    }

    private static bool TryExtractRawJsonCandidate(string text, out string json)
    {
        json = text;

        var objectStart = text.IndexOf('{');
        var objectEnd = text.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            json = text[objectStart..(objectEnd + 1)];
            return true;
        }

        var arrayStart = text.IndexOf('[');
        var arrayEnd = text.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            json = text[arrayStart..(arrayEnd + 1)];
            return true;
        }

        return false;
    }

    private static bool TryFindToolPlanElement(JsonElement element, out JsonElement planElement)
    {
        if (LooksLikeToolPlan(element))
        {
            planElement = element;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var json = ExtractToolPlanJson(text);
                    using var document = JsonDocument.Parse(json);
                    planElement = document.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindToolPlanElement(item, out planElement))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindToolPlanElement(property.Value, out planElement))
                {
                    return true;
                }
            }
        }

        planElement = default;
        return false;
    }

    private static bool LooksLikeToolPlan(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               ((element.TryGetProperty("intent", out _) &&
                 element.TryGetProperty("toolName", out _)) ||
                element.TryGetProperty("steps", out _));
    }

    private static string CreatePreview(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException($"DeepSeek tool plan is missing required string property: {propertyName}.");
        }

        return element.GetString()!;
    }

    private static string? GetApiKey()
    {
        var apiKey = GetSetting(ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    private static string? GetSetting(string key)
    {
        var apiKey = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        foreach (var filePath in GetDotEnvSearchPaths())
        {
            apiKey = TryReadValueFromDotEnv(filePath, key);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return apiKey;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDotEnvSearchPaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        yield return Path.Combine(baseDirectory, ".env");
        yield return Path.Combine(documents, "New project", ".env");
        yield return Path.Combine(userProfile, ".arcgis-ai-assistant.env");

        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, ".env");
            directory = directory.Parent;
        }
    }

    private static string? TryReadValueFromDotEnv(string filePath, string settingKey)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            if (!string.Equals(key, settingKey, StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed[(separatorIndex + 1)..].Trim().Trim('"');
        }

        return null;
    }
}

internal static class RuleBasedPlanFallback
{
    public static bool TryCreate(string prompt, out AiToolPlan plan)
    {
        var userInput = ExtractLineValue(prompt, "User input:");
        var layerNames = ExtractLayerNames(prompt);

        if (TryCreateZoomPlan(userInput, layerNames, out plan))
        {
            return true;
        }

        if (TryCreateBufferPlan(userInput, layerNames, out plan))
        {
            return true;
        }

        if (TryCreateClipPlan(userInput, layerNames, out plan))
        {
            return true;
        }

        plan = new AiToolPlan();
        return false;
    }

    private static bool TryCreateZoomPlan(string userInput, IReadOnlyList<string> layerNames, out AiToolPlan plan)
    {
        plan = new AiToolPlan();
        if (!ContainsAny(userInput, "缩放", "定位", "zoom"))
        {
            return false;
        }

        var layerName = FindMentionedLayer(userInput, layerNames);
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return false;
        }

        plan = new AiToolPlan
        {
            Intent = "cartography",
            ToolName = "zoom_to_layer",
            RequiresConfirmation = false,
            Summary = $"缩放到图层 {layerName}。",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["layerName"] = layerName
            }
        };
        return true;
    }

    private static bool TryCreateBufferPlan(string userInput, IReadOnlyList<string> layerNames, out AiToolPlan plan)
    {
        plan = new AiToolPlan();
        if (!ContainsAny(userInput, "缓冲", "buffer"))
        {
            return false;
        }

        var layerName = FindMentionedLayer(userInput, layerNames);
        var distance = ExtractDistance(userInput);
        if (string.IsNullOrWhiteSpace(layerName) || string.IsNullOrWhiteSpace(distance))
        {
            return false;
        }

        plan = new AiToolPlan
        {
            Intent = "analysis",
            ToolName = "buffer",
            RequiresConfirmation = true,
            Summary = $"为 {layerName} 创建 {distance} 缓冲区。",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputLayer"] = layerName,
                ["distance"] = distance,
                ["outputName"] = CreateOutputName(layerName, "Buffer")
            }
        };
        return true;
    }

    private static bool TryCreateClipPlan(string userInput, IReadOnlyList<string> layerNames, out AiToolPlan plan)
    {
        plan = new AiToolPlan();
        if (!ContainsAny(userInput, "裁剪", "clip"))
        {
            return false;
        }

        var mentionedLayers = layerNames
            .Where(layerName => Normalized(userInput).Contains(Normalized(layerName), StringComparison.OrdinalIgnoreCase) ||
                                Normalized(layerName).Contains(Normalized(userInput), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mentionedLayers.Count < 2)
        {
            return false;
        }

        var clipLayer = mentionedLayers[0];
        var inputLayer = mentionedLayers[1];

        var match = Regex.Match(userInput, @"用(?<clip>.+?)裁剪(?<input>.+)");
        if (match.Success)
        {
            clipLayer = FindMentionedLayer(match.Groups["clip"].Value, layerNames) ?? clipLayer;
            inputLayer = FindMentionedLayer(match.Groups["input"].Value, layerNames) ?? inputLayer;
        }

        plan = new AiToolPlan
        {
            Intent = "analysis",
            ToolName = "clip",
            RequiresConfirmation = true,
            Summary = $"用 {clipLayer} 裁剪 {inputLayer}。",
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputLayer"] = inputLayer,
                ["clipLayer"] = clipLayer,
                ["outputName"] = CreateOutputName(inputLayer, "Clip")
            }
        };
        return true;
    }

    private static string ExtractLineValue(string prompt, string prefix)
    {
        foreach (var line in prompt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ExtractLayerNames(string prompt)
    {
        var layerNames = new List<string>();
        var inLayerBlock = false;

        foreach (var line in prompt.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.Equals("Layers:", StringComparison.OrdinalIgnoreCase))
            {
                inLayerBlock = true;
                continue;
            }

            if (line.Equals("Selected layers:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (inLayerBlock && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                layerNames.Add(line.Trim()[2..].Trim());
            }
        }

        return layerNames;
    }

    private static string? FindMentionedLayer(string text, IReadOnlyList<string> layerNames)
    {
        var normalizedText = Normalized(text);
        return layerNames
            .OrderByDescending(layerName => Normalized(layerName).Length)
            .FirstOrDefault(layerName =>
            {
                var normalizedLayer = Normalized(layerName);
                return normalizedText.Contains(normalizedLayer, StringComparison.OrdinalIgnoreCase) ||
                       normalizedLayer.Contains(normalizedText, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string? ExtractDistance(string text)
    {
        var match = Regex.Match(text, @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>米|m|meter|meters|千米|公里|km|kilometer|kilometers)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value;
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit is "千米" or "公里" or "km" or "kilometer" or "kilometers"
            ? $"{value} Kilometers"
            : $"{value} Meters";
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalized(string value)
    {
        return Regex.Replace(value, @"[\s_\-：:，,。\.图层]", string.Empty);
    }

    private static string CreateOutputName(string layerName, string suffix)
    {
        var ascii = Regex.Replace(layerName, @"[^A-Za-z0-9_]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(ascii) ? $"Ai_{suffix}" : $"{ascii}_{suffix}";
    }
}

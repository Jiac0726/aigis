using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGisAiAssistant.AddIn.Ai;
using ArcGisAiAssistant.AddIn.ArcGis;
using ArcGisAiAssistant.AddIn.Models;
using ArcGisAiAssistant.AddIn.Services;
using ArcGisAiAssistant.AddIn.Ui;

namespace ArcGisAiAssistant.AddIn;

internal class AiAssistantDockPaneViewModel : DockPane
{
    private const string DockPaneId = "ArcGisAiAssistant_DockPane";
    private readonly DeepSeekClient _aiClient = new();
    private readonly PromptOrchestrator _promptOrchestrator = new();
    private readonly MapContextService _mapContextService = new();
    private readonly IntentRouter _intentRouter;
    private readonly List<ConversationTurn> _conversationHistory = new();
    private readonly TaskHistoryStore _taskHistoryStore = new();
    private string _userInput = string.Empty;
    private string _status = "Ready";
    private bool _isRunning;

    protected AiAssistantDockPaneViewModel()
    {
        _intentRouter = new IntentRouter(new MapCommandService(), new GeoprocessingService());
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(UserInput));
        AnalyzeProjectCommand = new AsyncRelayCommand(AnalyzeProjectAsync, () => !IsRunning);
        _taskHistoryStore.Load();
        foreach (var r in _taskHistoryStore.Records) TaskHistory.Add(r);
    }

    public ObservableCollection<string> Messages { get; } = new();
    public ObservableCollection<TaskRecord> TaskHistory { get; } = new();

    public ICommand SubmitCommand { get; }
    public ICommand AnalyzeProjectCommand { get; }

    public string UserInput
    {
        get => _userInput;
        set
        {
            if (SetProperty(ref _userInput, value))
            {
                ((AsyncRelayCommand)SubmitCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    private bool _isSandboxMode = true;
    private bool _maskExtent = true;
    private bool _maskFieldValues = true;
    private bool _requirePromptReview = true;
    public bool MaskExtent
    {
        get => _maskExtent;
        set => SetProperty(ref _maskExtent, value);
    }

    public bool MaskFieldValues
    {
        get => _maskFieldValues;
        set => SetProperty(ref _maskFieldValues, value);
    }

    public bool RequirePromptReview
    {
        get => _requirePromptReview;
        set => SetProperty(ref _requirePromptReview, value);
    }

    public bool IsSandboxMode
    {
        get => _isSandboxMode;
        set => SetProperty(ref _isSandboxMode, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                ((AsyncRelayCommand)SubmitCommand).RaiseCanExecuteChanged();
            }
        }
    }

    internal static void Show()
    {
        var pane = FrameworkApplication.DockPaneManager.Find(DockPaneId);
        if (pane is null)
        {
            ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                $"DockPane not found: {DockPaneId}. Rebuild the add-in and restart ArcGIS Pro.",
                "ArcGIS AI Assistant");
            return;
        }

        pane.Activate();
    }

    private async Task SubmitAsync()
    {
        var input = UserInput.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        IsRunning = true;
        Status = "1/6 Gathering context";
        AddStep("User input", input);

        try
        {
            var context = await _mapContextService.CreateContextAsync(input, _maskExtent, _maskFieldValues).ConfigureAwait(true);
            AddStep("Map context", FormatContext(context));

            Status = "2/6 Building prompt";
            var prompt = _promptOrchestrator.BuildPrompt(context, _conversationHistory);
            AddStep("Prompt sent to DeepSeek", Truncate(prompt, 2000));

            Status = "3/6 Asking DeepSeek";
            var planningResult = await _aiClient.CreateToolPlanAsync(prompt, CancellationToken.None).ConfigureAwait(true);
            var workflow = planningResult.Workflow;
            AddStep("DeepSeek raw response", Truncate(planningResult.RawModelResponse, 2000));

            if (planningResult.UsedFallback)
            {
                AddStep("Planner fallback", $"Used local rule-based planner because AI JSON parsing failed: {planningResult.FallbackReason}");
            }

            if (!string.IsNullOrWhiteSpace(planningResult.ParsedJson))
            {
                AddStep("Parsed JSON plan", planningResult.ParsedJson);
            }

            AddStep("Workflow plan", FormatWorkflow(workflow));

            Status = "4/6 Validating plan";
            var validationErrors = ValidateWorkflow(workflow);
            if (validationErrors.Length > 0)
            {
                AddStep("Validation failed", string.Join(
                    Environment.NewLine,
                    validationErrors.Select(item => $"Step {item.StepNumber}: {item.Error!.Message}")));
                var validationRepairDiagnostics = validationErrors
                    .Select(item => new WorkflowDiagnostic(
                        item.StepNumber,
                        "error",
                        item.Error!.Message,
                        "Fill missing required parameters using exact current project layers, fields, and the original user intent."))
                    .ToArray();
                var validationRepairResult = await TryRepairWorkflowAsync(context, workflow, validationRepairDiagnostics).ConfigureAwait(true);
                workflow = validationRepairResult.Workflow;
                if (!validationRepairResult.CanExecute)
                {
                    Status = "Needs clarification";
                    AddStep("Clarification needed", validationRepairResult.ClarificationMessage);
                    UserInput = $"请根据诊断修正计划：{validationRepairResult.ClarificationMessage}";
                    return;
                }
            }
            else
            {
                AddStep("Validation", $"Passed. {workflow.Steps.Count} step(s) are executable.");
                var repairResult = await TryRepairWorkflowAsync(context, workflow).ConfigureAwait(true);
                workflow = repairResult.Workflow;
                if (!repairResult.CanExecute)
                {
                    Status = "Needs clarification";
                    AddStep("Clarification needed", repairResult.ClarificationMessage);
                    UserInput = $"请根据诊断修正计划：{repairResult.ClarificationMessage}";
                    return;
                }
            }

            var hasDestructive = workflow.Steps.Any(s => s.ToolName is "buffer" or "clip" or "intersect" or "spatial_join");
            if (_isSandboxMode && hasDestructive)
            {
                AddStep("安全模式", "检测到会修改数据的操作。AI 输出将写入隔离沙箱 Geodatabase。");
            }

            if (workflow.RequiresConfirmation || (_isSandboxMode && hasDestructive))
            {
                Status = "5/6 Waiting for confirmation";
                AddStep("Confirmation required", workflow.Summary);
                var result = MessageBox.Show(workflow.Summary, "Confirm AI workflow", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (result != MessageBoxResult.OK)
                {
                    Status = "Cancelled";
                    AddStep("Confirmation", "User cancelled the action.");
                    return;
                }

                AddStep("Confirmation", "User approved the action.");
            }
            else
            {
                AddStep("Confirmation", "Not required for this action.");
            }

            Status = "6/6 Executing";
            var workflowArtifacts = new List<string>();
            var artifactMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < workflow.Steps.Count; index++)
            {
                var step = workflow.Steps[index];

                // Artifact chain: inject previous outputs into current parameters
                foreach (var key in step.Parameters.Keys.ToList())
                {
                    var val = step.Parameters[key];
                    if (artifactMap.TryGetValue(val, out var resolved))
                        step.Parameters[key] = resolved;
                }

                AddStep($"Executing step {index + 1}/{workflow.Steps.Count}", FormatPlan(step));
                var executionResult = await _intentRouter.ExecuteAsync(step, CancellationToken.None).ConfigureAwait(true);
                AddStep($"Step {index + 1} result", FormatExecutionResult(executionResult));

                if (executionResult.Logs is { Count: > 0 })
                {
                    foreach (var log in executionResult.Logs.Take(8))
                    {
                        AddStep($"Step {index + 1} ArcGIS log", log);
                    }
                }

                if (!string.IsNullOrWhiteSpace(executionResult.Error))
                {
                    AddStep($"Step {index + 1} execution error", executionResult.Error);
                }

                if (!executionResult.Succeeded)
                {
                    _conversationHistory.Add(new ConversationTurn(input, workflow.Summary, Array.Empty<string>(), false, DateTime.Now));
                    SaveTaskRecord(input, workflow, Array.Empty<string>(), false, executionResult.Error);
                    Status = "Failed";
                    return;
                }

                if (step.Parameters.TryGetValue("outputName", out var outputName) &&
                    !string.IsNullOrWhiteSpace(outputName))
                {
                    workflowArtifacts.Add($"{outputName} <= step {index + 1} {step.ToolName}");
                    artifactMap[outputName] = outputName;
                    if (step.Parameters.TryGetValue("inputLayer", out var inputForOutput))
                        artifactMap[inputForOutput + "_output"] = outputName;
                    AddStep("Workflow artifact", $"{outputName} available for next steps.");
                }
            }

            _conversationHistory.Add(new ConversationTurn(input, workflow.Summary, workflowArtifacts, true, DateTime.Now));
            SaveTaskRecord(input, workflow, workflowArtifacts, true, null);
            Status = "Ready";
            AddStep(
                "Workflow complete",
                workflowArtifacts.Count == 0
                    ? $"Executed {workflow.Steps.Count} step(s). No named output artifacts were produced."
                    : $"Executed {workflow.Steps.Count} step(s).{Environment.NewLine}Artifacts:{Environment.NewLine}{string.Join(Environment.NewLine, workflowArtifacts)}");
        }
        catch (Exception ex)
        {
            Status = "Failed";
            AddStep("Error", ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<WorkflowRepairResult> TryRepairWorkflowAsync(
        AiRequestContext context,
        AiWorkflowPlan workflow,
        IReadOnlyList<WorkflowDiagnostic>? preflightDiagnostics = null)
    {
        var diagnostics = preflightDiagnostics is { Count: > 0 }
            ? preflightDiagnostics
            : WorkflowPlanDiagnostics.Analyze(workflow, context);
        if (diagnostics.Count == 0)
        {
            AddStep("Project diagnostics", "Passed. Referenced layers and fields match the current project context.");
            return WorkflowRepairResult.Success(workflow);
        }

        AddStep("Project diagnostics", FormatDiagnostics(diagnostics));
        var clarification = WorkflowPlanDiagnostics.BuildClarificationMessage(diagnostics);
        if (string.IsNullOrWhiteSpace(clarification))
        {
            return WorkflowRepairResult.Success(workflow);
        }

        Status = "4/6 Repairing plan";
        AddStep("Automatic repair", "Trying one DeepSeek repair pass using project diagnostics.");
        var repairPrompt = _promptOrchestrator.BuildRepairPrompt(context, workflow, diagnostics);
        AddStep("Repair prompt sent to DeepSeek", Truncate(repairPrompt, 2000));

        try
        {
            var repairedPlanningResult = await _aiClient.CreateToolPlanAsync(repairPrompt, CancellationToken.None).ConfigureAwait(true);
            var repairedWorkflow = repairedPlanningResult.Workflow;
            AddStep("DeepSeek repair raw response", Truncate(repairedPlanningResult.RawModelResponse, 2000));
            if (!string.IsNullOrWhiteSpace(repairedPlanningResult.ParsedJson))
            {
                AddStep("Parsed repaired JSON plan", repairedPlanningResult.ParsedJson);
            }

            AddStep("Repaired workflow plan", FormatWorkflow(repairedWorkflow));

            var validationErrors = ValidateWorkflow(repairedWorkflow);
            if (validationErrors.Length > 0)
            {
                var message = string.Join(
                    Environment.NewLine,
                    validationErrors.Select(item => $"Step {item.StepNumber}: {item.Error!.Message}"));
                AddStep("Repaired validation failed", message);
                return WorkflowRepairResult.NeedsClarification(repairedWorkflow, message);
            }

            var repairedDiagnostics = WorkflowPlanDiagnostics.Analyze(repairedWorkflow, context);
            if (repairedDiagnostics.Count == 0)
            {
                AddStep("Repaired project diagnostics", "Passed. The repaired workflow matches the current project context.");
                return WorkflowRepairResult.Success(repairedWorkflow);
            }

            AddStep("Repaired project diagnostics", FormatDiagnostics(repairedDiagnostics));
            var repairedClarification = WorkflowPlanDiagnostics.BuildClarificationMessage(repairedDiagnostics);
            return string.IsNullOrWhiteSpace(repairedClarification)
                ? WorkflowRepairResult.Success(repairedWorkflow)
                : WorkflowRepairResult.NeedsClarification(repairedWorkflow, repairedClarification);
        }
        catch (Exception ex)
        {
            AddStep("Automatic repair failed", ex.Message);
            return WorkflowRepairResult.NeedsClarification(workflow, clarification);
        }
    }

    private static WorkflowValidationError[] ValidateWorkflow(AiWorkflowPlan workflow)
    {
        return workflow.Steps
            .Select((step, index) => new WorkflowValidationError(index + 1, AiToolPlanValidator.Validate(step)))
            .Where(item => item.Error is not null)
            .ToArray();
    }

    private static string FormatParameters(IReadOnlyDictionary<string, string> parameters)
    {
        return parameters.Count == 0
            ? "(none)"
            : string.Join(", ", parameters.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private void AddStep(string title, string content)
    {
        Messages.Add($"[{DateTime.Now:HH:mm:ss}] {title}\n{content}");
    }

    private static string FormatContext(AiRequestContext context)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"Active map: {context.ActiveMapName ?? "(none)"}",
            $"View extent: {context.ViewExtent ?? "(unknown)"}",
            $"Layers: {FormatList(context.LayerNames)}",
            $"Selected layers: {FormatList(context.SelectedLayerNames)}",
            $"Layer profiles:{Environment.NewLine}{FormatLayerProfiles(context.Layers)}"
        });
    }

    private static string FormatPlan(AiToolPlan plan)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"Summary: {plan.Summary}",
            $"Intent: {plan.Intent}",
            $"Tool: {plan.ToolName}",
            $"Requires confirmation: {plan.RequiresConfirmation}",
            $"Parameters: {FormatParameters(plan.Parameters)}"
        });
    }

    private static string FormatWorkflow(AiWorkflowPlan workflow)
    {
        var lines = new List<string>
        {
            $"Summary: {workflow.Summary}",
            $"Requires confirmation: {workflow.RequiresConfirmation}",
            $"Steps: {workflow.Steps.Count}"
        };

        for (var index = 0; index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            lines.Add($"{index + 1}. {step.Intent}/{step.ToolName} - {step.Summary} - {FormatParameters(step.Parameters)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatExecutionResult(ExecutionResult result)
    {
        var lines = new List<string>
        {
            $"Succeeded: {result.Succeeded}",
            $"Message: {result.Message}",
            $"Output layer: {result.OutputLayerName ?? "(none)"}"
        };

        if (result.Logs is { Count: > 0 })
        {
            lines.Add("ArcGIS messages:");
            foreach (var log in result.Logs.Take(8))
            {
                lines.Add($"  - {log}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            lines.Add($"Error: {result.Error}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDiagnostics(IReadOnlyList<WorkflowDiagnostic> diagnostics)
    {
        return diagnostics.Count == 0
            ? "No diagnostics."
            : string.Join(
                Environment.NewLine,
                diagnostics.Select(diagnostic =>
                    $"Step {diagnostic.StepNumber} [{diagnostic.Severity}]: {diagnostic.Message} {diagnostic.Suggestion}"));
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "(none)" : string.Join(", ", values);
    }

    private static string FormatLayerProfiles(IReadOnlyList<LayerProfile> layers)
    {
        if (layers.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            Environment.NewLine,
            layers.Select(layer =>
            {
                var fields = layer.Fields.Count == 0
                    ? "fields=(unknown)"
                    : $"fields={string.Join(", ", layer.Fields.Take(12).Select(field => $"{field.Name}:{field.FieldType}"))}";
                return $"- {layer.Name}: {layer.LayerType}, geometry={layer.GeometryType ?? "(unknown)"}, count={layer.RowCount?.ToString() ?? "(unknown)"}, {fields}";
            }));
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        }

        return value[..maxLength] + $"{Environment.NewLine}... truncated, total {value.Length} characters";
    }

    private sealed record WorkflowValidationError(int StepNumber, ExecutionResult? Error);


    private async Task AnalyzeProjectAsync()
    {
        IsRunning = true;
        Status = "Analyzing project";
        AddStep("分析项目", "正在扫描图层并生成分析方案...");

        try
        {
            var context = await _mapContextService.CreateContextAsync("分析项目").ConfigureAwait(true);
            AddStep("Map context", FormatContext(context));

            var analysisPrompt = _promptOrchestrator.BuildAnalysisPrompt(context);
            AddStep("Analysis prompt", Truncate(analysisPrompt, 1500));

            var analysisResult = await _aiClient.AnalyzeProjectAsync(analysisPrompt, CancellationToken.None).ConfigureAwait(true);
            AddStep("Project summary", analysisResult.Summary);

            for (var i = 0; i < analysisResult.Suggestions.Count; i++)
            {
                var sug = analysisResult.Suggestions[i];
                AddStep($"方案 {i + 1}", sug.ToDisplayText());
                if (sug.Workflow is not null)
                    AddStep($"方案 {i + 1} workflow", FormatWorkflow(sug.Workflow));
            }

            if (analysisResult.Suggestions.Count > 0)
            {
                Status = "Ready";
                AddStep("提示", "输入方案编号来执行，例如 '执行方案1'");
            }
            else
            {
                Status = "Ready";
                AddStep("结果", "未生成分析方案，请尝试更具体的问题。");
            }
        }
        catch (Exception ex)
        {
            Status = "Failed";
            AddStep("分析失败", ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void SaveTaskRecord(string input, AiWorkflowPlan workflow, IReadOnlyList<string> artifacts, bool succeeded, string? error)
    {
        var stepResults = workflow.Steps.Select(s => $"{s.Intent}/{s.ToolName}" + (s.Parameters.TryGetValue("outputName", out var on) && !string.IsNullOrWhiteSpace(on) ? " -> " + on : "") + ": " + s.Summary).ToArray();
        var record = new TaskRecord(
            Guid.NewGuid().ToString("N").Substring(0, 8),
            DateTime.Now,
            input,
            workflow.Summary,
            stepResults,
            succeeded,
            artifacts,
            error);
        _taskHistoryStore.Save(record);
        TaskHistory.Insert(0, record);
    }

    private sealed record WorkflowRepairResult(AiWorkflowPlan Workflow, bool CanExecute, string ClarificationMessage)
    {
        public static WorkflowRepairResult Success(AiWorkflowPlan workflow)
        {
            return new WorkflowRepairResult(workflow, true, string.Empty);
        }

        public static WorkflowRepairResult NeedsClarification(AiWorkflowPlan workflow, string clarificationMessage)
        {
            return new WorkflowRepairResult(workflow, false, clarificationMessage);
        }
    }
}

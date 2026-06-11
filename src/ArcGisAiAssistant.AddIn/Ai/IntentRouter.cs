using ArcGisAiAssistant.AddIn.ArcGis;
using ArcGisAiAssistant.AddIn.Models;

namespace ArcGisAiAssistant.AddIn.Ai;

internal sealed class IntentRouter
{
    private readonly MapCommandService _mapCommandService;
    private readonly GeoprocessingService _geoprocessingService;

    public IntentRouter(MapCommandService mapCommandService, GeoprocessingService geoprocessingService)
    {
        _mapCommandService = mapCommandService;
        _geoprocessingService = geoprocessingService;
    }

    public Task<ExecutionResult> ExecuteAsync(AiToolPlan plan, CancellationToken cancellationToken)
    {
        return plan.Intent switch
        {
            "cartography" => _mapCommandService.ExecuteAsync(plan, cancellationToken),
            "analysis" => _geoprocessingService.ExecuteAsync(plan, cancellationToken),
            _ => Task.FromResult(ExecutionResult.Failure($"Unknown intent: {plan.Intent}"))
        };
    }
}

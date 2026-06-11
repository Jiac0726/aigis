namespace ArcGisAiAssistant.AddIn.Ai;

internal static class AiToolPlanJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "intent", "toolName", "parameters", "requiresConfirmation", "summary" },
        properties = new
        {
            intent = new
            {
                type = "string",
                @enum = new[] { "cartography", "analysis" }
            },
            toolName = new
            {
                type = "string",
                @enum = new[]
                {
                    "zoom_to_layer",
                    "apply_symbology_from_layer",
                    "apply_unique_value_symbology",
                    "export_map_view",
                    "export_layout",
                    "buffer",
                    "clip",
                    "select_by_attribute",
                    "select_by_location",
                    "intersect",
                    "spatial_join",
                    "summary_statistics"
                }
            },
            parameters = new
            {
                type = "object",
                additionalProperties = new { type = "string" }
            },
            requiresConfirmation = new { type = "boolean" },
            summary = new { type = "string" }
        }
    };
}

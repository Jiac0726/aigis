using ArcGisAiAssistant.AddIn.Models;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace ArcGisAiAssistant.AddIn.ArcGis;

internal sealed class MapContextService
{
    public Task<AiRequestContext> CreateContextAsync(string userInput, bool maskExtent = false, bool maskFieldValues = false, IReadOnlyList<string>? targetLayerNames = null)
    {
        return CreateContextInternalAsync(userInput, maskExtent, maskFieldValues, targetLayerNames);
    }

    public Task<AiRequestContext> CreateContextAsync(string userInput)
    {
        return CreateContextInternalAsync(userInput, false, false);
    }

    private Task<AiRequestContext> CreateContextInternalAsync(string userInput, bool maskExtent, bool maskFieldValues, IReadOnlyList<string>? targetLayerNames = null)
    {
        return QueuedTask.Run(() =>
        {
            var activeMapView = MapView.Active;
            var map = activeMapView?.Map;
            var allLayers = map is not null ? FlattenLayers(map.Layers).ToArray() : Array.Empty<Layer>();
            var layers = targetLayerNames is { Count: > 0 }
                ? allLayers.Where(l => targetLayerNames.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).ToArray()
                : allLayers;
            var layerNames = layers.Select(layer => layer.Name).ToArray();
            var layerProfiles = layers.Select(l => CreateLayerProfile(l, maskFieldValues)).ToArray();
            var selectedLayerNames = TryGetSelectedLayerNames(activeMapView);

            return new AiRequestContext(
                userInput,
                map?.Name,
                layerNames,
                layerProfiles,
                selectedLayerNames,
                maskExtent ? "(masked)" : activeMapView?.Extent?.ToString());
        });
    }



    private static IEnumerable<Layer> FlattenLayers(IEnumerable<Layer> layers)
    {
        foreach (var layer in layers)
        {
            yield return layer;
            if (layer is ArcGIS.Desktop.Mapping.ILayerContainer container)
            {
                foreach (var child in FlattenLayers(container.Layers))
                    yield return child;
            }
        }
    }

    public static Task<IReadOnlyList<LayerSelection>> GetLayerSelectionsAsync()
    {
        return QueuedTask.Run(() =>
        {
            var map = MapView.Active?.Map;
            if (map is null) return Array.Empty<LayerSelection>();
            return (IReadOnlyList<LayerSelection>)FlattenLayers(map.Layers)
                .Select(l =>
                {
                    string? geom = null;
                    long? count = null;
                    if (l is BasicFeatureLayer bfl)
                    {
                        geom = bfl.ShapeType.ToString();
                        try { count = bfl.GetTable().GetCount(); } catch { }
                    }
                    return new LayerSelection
                    {
                        Name = l.Name,
                        GeometryType = geom ?? l.GetType().Name,
                        IsVisible = l.IsVisible,
                        RowCount = count
                    };
                }).ToArray();
        });
    }

    private static LayerProfile CreateLayerProfile(Layer layer, bool maskFieldValues = false)
    {
        var fields = Array.Empty<LayerFieldProfile>();
        long? rowCount = null;
        string? geometryType = null;

        if (layer is BasicFeatureLayer featureLayer)
        {
            geometryType = featureLayer.ShapeType.ToString();

            try
            {
                using var table = featureLayer.GetTable();
                fields = table.GetDefinition()
                    .GetFields()
                    .Take(40)
                    .Select(CreateFieldProfile)
                    .ToArray();
                rowCount = TryGetRowCount(table);
            }
            catch
            {
                fields = Array.Empty<LayerFieldProfile>();
            }
        }

        var displayName = maskFieldValues ? $"Layer_{layer.GetHashCode() & 0xFFF:X3}" : layer.Name;
        return new LayerProfile(
            displayName,
            layer.GetType().Name,
            geometryType,
            layer.IsVisible,
            TryGetBoolProperty(layer, "IsSelectable") ?? false,
            TryInvokeString(layer, "GetDefinitionQuery"),
            layer.URI,
            rowCount,
            fields);
    }

    private static LayerFieldProfile CreateFieldProfile(Field field)
    {
        return new LayerFieldProfile(
            field.Name,
            string.IsNullOrWhiteSpace(field.AliasName) ? field.Name : field.AliasName,
            field.FieldType.ToString(),
            field.IsNullable);
    }

    private static long? TryGetRowCount(Table table)
    {
        try
        {
            return table.GetCount();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> TryGetSelectedLayerNames(MapView? mapView)
    {
        if (mapView is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return mapView.GetSelectedLayers().Select(layer => layer.Name).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool? TryGetBoolProperty(object value, string propertyName)
    {
        try
        {
            return value.GetType().GetProperty(propertyName)?.GetValue(value) as bool?;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryInvokeString(object value, string methodName)
    {
        try
        {
            return value.GetType().GetMethod(methodName, Type.EmptyTypes)?.Invoke(value, null)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}

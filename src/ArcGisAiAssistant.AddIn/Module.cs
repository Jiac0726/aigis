using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGisAiAssistant.AddIn;

internal sealed class Module : ArcGIS.Desktop.Framework.Contracts.Module
{
    private static Module? _current;

    public static Module Current => _current ??= (Module)FrameworkApplication.FindModule("ArcGisAiAssistant_Module");

    protected override bool CanUnload()
    {
        return true;
    }
}

using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGisAiAssistant.AddIn;

internal sealed class ShowDockPaneButton : Button
{
    protected override void OnClick()
    {
        AiAssistantDockPaneViewModel.Show();
    }
}

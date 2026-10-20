using System.Windows;
namespace WindowSocks.Desktop;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A control panel has no GPU-intensive content; software rendering also works over remote desktops.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }
}

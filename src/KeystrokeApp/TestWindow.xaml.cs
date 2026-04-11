using System.Windows;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

/// <summary>
/// Debug window that displays live input listener events and prediction diagnostics.
/// Opened via "Show Debug Window" in the system tray menu.
/// </summary>
public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBarHelper.Apply(this);
    }

    public void Log(string message)
    {
        LogTextBox.AppendText($"{message}\n");
        LogTextBox.ScrollToEnd();
    }
}

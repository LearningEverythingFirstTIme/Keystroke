using System.Windows;

namespace KeystrokeApp.Views;

/// <summary>
/// Debug window that displays live keyboard hook events and prediction diagnostics.
/// Opened via "Show Debug Window" in the system tray menu.
/// </summary>
public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
    }

    public void Log(string message)
    {
        LogTextBox.AppendText($"{message}\n");
        LogTextBox.ScrollToEnd();
    }
}

using System.Windows;

namespace KeystrokeApp;

/// <summary>
/// Simple test window to display keyboard hook events.
/// This is just for Phase 2.1 testing - will be removed later.
/// </summary>
public partial class TestWindow : Window
{
    public TestWindow()
    {
        InitializeComponent();
    }

    public void Log(string message)
    {
        LogTextBox.AppendText($"{message}\n");
        LogTextBox.ScrollToEnd();
    }
}

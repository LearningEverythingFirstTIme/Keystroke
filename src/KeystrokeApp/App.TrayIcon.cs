using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace KeystrokeApp;

/// <summary>
/// System-tray icon management — creation, icon state, context menu, tooltip, and the
/// Ctrl+Shift+K toggle. Split from App.xaml.cs as a partial class for readability.
/// </summary>
public partial class App
{
    private Icon GetTrayIcon(bool enabled) => enabled
        ? (_iconEnabled  ??= CreateKeyboardIcon(true))
        : (_iconDisabled ??= CreateKeyboardIcon(false));

    private void CreateTrayIcon()
    {
        // Pre-build both icon states once so ToggleEnabled() never allocates GDI+ objects.
        _iconEnabled  = CreateKeyboardIcon(true);
        _iconDisabled = CreateKeyboardIcon(false);

        _trayIcon = new TaskbarIcon();
        _trayIcon.Icon        = GetTrayIcon(_isEnabled);
        _trayIcon.ToolTipText = BuildToolTip();

        var menu = new ContextMenu();

        _enabledMenuItem = new MenuItem { Header = "Enabled", IsCheckable = true, IsChecked = _isEnabled };
        _enabledMenuItem.Click += (s, e) =>
        {
            _isEnabled = _enabledMenuItem.IsChecked;
            _trayIcon!.Icon        = GetTrayIcon(_isEnabled);
            _trayIcon!.ToolTipText = BuildToolTip();
            Log(_isEnabled ? "Enabled" : "Disabled");
        };

        var suspendItem = new MenuItem { Header = "Suspend for 30 min" };
        suspendItem.Click += (s, e) =>
        {
            _isEnabled = false;
            _enabledMenuItem.IsChecked = false;
            _trayIcon!.Icon        = GetTrayIcon(false);
            _trayIcon!.ToolTipText = BuildToolTip() + "\nSuspended until " + DateTime.Now.AddMinutes(30).ToString("t");
            _suggestionPanel?.HideSuggestion();
            CancelPendingPrediction();
            _typingBuffer.Clear();
            Log("Suspended for 30 minutes");

            _suspendTimer?.Dispose();
            _suspendTimer = new Timer(_ =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _isEnabled = true;
                    _enabledMenuItem.IsChecked = true;
                    _trayIcon!.Icon        = GetTrayIcon(true);
                    _trayIcon!.ToolTipText = BuildToolTip();
                    Log("Resumed after suspension");
                });
            }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(-1));
        };

        _engineMenuItem = new MenuItem
        {
            Header    = $"Engine: {_config.PredictionEngine} ({GetCurrentModelName()})",
            IsEnabled = false
        };

        _sessionMenuItem = new MenuItem
        {
            Header    = $"Accepted: {_sessionAcceptCount} this session  ({_usage.DailyCount}/{UsageCounters.DailyLimit} today)",
            IsEnabled = false
        };

        var showDebugItem = new MenuItem { Header = "Show Debug Window" };
        showDebugItem.Click += (s, e) => ShowDebugWindow();

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => ShowSettingsWindow();

        var openConfigItem = new MenuItem { Header = "Open Config Folder" };
        openConfigItem.Click += (s, e) =>
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keystroke"
            );
            System.Diagnostics.Process.Start("explorer.exe", configDir);
        };

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) =>
        {
            Log("Exit requested from tray menu.");
            Shutdown();
        };

        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(suspendItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_engineMenuItem);
        menu.Items.Add(_sessionMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(showDebugItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowSettingsWindow();
    }

    private string BuildToolTip()
    {
        var status   = _isEnabled ? "Active" : "Paused";
        var engine   = _config.PredictionEngine;
        var model    = GetCurrentModelName();
        var accepted = _sessionAcceptCount;
        var today    = _usage.DailyCount;
        var limitStr = (_config.LimitEnabled && _usage.IsLimitReached())
            ? $"  ⚠ Limit reached"
            : $"  ({today}/{UsageCounters.DailyLimit} today)";
        return $"Keystroke - {status}\n{engine} ({model})\n{accepted} accepted this session{limitStr}";
    }

    private string GetCurrentModelName() => _config.PredictionEngine.ToLower() switch
    {
        "gemini" => _config.GeminiModel ?? "default",
        "gpt5"   => _config.Gpt5Model   ?? "default",
        "claude" => _config.ClaudeModel  ?? "default",
        "ollama" => _config.OllamaModel  ?? "qwen2.5:0.5b",
        _        => "default"
    };

    /// <summary>
    /// Toggles the enabled state (Ctrl+Shift+K). Updates the tray icon and menu,
    /// and cancels any in-flight prediction when disabling.
    /// </summary>
    private void ToggleEnabled()
    {
        _isEnabled = !_isEnabled;

        Dispatcher.BeginInvoke(() =>
        {
            if (_enabledMenuItem != null)
                _enabledMenuItem.IsChecked = _isEnabled;
            if (_trayIcon != null)
            {
                _trayIcon.Icon        = GetTrayIcon(_isEnabled);
                _trayIcon.ToolTipText = BuildToolTip();
            }

            if (!_isEnabled)
            {
                _suggestionPanel?.HideSuggestion();
                CancelPendingPrediction();
                _typingBuffer.Clear();
            }
        });

        Log($"Toggled via Ctrl+Shift+K: {(_isEnabled ? "Enabled" : "Disabled")}");
    }

    private void UpdateTraySessionInfo()
    {
        if (_trayIcon?.ContextMenu?.Items == null) return;
        foreach (var item in _trayIcon.ContextMenu.Items)
        {
            if (item is MenuItem mi && mi.Header is string s && s.StartsWith("Accepted:"))
            {
                var limitStr = (_config.LimitEnabled && _usage.IsLimitReached())
                    ? $"  ⚠ Daily limit reached — go Pro"
                    : $"  ({_usage.DailyCount}/{UsageCounters.DailyLimit} today)";
                mi.Header = $"Accepted: {_sessionAcceptCount} this session{limitStr}";
                break;
            }
        }
        _trayIcon.ToolTipText = BuildToolTip();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private Icon CreateKeyboardIcon(bool enabled = true)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(System.Drawing.Color.FromArgb(30, 30, 46));

        using var bodyBrush   = new SolidBrush(System.Drawing.Color.FromArgb(200, 200, 220));
        using var smallBrush  = new SolidBrush(System.Drawing.Color.FromArgb(30, 30, 46));
        using var statusGreen = new SolidBrush(System.Drawing.Color.FromArgb(47, 186, 78));
        using var statusGray  = new SolidBrush(System.Drawing.Color.FromArgb(100, 100, 120));

        g.FillRectangle(bodyBrush, 2, 8, 28, 16);

        for (int i = 0; i < 5; i++)
        {
            g.FillRectangle(smallBrush, 4 + i * 5, 10, 4, 4);
        }
        g.FillRectangle(smallBrush, 8, 18, 16, 3);

        g.FillEllipse(enabled ? statusGreen : statusGray, 22, 2, 8, 8);

        var hIcon   = bitmap.GetHicon();
        var tempIcon = Icon.FromHandle(hIcon);
        var icon    = (Icon)tempIcon.Clone();
        tempIcon.Dispose();
        DestroyIcon(hIcon);
        return icon;
    }
}

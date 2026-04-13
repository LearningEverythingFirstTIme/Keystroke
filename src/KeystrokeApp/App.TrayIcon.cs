using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// System-tray icon management — creation, icon state, context menu, tooltip, and the
/// Ctrl+Shift+K toggle. Split from App.xaml.cs as a partial class for readability.
/// </summary>
public partial class App
{
    private sealed record CurrentAppStatus(
        string ProcessName,
        string WindowTitle,
        bool IsEnabled,
        string Reason,
        string Label);

    private sealed record ProfileStatusSummary(
        bool PersonalizedAiEnabled,
        int AcceptedSignals,
        int ContextCount)
    {
        public bool HasSignals => AcceptedSignals > 0;
    }

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
            Header    = BuildSessionMenuHeader(),
            IsEnabled = false
        };

        _profileMenuItem = new MenuItem
        {
            Header = BuildProfileMenuHeader(),
            IsEnabled = false
        };

        _setupMenuItem = new MenuItem { Header = "Finish Gemini setup" };
        _setupMenuItem.Click += (s, e) =>
        {
            if (RunOnboardingFlow(isStartup: false))
                EnsureRuntimeStateFromConfig();
        };

        _currentAppMenuItem = new MenuItem { Header = "Current app: loading...", IsEnabled = false };
        _currentAppStatusMenuItem = new MenuItem { Header = "Status: checking...", IsEnabled = false };
        _currentAppBlockMenuItem = new MenuItem { Header = "Pause in this app" };
        _currentAppBlockMenuItem.Click += (s, e) => BlockCurrentAppFromTray();
        _currentAppAllowMenuItem = new MenuItem { Header = "Allow only this app" };
        _currentAppAllowMenuItem.Click += (s, e) => AllowCurrentAppFromTray();

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
        menu.Items.Add(_profileMenuItem);
        menu.Items.Add(_setupMenuItem);
        menu.Items.Add(_currentAppMenuItem);
        menu.Items.Add(_currentAppStatusMenuItem);
        menu.Items.Add(_currentAppBlockMenuItem);
        menu.Items.Add(_currentAppAllowMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        // Apply dark-themed styles from the design system
        var themeDict = new ResourceDictionary
        {
            Source = new Uri("/KeystrokeApp;component/Resources/CleanPro.xaml", UriKind.Relative)
        };
        if (themeDict["TrayContextMenuStyle"] is Style menuStyle)
            menu.Style = menuStyle;
        var itemStyle = themeDict["TrayMenuItemStyle"] as Style;
        var separatorStyle = themeDict["TrayMenuSeparatorStyle"] as Style;
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi && itemStyle != null)
                mi.Style = itemStyle;
            else if (item is Separator sep && separatorStyle != null)
                sep.Style = separatorStyle;
        }

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowSettingsWindow();
        menu.Opened += (_, _) => UpdateTrayCurrentAppActions();
    }

    private string BuildToolTip()
    {
        if (_isSetupIncomplete)
        {
            return $"Keystroke - Setup required\n{_setupIncompleteReason}\nRecommended: gemini ({AppConfig.DefaultGeminiModel})";
        }

        var status   = _isEnabled ? "Active" : "Paused";
        var engine   = _config.PredictionEngine;
        var model    = GetCurrentModelName();
        var currentApp = GetCurrentAppStatus();
        return $"Keystroke - {status}\n{engine} ({model})\n{BuildUsageTooltipSummary()}\nAI profile: {BuildProfileTooltipSummary()}\n{currentApp.Label}: {currentApp.Reason}\nLast accept: {_lastAcceptanceStatus}";
    }

    private string GetCurrentModelName() => _config.PredictionEngine.ToLower() switch
    {
        "gemini" => _config.GeminiModel ?? "default",
        "gpt5"   => _config.Gpt5Model   ?? "default",
        "claude" => _config.ClaudeModel  ?? "default",
        "ollama" => _config.OllamaModel  ?? AppConfig.DefaultOllamaModel,
        _        => "default"
    };

    /// <summary>
    /// Toggles the enabled state (Ctrl+Shift+K). Updates the tray icon and menu,
    /// and cancels any in-flight prediction when disabling.
    /// </summary>
    private void ToggleEnabled()
    {
        // _isEnabled is volatile. The read-modify-write is not atomic, but the only
        // other writers are UI-thread menu clicks and the suspend timer callback
        // (which dispatches to UI). A lost toggle is extremely unlikely and harmless
        // (user just presses the hotkey again). Avoiding a lock here keeps the
        // input-hook callback fast.
        var newState = !_isEnabled;
        _isEnabled = newState;

        Dispatcher.BeginInvoke(() =>
        {
            if (_enabledMenuItem != null)
                _enabledMenuItem.IsChecked = newState;
            if (_trayIcon != null)
            {
                _trayIcon.Icon        = GetTrayIcon(newState);
                _trayIcon.ToolTipText = BuildToolTip();
            }

            if (!newState)
            {
                _suggestionPanel?.HideSuggestion();
                CancelPendingPrediction();
                _typingBuffer.Clear();
            }
        });

        Log($"Toggled via Ctrl+Shift+K: {(newState ? "Enabled" : "Disabled")}");
    }

    private void UpdateTraySessionInfo()
    {
        if (_trayIcon?.ContextMenu?.Items == null) return;
        foreach (var item in _trayIcon.ContextMenu.Items)
        {
            if (item is MenuItem mi && mi.Header is string s && s.StartsWith("Accepted:"))
            {
                mi.Header = BuildSessionMenuHeader();
                break;
            }
            if (item is MenuItem cappedMi && cappedMi.Header is string capped && capped.StartsWith("⚠ Daily limit reached"))
            {
                cappedMi.Header = BuildSessionMenuHeader();
                break;
            }
        }

        if (_profileMenuItem != null)
            _profileMenuItem.Header = BuildProfileMenuHeader();

        _trayIcon.ToolTipText = BuildToolTip();
    }

    private void UpdateTrayCurrentAppActions()
    {
        var status = GetCurrentAppStatus();
        if (_currentAppMenuItem != null)
            _currentAppMenuItem.Header = $"Current app: {status.Label}";
        if (_currentAppStatusMenuItem != null)
            _currentAppStatusMenuItem.Header = $"Status: {status.Reason}";

        if (_setupMenuItem != null)
        {
            _setupMenuItem.Visibility = _isSetupIncomplete ? Visibility.Visible : Visibility.Collapsed;
            _setupMenuItem.IsEnabled = true;
        }

        var hasProcess = !string.IsNullOrWhiteSpace(status.ProcessName);
        if (_currentAppBlockMenuItem != null)
        {
            _currentAppBlockMenuItem.IsEnabled = hasProcess && !_isSetupIncomplete;
            _currentAppBlockMenuItem.Header = _isSetupIncomplete
                ? "Finish setup first"
                : status.IsEnabled ? "Pause in this app" : "Blocked in this app";
        }

        if (_currentAppAllowMenuItem != null)
        {
            _currentAppAllowMenuItem.IsEnabled = hasProcess && !_isSetupIncomplete;
            _currentAppAllowMenuItem.Header = _isSetupIncomplete
                ? "Finish setup first"
                : PerAppSettings.NormalizeMode(_config.AppFilteringMode) == PerAppSettings.AllowListedOnly
                ? "Add this app to allow list"
                : "Allow only this app";
        }

        if (_trayIcon != null)
            _trayIcon.ToolTipText = BuildToolTip();

        if (_sessionMenuItem != null)
            _sessionMenuItem.Header = BuildSessionMenuHeader();
    }

    private CurrentAppStatus GetCurrentAppStatus()
    {
        var (processName, windowTitle) = GetLastExternalWindowOrCurrent();
        var normalized = PerAppSettings.NormalizeProcessName(processName);
        if (_isSetupIncomplete)
        {
            return new CurrentAppStatus(
                processName,
                windowTitle,
                false,
                _setupIncompleteReason,
                string.IsNullOrWhiteSpace(normalized) ? "Setup incomplete" : normalized);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new CurrentAppStatus("", "", true, "No active app detected", "No active app");
        }

        var enabled = IsProcessEnabled(processName);
        var reason = PerAppSettings.GetAvailabilityReason(_config, processName);
        var label = string.IsNullOrWhiteSpace(windowTitle)
            ? normalized
            : $"{normalized} - {windowTitle}";
        return new CurrentAppStatus(processName, windowTitle, enabled, reason, label);
    }

    private void BlockCurrentAppFromTray()
    {
        var status = GetCurrentAppStatus();
        var normalized = PerAppSettings.NormalizeProcessName(status.ProcessName);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _config.BlockedProcesses = PerAppSettings.NormalizeProcessList(_config.BlockedProcesses.Append(normalized));
        _config.AllowedProcesses = PerAppSettings.NormalizeProcessList(
            _config.AllowedProcesses.Where(process => !string.Equals(process, normalized, StringComparison.OrdinalIgnoreCase)));
        _config.Save();
        SyncSettingsFromConfig();
        SuppressForFilteredApp(status.ProcessName);
        _lastAcceptanceStatus = $"Paused in {normalized}";
        UpdateTrayCurrentAppActions();
    }

    private void AllowCurrentAppFromTray()
    {
        var status = GetCurrentAppStatus();
        var normalized = PerAppSettings.NormalizeProcessName(status.ProcessName);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (PerAppSettings.NormalizeMode(_config.AppFilteringMode) == PerAppSettings.AllowListedOnly)
        {
            _config.AllowedProcesses = PerAppSettings.NormalizeProcessList(_config.AllowedProcesses.Append(normalized));
        }
        else
        {
            _config.AppFilteringMode = PerAppSettings.AllowListedOnly;
            _config.AllowedProcesses = [normalized];
        }

        _config.BlockedProcesses = PerAppSettings.NormalizeProcessList(
            _config.BlockedProcesses.Where(process => !string.Equals(process, normalized, StringComparison.OrdinalIgnoreCase)));
        _config.Save();
        SyncSettingsFromConfig();
        _lastAcceptanceStatus = PerAppSettings.NormalizeMode(_config.AppFilteringMode) == PerAppSettings.AllowListedOnly
            ? $"Allow list updated for {normalized}"
            : $"Only {normalized} allowed";
        UpdateTrayCurrentAppActions();
    }

    private void ReportAcceptanceStatus(TextInjectionResult result, string source)
    {
        _lastAcceptanceStatus = result.Outcome switch
        {
            TextInjectionOutcome.Injected => "Delivered",
            TextInjectionOutcome.ClipboardRestoreFailed => "Delivered, but clipboard restore failed",
            TextInjectionOutcome.ClipboardChangedExternally => "Delivered, clipboard changed before restore",
            TextInjectionOutcome.FallbackInjected => "Delivered via SendInput fallback",
            TextInjectionOutcome.Cancelled => "Cancelled before delivery",
            _ => $"Failed: {result.FailureReason ?? "unknown error"}"
        };

        if (_trayIcon != null)
            _trayIcon.ToolTipText = BuildToolTip();

        if (_trayIcon == null)
            return;

        if (result.Outcome is TextInjectionOutcome.Injected)
            return;

        var title = result.Outcome is TextInjectionOutcome.Failed or TextInjectionOutcome.Cancelled
            ? "Keystroke accept failed"
            : "Keystroke accept warning";
        var message = result.Outcome switch
        {
            TextInjectionOutcome.ClipboardRestoreFailed => "Accepted text reached the app, but Keystroke could not restore your previous clipboard contents.",
            TextInjectionOutcome.ClipboardChangedExternally => "Accepted text reached the app. Keystroke left the clipboard alone because something else changed it first.",
            TextInjectionOutcome.FallbackInjected => "Accepted text reached the app through the SendInput fallback. Local buffer tracking may lag until you type again.",
            TextInjectionOutcome.Cancelled => $"The {source.Replace('_', ' ')} action was cancelled before text was delivered.",
            _ => $"Keystroke could not deliver accepted text: {result.FailureReason ?? "unknown error"}"
        };

        _trayIcon.ShowBalloonTip(title, message, BalloonIcon.Info);
        LogToDebug($"{title}: {message}");
    }

    private string BuildProfileMenuHeader()
    {
        var profile = GetProfileStatusSummary();
        return profile.PersonalizedAiEnabled
            ? profile.HasSignals
                ? $"AI Profile: active ({profile.AcceptedSignals} signals)"
                : "AI Profile: active, waiting for first signal"
            : profile.HasSignals
                ? $"AI Profile: building quietly ({profile.AcceptedSignals} signals)"
                : "AI Profile: off, no signals yet";
    }

    private string BuildProfileTooltipSummary()
    {
        var profile = GetProfileStatusSummary();
        var contextText = profile.ContextCount > 0
            ? $" across {profile.ContextCount} context{(profile.ContextCount == 1 ? "" : "s")}"
            : "";

        return profile.PersonalizedAiEnabled
            ? profile.HasSignals
                ? $"active with {profile.AcceptedSignals} signal{(profile.AcceptedSignals == 1 ? "" : "s")}{contextText}"
                : "active, waiting for first accepted suggestion"
            : profile.HasSignals
                ? $"building from {profile.AcceptedSignals} signal{(profile.AcceptedSignals == 1 ? "" : "s")}{contextText}"
                : "off until you accept a few suggestions";
    }

    private ProfileStatusSummary GetProfileStatusSummary()
    {
        try
        {
            var stats = _learningService.GetStats();
            return new ProfileStatusSummary(
                _isProTier,
                stats.TotalAccepted,
                stats.ContextSummaries.Count);
        }
        catch
        {
            return new ProfileStatusSummary(_isProTier, 0, 0);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private Icon CreateKeyboardIcon(bool enabled = true)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);

        // Keyboard body — rounded rectangle
        var bodyColor = System.Drawing.Color.FromArgb(200, 210, 225);
        using var bodyBrush = new SolidBrush(bodyColor);
        using var bodyPath = CreateRoundedRect(2, 9, 27, 16, 3);
        g.FillPath(bodyBrush, bodyPath);

        // Key rows — dark insets
        var keyColor = System.Drawing.Color.FromArgb(20, 26, 42);
        using var keyBrush = new SolidBrush(keyColor);

        // Top row: 5 keys
        for (int i = 0; i < 5; i++)
            g.FillRectangle(keyBrush, 5 + i * 5, 12, 3, 3);

        // Bottom row: spacebar
        using var spacePath = CreateRoundedRect(9, 18, 14, 3, 1);
        g.FillPath(keyBrush, spacePath);

        // Status dot — accent blue when enabled, muted when disabled
        var dotColor = enabled
            ? System.Drawing.Color.FromArgb(94, 166, 255)   // Accent #5EA6FF
            : System.Drawing.Color.FromArgb(140, 160, 187); // TextMuted #8CA0BB
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, 22, 2, 8, 8);

        // Subtle ring around dot
        using var ringPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, dotColor), 1f);
        g.DrawEllipse(ringPen, 21.5f, 1.5f, 9f, 9f);

        var hIcon   = bitmap.GetHicon();
        var tempIcon = Icon.FromHandle(hIcon);
        var icon    = (Icon)tempIcon.Clone();
        tempIcon.Dispose();
        DestroyIcon(hIcon);
        return icon;
    }

    private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

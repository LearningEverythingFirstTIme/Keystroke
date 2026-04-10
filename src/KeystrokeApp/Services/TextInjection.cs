using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WindowsInput;
using WindowsInput.Native;

namespace KeystrokeApp.Services;

public enum TextInjectionMethod
{
    ClipboardPaste,
    SendInputFallback
}

public enum TextInjectionOutcome
{
    Injected,
    ClipboardRestoreFailed,
    ClipboardChangedExternally,
    FallbackInjected,
    Cancelled,
    Failed
}

public sealed record TextInjectionResult(
    TextInjectionOutcome Outcome,
    TextInjectionMethod Method,
    bool ClipboardCaptured,
    bool ClipboardRestoreAttempted,
    bool ClipboardRestoreSucceeded,
    bool ClipboardChangedExternally,
    string? FailureReason = null
)
{
    public bool Success => DeliveredToTarget;
    public bool DeliveredToTarget => Outcome is
        TextInjectionOutcome.Injected or
        TextInjectionOutcome.ClipboardRestoreFailed or
        TextInjectionOutcome.ClipboardChangedExternally or
        TextInjectionOutcome.FallbackInjected;

    public static TextInjectionResult Cancelled(TextInjectionMethod method = TextInjectionMethod.ClipboardPaste) =>
        new(TextInjectionOutcome.Cancelled, method, false, false, false, false, "Injection cancelled");

    public static TextInjectionResult Failed(TextInjectionMethod method, string? reason) =>
        new(TextInjectionOutcome.Failed, method, false, false, false, false, reason);
}

public interface ITextInjector
{
    Task<TextInjectionResult> InjectAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class ClipboardTextInjector : ITextInjector
{
    private readonly InputSimulator _inputSimulator;
    private readonly ReliabilityTraceService _trace;
    private readonly SemaphoreSlim _injectGate = new(1, 1);

    private const int PasteDelayMs = 30;
    private const int RestoreDelayMs = 100;
    private const int RestoreAttempts = 3;
    private const int SendInputCharDelayMs = 5;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public ClipboardTextInjector(InputSimulator inputSimulator, ReliabilityTraceService trace)
    {
        _inputSimulator = inputSimulator;
        _trace = trace;
    }

    public async Task<TextInjectionResult> InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TextInjectionResult(TextInjectionOutcome.Injected, TextInjectionMethod.ClipboardPaste, false, false, true, false);
        }

        try
        {
            await _injectGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return TextInjectionResult.Cancelled();
        }

        try
        {
            return await InjectCoreAsync(text, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _trace.Trace("injection", "cancelled", "Injection cancelled before completion.");
            return TextInjectionResult.Cancelled();
        }
        catch (Exception ex)
        {
            _trace.Trace("injection", "failed", "Injection crashed before completion.", new Dictionary<string, string>
            {
                ["error"] = ex.Message
            });
            return TextInjectionResult.Failed(TextInjectionMethod.ClipboardPaste, ex.Message);
        }
        finally
        {
            _injectGate.Release();
        }
    }

    private async Task<TextInjectionResult> InjectCoreAsync(string text, CancellationToken cancellationToken)
    {
        _trace.Trace("injection", "started", "Beginning clipboard injection.", new Dictionary<string, string>
        {
            ["length"] = text.Length.ToString()
        });

        IDataObject? originalClipboard = null;
        DataObject? savedClipboard = null;
        uint clipboardSequenceAfterSet = 0;
        bool clipboardCaptured = false;

        try
        {
            await RunOnUiAsync(() =>
            {
                originalClipboard = Clipboard.GetDataObject();
                savedClipboard = CloneClipboardData(originalClipboard);
                clipboardCaptured = savedClipboard != null || originalClipboard != null;
            });

            _trace.Trace("injection", "clipboard_captured", "Captured current clipboard state.", new Dictionary<string, string>
            {
                ["captured"] = clipboardCaptured.ToString()
            });

            await RunOnUiAsync(() =>
            {
                Clipboard.SetText(text);
                clipboardSequenceAfterSet = GetClipboardSequenceNumber();
            });

            _trace.Trace("injection", "clipboard_set", "Placed accepted text on the clipboard.", new Dictionary<string, string>
            {
                ["sequence"] = clipboardSequenceAfterSet.ToString()
            });

            await Task.Delay(PasteDelayMs, cancellationToken);
            _inputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
            _trace.Trace("injection", "paste_sent", "Sent Ctrl+V for accepted text.");

            await Task.Delay(RestoreDelayMs, cancellationToken);
            var restoreResult = await RestoreClipboardAsync(savedClipboard, clipboardSequenceAfterSet);

            var outcome = restoreResult.ClipboardChangedExternally
                ? TextInjectionOutcome.ClipboardChangedExternally
                : restoreResult.RestoreAttempted && !restoreResult.RestoreSucceeded
                    ? TextInjectionOutcome.ClipboardRestoreFailed
                    : TextInjectionOutcome.Injected;

            return new TextInjectionResult(
                outcome,
                TextInjectionMethod.ClipboardPaste,
                clipboardCaptured,
                restoreResult.RestoreAttempted,
                restoreResult.RestoreSucceeded || !restoreResult.RestoreAttempted,
                restoreResult.ClipboardChangedExternally,
                restoreResult.RestoreSucceeded || !restoreResult.RestoreAttempted ? null : "Clipboard restore failed");
        }
        catch (Exception ex)
        {
            _trace.Trace("injection", "clipboard_failed", "Clipboard injection failed; falling back to SendInput.", new Dictionary<string, string>
            {
                ["error"] = ex.Message
            });

            // SendInput produces LLKHF_INJECTED keystrokes which are (correctly)
            // filtered by InputListenerService. The characters still reach the
            // target app via CallNextHookEx, but Keystroke's typing buffer never
            // sees them. Inject character-by-character with a small delay to
            // avoid overwhelming slow target apps (e.g. Electron-based editors).
            await Task.Delay(PasteDelayMs, cancellationToken);
            foreach (var ch in text)
            {
                _inputSimulator.Keyboard.TextEntry(ch);
                await Task.Delay(SendInputCharDelayMs, cancellationToken);
            }

            _trace.Trace("injection", "sendinput_fallback",
                "Injected accepted text with SendInput fallback. " +
                "Note: these keystrokes are filtered by the input hook (LLKHF_INJECTED) " +
                "and bypass typing buffer tracking.",
                new Dictionary<string, string>
                {
                    ["length"] = text.Length.ToString()
                });

            return new TextInjectionResult(TextInjectionOutcome.FallbackInjected, TextInjectionMethod.SendInputFallback, clipboardCaptured, false, false, false, ex.Message);
        }
    }

    private async Task<(bool RestoreAttempted, bool RestoreSucceeded, bool ClipboardChangedExternally)> RestoreClipboardAsync(
        DataObject? savedClipboard,
        uint clipboardSequenceAfterSet)
    {
        var currentSequence = await RunOnUiAsync(GetClipboardSequenceNumber);
        var clipboardChangedExternally = currentSequence != clipboardSequenceAfterSet;

        if (clipboardChangedExternally)
        {
            _trace.Trace("injection", "clipboard_changed_externally", "Skipped clipboard restore because the clipboard changed after paste.", new Dictionary<string, string>
            {
                ["expectedSequence"] = clipboardSequenceAfterSet.ToString(),
                ["actualSequence"] = currentSequence.ToString()
            });
            return (false, false, true);
        }

        for (var attempt = 1; attempt <= RestoreAttempts; attempt++)
        {
            try
            {
                await RunOnUiAsync(() =>
                {
                    if (savedClipboard != null)
                        Clipboard.SetDataObject(savedClipboard, true);
                    else
                        Clipboard.Clear();
                });

                _trace.Trace("injection", "clipboard_restored", "Restored original clipboard content.", new Dictionary<string, string>
                {
                    ["attempt"] = attempt.ToString()
                });
                return (true, true, false);
            }
            catch (Exception ex)
            {
                _trace.Trace("injection", "clipboard_restore_retry", "Clipboard restore attempt failed.", new Dictionary<string, string>
                {
                    ["attempt"] = attempt.ToString(),
                    ["error"] = ex.Message
                });
                await Task.Delay(40 * attempt);
            }
        }

        _trace.Trace("injection", "clipboard_restore_failed", "All clipboard restore attempts failed.");
        return (true, false, false);
    }

    private static async Task RunOnUiAsync(Action action)
    {
        await Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Send);
    }

    private static async Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        return await Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Send);
    }

    private static DataObject? CloneClipboardData(IDataObject? source)
    {
        if (source == null) return null;

        try
        {
            var clone = new DataObject();
            var hasAnything = false;

            foreach (var format in source.GetFormats())
            {
                try
                {
                    var data = source.GetData(format);
                    if (data == null) continue;

                    clone.SetData(format, data);
                    hasAnything = true;
                }
                catch
                {
                }
            }

            return hasAnything ? clone : null;
        }
        catch
        {
            return null;
        }
    }
}

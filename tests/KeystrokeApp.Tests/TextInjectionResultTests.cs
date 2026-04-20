using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class TextInjectionResultTests
{
    [Fact]
    public void ClipboardRestoreSkipped_IsStillMarkedDelivered()
    {
        var result = new TextInjectionResult(
            TextInjectionOutcome.ClipboardRestoreSkipped,
            TextInjectionMethod.ClipboardPaste,
            ClipboardCaptured: true,
            ClipboardRestoreAttempted: false,
            ClipboardRestoreSucceeded: false,
            ClipboardChangedExternally: false);

        Assert.True(result.DeliveredToTarget);
        Assert.True(result.Success);
    }
}

using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class SuggestionLifecycleControllerTests
{
    [Fact]
    public void ShowSuggestion_ReplacesPreviousSuggestionAndResetsCycleDepth()
    {
        var controller = new SuggestionLifecycleController();
        var context = BuildContext("hello");

        controller.BeginPrediction(1, "hello");
        var first = controller.ShowSuggestion(1, context, "hello", " world");
        controller.MarkAlternativesReady(1);
        controller.IncrementCycleDepth();

        var second = controller.ShowSuggestion(2, context, "hello there", " friend");

        Assert.Equal(first.State.SuggestionId, second.ClearedSuggestionId);
        Assert.True(second.State.IsVisible);
        Assert.False(second.State.AlternativesValid);
        Assert.Equal(0, second.State.CycleDepth);
        Assert.Equal("hello there", second.State.Prefix);
        Assert.Equal(" friend", second.State.Completion);
    }

    [Fact]
    public void MarkAlternativesReady_IgnoresStaleRequests()
    {
        var controller = new SuggestionLifecycleController();
        controller.BeginPrediction(5, "draft");
        controller.ShowSuggestion(5, BuildContext("draft"), "draft", " ready");

        var stale = controller.MarkAlternativesReady(4);
        var current = controller.MarkAlternativesReady(5);

        Assert.False(stale);
        Assert.True(current);
        Assert.True(controller.Snapshot().AlternativesValid);
    }

    [Fact]
    public void ClearSuggestion_ClearsVisibleState()
    {
        var controller = new SuggestionLifecycleController();
        controller.BeginPrediction(7, "todo");
        var shown = controller.ShowSuggestion(7, BuildContext("todo"), "todo", " item");

        var clearedSuggestionId = controller.ClearSuggestion(resetRequest: true);
        var state = controller.Snapshot();

        Assert.Equal(shown.State.SuggestionId, clearedSuggestionId);
        Assert.False(state.IsVisible);
        Assert.Equal(0, state.ActiveRequestId);
        Assert.Equal("", state.SuggestionId);
    }

    private static ContextSnapshot BuildContext(string typedText) => new()
    {
        TypedText = typedText,
        ProcessName = "code",
        WindowTitle = "Editor",
        SafeContextLabel = "code (Code)",
        Category = AppCategory.Category.Code.ToString()
    };
}

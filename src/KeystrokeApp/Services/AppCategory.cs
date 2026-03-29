namespace KeystrokeApp.Services;

/// <summary>
/// Classifies applications into categories and provides tone/context hints
/// so the prediction engine can adapt its style.
/// </summary>
public static class AppCategory
{
    public enum Category
    {
        Chat,
        Email,
        Code,
        Document,
        Browser,
        Terminal,
        Unknown
    }

    /// <summary>
    /// Classify a process by name into an app category.
    /// </summary>
    public static Category Classify(string processName)
    {
        var name = processName.ToLowerInvariant();

        // Chat / messaging
        if (name is "claude" or "chatgpt" or "slack" or "discord" or "teams"
            or "telegram" or "whatsapp" or "signal" or "messenger"
            or "skype" or "zoom" or "webex")
            return Category.Chat;

        // Email
        if (name is "outlook" or "thunderbird" or "mailspring"
            or "OUTLOOK" or "olk")
            return Category.Email;

        // Code editors / IDEs
        if (name is "code" or "devenv" or "rider" or "idea" or "idea64"
            or "webstorm" or "pycharm" or "goland" or "clion"
            or "sublime_text" or "notepad++" or "atom"
            or "cursor" or "windsurf")
            return Category.Code;

        // Document / writing
        if (name is "winword" or "wordpad" or "notepad" or "obsidian"
            or "notion" or "onenote" or "libreoffice"
            or "googledocs" or "typora" or "marktext")
            return Category.Document;

        // Terminal
        if (name is "windowsterminal" or "cmd" or "powershell" or "pwsh"
            or "conhost" or "wezterm" or "alacritty" or "hyper"
            or "wt")
            return Category.Terminal;

        // Browsers — could be anything, but check window title for hints
        if (name is "chrome" or "msedge" or "firefox" or "opera" or "brave"
            or "vivaldi" or "arc" or "comet" or "chromium" or "waterfox"
            or "librewolf" or "floorp" or "zen")
            return Category.Browser;

        return Category.Unknown;
    }

    /// <summary>
    /// Refine browser category using window title hints.
    /// </summary>
    public static Category RefineBrowserCategory(string windowTitle)
    {
        var title = windowTitle.ToLowerInvariant();

        if (title.Contains("gmail") || title.Contains("outlook") || title.Contains("mail")
            || title.Contains("proton"))
            return Category.Email;

        if (title.Contains("slack") || title.Contains("discord") || title.Contains("teams")
            || title.Contains("messenger") || title.Contains("chat")
            || title.Contains("whatsapp") || title.Contains("telegram"))
            return Category.Chat;

        if (title.Contains("github") || title.Contains("gitlab") || title.Contains("codepen")
            || title.Contains("codesandbox") || title.Contains("stackblitz")
            || title.Contains("replit"))
            return Category.Code;

        if (title.Contains("docs.google") || title.Contains("notion")
            || title.Contains("confluence") || title.Contains("wiki"))
            return Category.Document;

        return Category.Browser;
    }

    /// <summary>
    /// Get a tone/behavior hint for the prediction engine based on category.
    /// </summary>
    public static string GetToneHint(Category category) => category switch
    {
        Category.Chat =>
            "This is a chat/messaging app. The user is having a conversation. " +
            "Predict casual, conversational text. Use contractions, informal language. " +
            "Keep predictions short — chat messages are typically brief.",

        Category.Email =>
            "This is an email client. Predict professional, clear text. " +
            "Use complete sentences and appropriate greetings/closings. " +
            "Match formality to the thread — reply-all to executives is different from a quick note to a teammate.",

        Category.Code =>
            "This is a code editor or IDE. If the user is writing code, predict syntactically valid code. " +
            "If they're writing a comment or commit message, predict natural language. " +
            "Be precise with variable names, function signatures, and language idioms.",

        Category.Document =>
            "This is a document or note-taking app. Predict well-structured prose. " +
            "Continue paragraphs naturally. Match the document's voice and formality level.",

        Category.Terminal =>
            "This is a terminal/command line. Predict shell commands, flags, and file paths. " +
            "Be precise with syntax. Predict based on common command patterns and the visible command history.",

        Category.Browser =>
            "The user is typing in a web browser. Infer the context from the page title and visible text. " +
            "Adapt tone to whatever the user appears to be doing — searching, commenting, filling a form, etc.",

        _ =>
            "Adapt your tone to match whatever the user appears to be writing."
    };

    /// <summary>
    /// Get the effective category, refining browsers by window title.
    /// </summary>
    public static Category GetEffectiveCategory(string processName, string windowTitle)
    {
        var category = Classify(processName);
        if (category == Category.Browser)
            category = RefineBrowserCategory(windowTitle);
        return category;
    }
}

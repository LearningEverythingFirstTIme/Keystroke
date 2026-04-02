using System.IO;
using System.Text;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Extracts a deterministic personal vocabulary fingerprint from the user's accepted
/// completions. Unlike StyleProfileService, this requires no LLM call — all analysis
/// is computed directly from the text, making it fast, reproducible, and free to run.
///
/// What gets extracted per category:
///   - High-frequency personal vocabulary (words above common-English baseline)
///   - Preferred opening and closing phrases (n-gram frequency analysis)
///   - Structural preferences: avg sentence length, contractions, Oxford comma,
///     em-dash usage, exclamation habit, ellipsis, and formality level
///
/// The result is injected into prediction prompts as a compact structured block,
/// giving the AI model specific, actionable signals it can reliably follow.
/// </summary>
public class VocabularyProfileService
{
    // ── Thresholds ────────────────────────────────────────────────────────────
    private const int MinEntriesPerCategory = 15;  // minimum accepted entries to analyse a category
    private const int MaxSamplesPerCategory = 60;   // cap to keep analysis fast
    private const int MinPhraseFrequency    = 3;    // a phrase must appear this many times to be kept
    private const int MinWordFrequency      = 2;    // personal word must appear at least twice
    private const int MaxTopWords           = 20;   // words shown per category
    private const int MaxPhrases            = 5;    // opening/closing phrases shown per category

    // ── File paths ────────────────────────────────────────────────────────────
    private readonly string _profilePath;
    private readonly string _trackingPath;
    private readonly string _logPath;

    // ── State ─────────────────────────────────────────────────────────────────
    private VocabularyProfile? _profile;
    private int  _acceptCount;
    private int  _profileInterval;
    private bool _isGenerating;
    private readonly object _lock = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public VocabularyProfileService()
    {
        var appData     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keystroke");
        _profilePath    = Path.Combine(appData, "vocabulary-profile.json");
        _trackingPath   = Path.Combine(appData, "tracking.jsonl");
        _logPath        = Path.Combine(appData, "vocabulary-profile.log");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start(int interval)
    {
        _profileInterval = interval;
        LoadProfile();
        Log($"Started. Interval={interval}, HasProfile={_profile != null}");
    }

    public void UpdateInterval(int interval) => _profileInterval = interval;

    /// <summary>
    /// Called on every full suggestion acceptance. Triggers a background
    /// generation when the counter reaches the configured interval.
    /// </summary>
    public void OnAccepted()
    {
        lock (_lock)
        {
            _acceptCount++;
            if (_acceptCount >= _profileInterval && !_isGenerating)
            {
                _acceptCount = 0;
                _ = Task.Run(GenerateAsync).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Log($"Unobserved error: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>
    /// Returns a compact, structured prompt block for the given category,
    /// or null if no profile exists yet for that category.
    /// </summary>
    public string? GetVocabularyHint(string category)
    {
        lock (_lock)
        {
            if (_profile == null) return null;
            if (!_profile.Categories.TryGetValue(category, out var vocab)) return null;

            var sb = new StringBuilder();
            sb.AppendLine($"User's vocabulary fingerprint ({category}):");

            if (vocab.TopWords.Count > 0)
                sb.AppendLine($"- Preferred words: {string.Join(", ", vocab.TopWords.Take(8))}");

            if (vocab.OpeningPhrases.Count > 0)
                sb.AppendLine($"- Often opens with: {string.Join(", ", vocab.OpeningPhrases.Take(3).Select(p => $"\"{p}\""))}");

            if (vocab.ClosingPhrases.Count > 0)
                sb.AppendLine($"- Often closes with: {string.Join(", ", vocab.ClosingPhrases.Take(3).Select(p => $"\"{p}\""))}");

            var style = BuildStyleDescription(vocab);
            if (!string.IsNullOrEmpty(style))
                sb.AppendLine($"- Style: {style}");

            return sb.ToString().TrimEnd();
        }
    }

    public VocabularyProfile? GetProfile() { lock (_lock) return _profile; }

    public int ProfiledCategoryCount()
    {
        lock (_lock) return _profile?.Categories.Count ?? 0;
    }

    // ── Generation ────────────────────────────────────────────────────────────

    private async Task GenerateAsync()
    {
        lock (_lock) { _isGenerating = true; }
        try
        {
            var entries = LoadAcceptedEntries();
            Log($"Generating from {entries.Count} accepted entries...");

            var newProfile = new VocabularyProfile
            {
                LastUpdated      = DateTime.UtcNow,
                EntriesProcessed = entries.Count
            };

            var groups = entries
                .GroupBy(e => e.Category)
                .Where(g => g.Count() >= MinEntriesPerCategory);

            foreach (var group in groups)
            {
                var samples     = group.OrderByDescending(e => e.Timestamp).Take(MaxSamplesPerCategory).ToList();
                var completions = samples.Select(s => s.Completion).ToList();
                var vocab       = AnalyzeCategory(completions);
                newProfile.Categories[group.Key] = vocab;
                Log($"Extracted {group.Key}: {vocab.TopWords.Count} words, " +
                    $"{vocab.OpeningPhrases.Count} openings, {vocab.ClosingPhrases.Count} closings");
            }

            lock (_lock) { _profile = newProfile; }
            SaveProfile();
            Log("Vocabulary profile generation complete.");
        }
        catch (Exception ex)
        {
            Log($"Generate error: {ex.Message}");
        }
        finally
        {
            lock (_lock) { _isGenerating = false; }
        }

        await Task.CompletedTask; // async signature for Task.Run compatibility
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    /// <summary>Extracts all vocabulary signals for one category's completions.</summary>
    private static CategoryVocabulary AnalyzeCategory(List<string> completions)
    {
        return new CategoryVocabulary
        {
            TopWords        = ExtractTopWords(completions),
            OpeningPhrases  = ExtractPhrases(completions, fromStart: true),
            ClosingPhrases  = ExtractPhrases(completions, fromStart: false),
            AvgSentenceWords = ComputeAvgSentenceLength(completions),
            UsesContractions = DetectContractions(completions),
            OxfordComma      = DetectOxfordComma(completions),
            Formality        = DetectFormality(completions),
            EmDash           = DetectEmDash(completions),
            Exclamation      = DetectExclamation(completions),
            UsesEllipsis     = DetectEllipsis(completions)
        };
    }

    /// <summary>
    /// Finds words that appear frequently in this user's completions but are NOT
    /// in the common-English baseline — these are their personal "voice words".
    /// </summary>
    private static List<string> ExtractTopWords(IEnumerable<string> completions)
    {
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var completion in completions)
        {
            var words = SplitIntoWords(completion);
            foreach (var word in words)
            {
                if (word.Length < 4) continue;
                if (CommonWords.Contains(word)) continue;
                freq[word] = freq.GetValueOrDefault(word, 0) + 1;
            }
        }

        return freq
            .Where(kv => kv.Value >= MinWordFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(MaxTopWords)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Extracts n-gram (2- and 3-word) phrases from the start or end of completions.
    /// Only phrases that appear at least <see cref="MinPhraseFrequency"/> times are kept.
    /// </summary>
    private static List<string> ExtractPhrases(IEnumerable<string> completions, bool fromStart)
    {
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var completion in completions)
        {
            var trimmed = completion.Trim();
            if (trimmed.Length < 8) continue;

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) continue;

            // Take up to 4 words from the target end, stripping trailing punctuation on closing words
            string[] target = fromStart
                ? words.Take(4).ToArray()
                : words.TakeLast(4).Select(w => w.TrimEnd('.', ',', '!', '?', ';', ':')).ToArray();

            // Build 2-grams and 3-grams
            for (int len = 2; len <= 3 && len <= target.Length; len++)
            {
                for (int i = 0; i <= target.Length - len; i++)
                {
                    var phrase = string.Join(" ", target.Skip(i).Take(len));
                    if (phrase.Length < 5) continue;
                    freq[phrase] = freq.GetValueOrDefault(phrase, 0) + 1;
                }
            }
        }

        return freq
            .Where(kv => kv.Value >= MinPhraseFrequency)
            .OrderByDescending(kv => kv.Value)
            .Take(MaxPhrases)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static double ComputeAvgSentenceLength(IEnumerable<string> completions)
    {
        var lengths = new List<int>();
        foreach (var c in completions)
        {
            var sentences = c.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in sentences)
            {
                int wc = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (wc > 1) lengths.Add(wc);
            }
        }
        return lengths.Count > 0 ? lengths.Average() : 10.0;
    }

    private static bool DetectContractions(IEnumerable<string> completions)
    {
        int contractions = 0, words = 0;
        foreach (var c in completions)
        {
            var ws = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            words += ws.Length;
            contractions += ws.Count(w =>
                w.Contains("n't", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("'re", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("'ve", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("'ll", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("'d",  StringComparison.OrdinalIgnoreCase) ||
                w.Contains("'m",  StringComparison.OrdinalIgnoreCase));
        }
        return words > 0 && (double)contractions / words > 0.04;
    }

    private static bool DetectOxfordComma(IEnumerable<string> completions)
    {
        var list = completions.ToList();
        int hits = list.Count(c => c.Contains(", and ", StringComparison.OrdinalIgnoreCase)
                                || c.Contains(", or ",  StringComparison.OrdinalIgnoreCase));
        return hits >= Math.Max(2, list.Count * 0.10);
    }

    private static string DetectFormality(IEnumerable<string> completions)
    {
        var joined = string.Join(" ", completions).ToLowerInvariant();

        string[] formalTerms  = ["therefore", "furthermore", "however", "regarding",
                                  "pursuant", "accordingly", "notwithstanding",
                                  "herein", "whereas", "hereby", "aforementioned"];
        string[] casualTerms  = ["gonna", "wanna", "kinda", "sorta", "yeah", "yep",
                                  "nope", "cool", "awesome", "hey", "btw", "fyi",
                                  "lol", "omg", "totally", "literally"];

        int formalHits = formalTerms.Sum(t => CountOccurrences(joined, t));
        int casualHits = casualTerms.Sum(t => CountOccurrences(joined, t));

        if (formalHits > casualHits * 2) return "formal";
        if (casualHits > formalHits * 2) return "casual";
        return "casual-professional";
    }

    private static string DetectEmDash(IEnumerable<string> completions)
    {
        var list  = completions.ToList();
        int hits  = list.Count(c => c.Contains(" — ") || c.Contains("—") || c.Contains(" -- "));
        return hits >= list.Count * 0.12 ? "frequent" : "rare";
    }

    private static string DetectExclamation(IEnumerable<string> completions)
    {
        var list = completions.ToList();
        int hits = list.Count(c => c.Contains('!'));
        if (hits >= list.Count * 0.20) return "frequent";
        if (hits >= list.Count * 0.07) return "occasional";
        return "rare";
    }

    private static bool DetectEllipsis(IEnumerable<string> completions) =>
        completions.Count(c => c.Contains("...")) >= 2;

    // ── Prompt formatting ─────────────────────────────────────────────────────

    private static string BuildStyleDescription(CategoryVocabulary vocab)
    {
        var parts = new List<string>
        {
            vocab.Formality,
            vocab.UsesContractions ? "uses contractions" : "avoids contractions",
            $"~{(int)Math.Round(vocab.AvgSentenceWords)} words/sentence"
        };
        if (vocab.OxfordComma)                         parts.Add("Oxford comma");
        if (vocab.EmDash == "frequent")                parts.Add("uses em-dashes");
        if (vocab.Exclamation == "frequent")           parts.Add("enthusiastic punctuation");
        else if (vocab.Exclamation == "occasional")    parts.Add("occasional exclamation");
        if (vocab.UsesEllipsis)                        parts.Add("uses ellipsis");
        return string.Join(" · ", parts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] SplitIntoWords(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':',
                    '"', '\'', '(', ')', '[', ']', '{', '}', '-'],
                StringSplitOptions.RemoveEmptyEntries);

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadProfile()
    {
        try
        {
            if (!File.Exists(_profilePath)) return;
            var json = File.ReadAllText(_profilePath);
            _profile = JsonSerializer.Deserialize<VocabularyProfile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Log($"Loaded: {_profile?.Categories.Count ?? 0} categories");
        }
        catch (Exception ex) { Log($"Load error: {ex.Message}"); }
    }

    private void SaveProfile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var json     = JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _profilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _profilePath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex.Message}"); }
    }

    private List<VocabEntry> LoadAcceptedEntries()
    {
        var entries = new List<VocabEntry>();
        try
        {
            if (!File.Exists(_trackingPath)) return entries;
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var line in File.ReadAllLines(_trackingPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<VocabEntry>(line, opts);
                    if (e?.Action == "accepted" && !string.IsNullOrWhiteSpace(e.Completion))
                        entries.Add(e);
                }
                catch { /* malformed line — skip */ }
            }
        }
        catch (Exception ex) { Log($"Read error: {ex.Message}"); }
        return entries;
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Vocab] {msg}\n"); }
        catch (IOException) { }
    }

    // ── Private entry model ───────────────────────────────────────────────────

    private class VocabEntry
    {
        public DateTime Timestamp  { get; set; }
        public string   Action     { get; set; } = "";
        public string   Completion { get; set; } = "";
        public string   Category   { get; set; } = "";
    }

    // ── Common-English word baseline ──────────────────────────────────────────
    // Words in this set are excluded from personal vocabulary extraction.
    // Source: top ~350 most frequent English words (function words + basic verbs/adj/adv).

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // articles
        "a","an","the",
        // pronouns
        "i","me","my","myself","we","our","ours","ourselves","you","your","yours",
        "yourself","yourselves","he","him","his","himself","she","her","hers","herself",
        "it","its","itself","they","them","their","theirs","themselves",
        "what","which","who","whom","this","that","these","those","whoever","whatever",
        // to be
        "am","is","are","was","were","be","been","being",
        // to have
        "have","has","had","having",
        // to do
        "do","does","did","doing",
        // modals
        "will","would","shall","should","may","might","must","can","could","ought",
        // conjunctions
        "and","but","or","nor","for","yet","so","although","because","since","while",
        "if","unless","until","after","before","when","where","whether","both",
        "either","neither","though","whereas",
        // prepositions
        "at","by","for","from","in","into","of","on","onto","out","over","past",
        "since","through","to","under","until","up","via","with","within","without",
        "about","above","across","after","against","along","among","around","as",
        "before","behind","below","beneath","beside","between","beyond","during",
        "except","inside","near","off","outside","per","throughout","toward",
        "towards","upon","than","like",
        // common verbs
        "say","said","go","went","gone","get","got","make","made","know","knew",
        "think","thought","take","took","see","saw","come","came","want","look",
        "use","find","found","give","gave","tell","told","work","call","try","ask",
        "need","feel","felt","become","became","leave","left","put","mean","keep",
        "kept","let","begin","began","show","showed","hear","heard","run","move",
        "live","write","wrote","provide","sit","sat","stand","lose","lost","pay",
        "paid","meet","met","include","continue","set","learn","change","lead",
        "understand","watch","follow","stop","create","read","spend","spent","grow",
        "grew","open","walk","offer","remember","appear","buy","bought","wait",
        "serve","send","sent","expect","build","built","stay","fall","fell","reach",
        "remain","suggest","raise","pass","sell","sold","require","report","decide",
        "pull","bring","brought","start","end","turn","help","play","hold","move",
        "seem","show","hear","come","give","talk","start","hand","place","turn",
        // common adjectives
        "good","new","first","last","long","great","little","own","other","old",
        "right","big","high","different","small","large","next","early","young",
        "important","few","public","bad","same","able","sure","real","best","free",
        "many","much","more","most","any","each","every","such","another","every",
        "true","false","full","open","whole","clear","hard","easy","sure","certain",
        // common adverbs
        "also","just","now","how","then","here","well","only","very","even","back",
        "there","still","down","where","when","often","never","always","sometimes",
        "already","together","however","therefore","actually","really","probably",
        "certainly","perhaps","soon","again","once","almost","enough","quite",
        "rather","usually","simply","today","already","else","away","far","forward",
        "instead","maybe","often","otherwise","recently","together","away","yes","no",
        // numbers + quantifiers
        "one","two","three","four","five","six","seven","eight","nine","ten",
        "some","any","all","none","both","half","zero",
        // misc high-frequency
        "okay","ok","thing","things","time","way","days","year","years","day",
        "people","man","world","life","hand","part","case","week","number",
        "home","room","word","end","point","kind","side","lot","bit",
    };
}

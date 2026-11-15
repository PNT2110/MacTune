using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MacTune.Services;

/// <summary>
/// Runtime localization. The UI is authored in Vietnamese; when the language is English, every visible
/// text element is translated just before it is rendered (hooked on LayoutUpdated, so there is no flicker).
/// Each element remembers its Vietnamese source so switching languages back and forth is lossless.
/// Code must not read UI text back to make decisions (it may be translated).
/// </summary>
public static partial class Loc
{
    public static string Language { get; private set; } = "vi";
    public static bool IsEnglish => Language == "en";

    sealed class Entry { public string Source = ""; public string Output = ""; }

    static readonly ConditionalWeakTable<DependencyObject, Entry> Seen = new();
    static readonly Dictionary<string, string> Cache = new();
    static List<KeyValuePair<string, string>>? _phrases;
    static readonly List<WeakReference<Window>> Windows = new();
    static bool _dirty, _walking;

    public static void SetLanguage(string lang)
    {
        lang = lang == "en" ? "en" : "vi";
        if (lang == Language) return;
        Language = lang;
        Cache.Clear();
        _dirty = true;
        foreach (var wr in Windows)
            if (wr.TryGetTarget(out var w)) Apply(w);
    }

    /// <summary>Translate a string for places the walker cannot reach (tray menu, balloons, message boxes).</summary>
    public static string T(string vi) => Tr(vi);

    static bool HasVietnamese(string s)
    {
        foreach (char c in s)
            if (c >= 'À' && c <= 'ỹ' && c != '×' && c != '÷') return true;
        return false;
    }

    static string Tr(string s)
    {
        if (!IsEnglish || string.IsNullOrEmpty(s)) return s;
        if (Cache.TryGetValue(s, out var hit)) return hit;

        string result;
        string trimmed = s.Trim();
        if (En.TryGetValue(trimmed, out var exact)) result = s.Replace(trimmed, exact);
        else if (EnWords.TryGetValue(trimmed, out var word)) result = s.Replace(trimmed, word);
        else if (!HasVietnamese(s) && !EnWords.Keys.Any(k => s.Contains(k))) result = s;
        else
        {
            _phrases ??= En.Concat(EnWords).OrderByDescending(p => p.Key.Length).ToList();
            var sb = new StringBuilder(s);
            foreach (var (vi, en) in _phrases)
            {
                int at = 0;
                while (true)
                {
                    string cur = sb.ToString();
                    int i = cur.IndexOf(vi, at, StringComparison.Ordinal);
                    if (i < 0) break;
                    // whole-phrase match only: no letter glued to either end
                    bool leftOk = i == 0 || !char.IsLetter(vi[0]) || !char.IsLetter(cur[i - 1]);
                    int end = i + vi.Length;
                    bool rightOk = end >= cur.Length || !char.IsLetter(vi[^1]) || !char.IsLetter(cur[end]);
                    if (leftOk && rightOk) { sb.Remove(i, vi.Length).Insert(i, en); at = i + en.Length; }
                    else at = i + 1;
                }
            }
            result = sb.ToString();
        }
        if (Cache.Count > 4000) Cache.Clear();
        Cache[s] = result;
        return result;
    }

    /// <summary>Keeps a window translated: call once after InitializeComponent.</summary>
    public static void Attach(Window w)
    {
        Windows.Add(new WeakReference<Window>(w));
        w.LayoutUpdated += (_, _) =>
        {
            if (_walking || (!IsEnglish && !_dirty)) return;
            Apply(w);
        };
    }

    static void Apply(Window w)
    {
        if (_walking) return;
        _walking = true;
        try
        {
            Walk(w);
            if (!IsEnglish) _dirty = false;
        }
        finally { _walking = false; }
    }

    static string Process(DependencyObject o, string current)
    {
        if (Seen.TryGetValue(o, out var e))
        {
            if (current != e.Output) e.Source = current;   // code (or a binding) set new text
        }
        else
        {
            if (!IsEnglish) return current;                // nothing to restore
            e = new Entry { Source = current };
            Seen.Add(o, e);
        }
        e.Output = Tr(e.Source);
        return e.Output;
    }

    static void Walk(DependencyObject o)
    {
        // English: only what is on screen. Restoring Vietnamese: everything that was ever translated.
        if (IsEnglish && o is UIElement { Visibility: not Visibility.Visible }) return;

        switch (o)
        {
            case TextBlock tb:
                if (tb.Inlines.Count > 1 || (tb.Inlines.Count == 1 && tb.Inlines.FirstInline is not Run))
                {
                    foreach (var run in tb.Inlines.OfType<Run>().ToList())
                    {
                        string t = Process(run, run.Text);
                        if (t != run.Text) run.SetCurrentValue(Run.TextProperty, t);
                    }
                }
                else
                {
                    string t = Process(tb, tb.Text);
                    if (t != tb.Text) tb.SetCurrentValue(TextBlock.TextProperty, t);
                }
                break;

            case ComboBox cb:
                foreach (var item in cb.Items.OfType<ComboBoxItem>())
                    if (item.Content is string s)
                    {
                        string t = Process(item, s);
                        if (t != s) item.SetCurrentValue(ContentControl.ContentProperty, t);
                    }
                break;
        }

        if (o is FrameworkElement { ToolTip: string tip } fe)
        {
            string t = Process(Tips.GetValue(fe, _ => new Holder()), tip);
            if (t != tip) fe.ToolTip = t;
        }

        int n = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(o) : 0;
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
    }

    // tooltips are plain strings on the element, so they get their own tracking object
    sealed class Holder : DependencyObject { }
    static readonly ConditionalWeakTable<FrameworkElement, Holder> Tips = new();
}

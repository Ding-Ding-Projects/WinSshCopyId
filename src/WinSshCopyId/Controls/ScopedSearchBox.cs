using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinSshCopyId.Workspace;

namespace WinSshCopyId.Controls;

/// <summary>A search field with independently owned query state and a lazily created anchored workbench.</summary>
public sealed class ScopedSearchBox : StackPanel
{
    private readonly TextBox field;
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WorkspaceSearch engine = new();
    private readonly string scopeId;
    private SearchQuery query = new();
    private bool synchronizing;
    private Action? synchronizeWorkbench;
    public SearchQuery Query => query;
    public event EventHandler? QueryChanged;
    public void SetPlaceholder(string placeholder)
    {
        field.PlaceholderText=placeholder;
        AutomationProperties.SetName(field,placeholder);
    }

    public ScopedSearchBox(string scopeId, string placeholder)
    {
        this.scopeId = scopeId;
        Spacing = 4;
        field = new TextBox { PlaceholderText = placeholder, MaxLength = WorkspaceSearch.MaxPatternLength, MinWidth = 80 };
        AutomationProperties.SetName(field, placeholder);
        AutomationProperties.SetAutomationId(field, scopeId + "-search");
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(field);
        var builder = new Button { Content = ".*", MinWidth = 44, MinHeight = 40 };
        ApplyStyle(builder, "MaterialButtonStyle");
        ApplyStyle(field, "MaterialTextBoxStyle");
        AutomationProperties.SetName(builder, "Open regular expression workbench for " + placeholder);
        ToolTipService.SetToolTip(builder, "Regular expression workbench");
        Grid.SetColumn(builder, 1);
        row.Children.Add(builder);
        Children.Add(row);
        Children.Add(validation);
        field.TextChanged += (_, _) => { if (!synchronizing) SetQuery(query with { Text = field.Text }); };
        // A nested filter creates its workbench only on explicit activation, avoiding eager recursion.
        builder.Click += (_, _) =>
        {
            if (builder.Flyout is null) builder.Flyout = CreateWorkbench();
            builder.Flyout.ShowAt(builder);
        };
    }

    public void SetQuery(SearchQuery value)
    {
        query = value;
        synchronizing = true;
        field.Text = value.Text;
        synchronizing = false;
        validation.Text = engine.Evaluate("", query).ValidationError ?? "";
        validation.Visibility = validation.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        synchronizeWorkbench?.Invoke();
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }

    private Flyout CreateWorkbench()
    {
        var panel = new StackPanel { Spacing = 12, Width = 480, MaxWidth = 480 };
        panel.Children.Add(new TextBlock { Text = "Regular expression workbench", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = WorkspaceSearch.EngineDescription + " Runtime " + Environment.Version + ". Sample text is processed locally and is not saved.", TextWrapping = TextWrapping.Wrap });
        var localFilter = new ScopedSearchBox(scopeId + "-workbench", "Find workbench sections");
        panel.Children.Add(localFilter);
        var sections = new List<(string Name, UIElement Element)>();
        StackPanel Section(string name)
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock { Text = name, FontSize = 16 });
            sections.Add((name, section));
            panel.Children.Add(section);
            return section;
        }
        localFilter.QueryChanged += (_, _) =>
        {
            foreach (var section in sections)
                section.Element.Visibility = localFilter.Query.Text.Length == 0 || engine.Evaluate(section.Name, localFilter.Query).Matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var editing = Section("Pattern and flags");
        var pattern = new TextBox { Header = "Pattern or literal text", MaxLength = WorkspaceSearch.MaxPatternLength, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        editing.Children.Add(pattern);
        var regex = new CheckBox { Content = "Use regular expressions (off means literal text)" };
        var ignoreCase = new CheckBox { Content = "Ignore case" };
        var multiline = new CheckBox { Content = "Multiline anchors (^ and $)" };
        var singleline = new CheckBox { Content = "Singleline dot (matches newline)" };
        var whitespace = new CheckBox { Content = "Ignore pattern whitespace" };
        foreach (var flag in new[] { regex, ignoreCase, multiline, singleline, whitespace }) editing.Children.Add(flag);
        var samples = Section("Sample input and replacement");
        var sample = new TextBox { Header = "Sample input", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = WorkspaceSearch.MaxInputLength, MinHeight = 90, MaxHeight = 200 };
        var replacement = new TextBox { Header = "Replacement template", PlaceholderText = "${name}, $1, $$", MaxLength = WorkspaceSearch.MaxPatternLength };
        samples.Children.Add(sample);
        samples.Children.Add(replacement);
        var resultsSection = Section("Matches, captures and replacement preview");
        var diagnostic = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var matches = new TextBox { Header = "Matches and captures", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220 };
        var preview = new TextBox { Header = "Replacement preview", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
        resultsSection.Children.Add(diagnostic);
        resultsSection.Children.Add(matches);
        resultsSection.Children.Add(preview);
        var generation = 0;
        var updating = false;
        var active = false;
        async void Evaluate()
        {
            var current = ++generation;
            if (!active) return;
            var input = sample.Text;
            var replace = replacement.Text;
            var snapshot = query;
            diagnostic.Text = "Evaluating locally…";
            await Task.Delay(180);
            if (current != generation) return;
            var watch = Stopwatch.StartNew();
            var result = await Task.Run(() => engine.Evaluate(input, snapshot, replace));
            if (current != generation) return;
            diagnostic.Text = result.ValidationError ?? $"{result.Matches.Count} matches in {watch.Elapsed.TotalMilliseconds:F1} ms. " + (result.Limited ? "Match limit reached; preview is partial." : "Evaluation finished.");
            var text = new StringBuilder();
            foreach (var match in result.Matches.Take(100))
            {
                text.AppendLine($"Match [{match.Index}..{match.Index + match.Length}){(match.Length == 0 ? " (zero width)" : "")}: {Display(match.Value)}");
                foreach (var capture in match.Captures.Take(30)) text.AppendLine($"  Group {capture.Group} [{capture.Index}, {capture.Length}]: {Display(capture.Value)}");
                if (match.Captures.Count > 30) text.AppendLine("  Display limited to 30 captures for this match.");
            }
            if (result.Matches.Count > 100) text.AppendLine("Display limited to the first 100 matches.");
            matches.Text = text.ToString();
            preview.Text = result.ReplacementPreview ?? "";
        }
        void UpdateQuery()
        {
            if (updating) return;
            SetQuery(new(pattern.Text, regex.IsChecked == true, ignoreCase.IsChecked == true, multiline.IsChecked == true, singleline.IsChecked == true, whitespace.IsChecked == true));
        }
        synchronizeWorkbench = () =>
        {
            updating = true;
            pattern.Text = query.Text;
            regex.IsChecked = query.UseRegex;
            ignoreCase.IsChecked = query.IgnoreCase;
            multiline.IsChecked = query.Multiline;
            singleline.IsChecked = query.Singleline;
            whitespace.IsChecked = query.IgnorePatternWhitespace;
            updating = false;
            Evaluate();
        };
        pattern.TextChanged += (_, _) => UpdateQuery();
        foreach (var flag in new[] { regex, ignoreCase, multiline, singleline, whitespace })
        {
            flag.Checked += (_, _) => UpdateQuery();
            flag.Unchecked += (_, _) => UpdateQuery();
        }
        sample.TextChanged += (_, _) => Evaluate();
        replacement.TextChanged += (_, _) => Evaluate();
        var guided = Section("Guided construction");
        var constructionSearch = new ScopedSearchBox(scopeId + "-constructs", "Filter expression constructs");
        guided.Children.Add(constructionSearch);
        var constructs = new (string Name, string Pattern)[]
        {
            ("Digit", @"\d"), ("Word character", @"\w"), ("Unicode letter", @"\p{L}"),
            ("Unicode code point", @"\u0041"), ("Character class", "[a-z]"), ("Class subtraction", "[a-z-[aeiou]]"),
            ("Start of input", @"\A"), ("End of input", @"\z"), ("Word boundary", @"\b"),
            ("Named capture", "(?<name>text)"), ("Numbered capture", "(text)"), ("Non-capturing group", "(?:text)"),
            ("Atomic group", "(?>text)"), ("Alternation", "(?:first|second)"), ("Greedy repetition", ".*"),
            ("Lazy repetition", ".*?"), ("Bounded repetition", "a{1,4}"), ("Positive lookahead", "(?=text)"),
            ("Negative lookahead", "(?!text)"), ("Lookbehind", "(?<=text)"), ("Backreference", @"\k<name>"),
            ("Conditional", "(?(name)yes|no)"), ("Inline modifiers", "(?im:text)"), ("Escaped literal dot", @"\.")
        };
        var buttons = new List<(string Name, Button Button)>();
        foreach (var construct in constructs)
        {
            var button = new Button { Content = construct.Name + "  " + construct.Pattern, HorizontalAlignment = HorizontalAlignment.Stretch };
            ApplyStyle(button, "MaterialButtonStyle");
            button.Click += (_, _) =>
            {
                var start = pattern.SelectionStart;
                var length = pattern.SelectionLength;
                SetQuery(query with { UseRegex = true, Text = pattern.Text.Remove(start, length).Insert(start, construct.Pattern) });
                pattern.Focus(FocusState.Programmatic);
                pattern.Select(start, construct.Pattern.Length);
            };
            buttons.Add((construct.Name + " " + construct.Pattern, button));
            guided.Children.Add(button);
        }
        constructionSearch.QueryChanged += (_, _) =>
        {
            foreach (var item in buttons) item.Button.Visibility = constructionSearch.Query.Text.Length == 0 || engine.Evaluate(item.Name, constructionSearch.Query).Matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var capabilities = Section("Engine capabilities and limits");
        capabilities.Children.Add(new TextBlock
        {
            Text = "The .NET engine supports Unicode categories, capture groups, lookarounds, backreferences, conditionals and atomic groups. Use class subtraction for set differences. Possessive quantifier syntax, recursive subroutine calls and a debugger execution trace are not exposed by this engine. Escaping uses .NET regex syntax, not JavaScript delimiters. Nested repetitions can backtrack heavily: test hostile inputs; timeout and total work bounds apply. Structured parse explanation, saved snippets, suite import/export and match navigation are not implemented in this workbench.",
            TextWrapping = TextWrapping.Wrap
        });
        synchronizeWorkbench();
        var scroll = new ScrollViewer { Content = panel, MaxHeight = 600, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var flyout = new Flyout { Content = scroll };
        flyout.Opening += (_, _) =>
        {
            active = true;
            panel.Width = Math.Max(180, Math.Min(480, (XamlRoot?.Size.Width ?? 560) - 80));
            scroll.MaxHeight = Math.Max(120, Math.Min(600, (XamlRoot?.Size.Height ?? 680) - 80));
            Evaluate();
        };
        flyout.Closed += (_, _) => { active = false; generation++; sample.Text = ""; replacement.Text = ""; matches.Text = ""; preview.Text = ""; };
        return flyout;
    }

    private static string Display(string text) => text.Length <= 240 ? text : text[..240] + "… (display shortened)";

    private static void ApplyStyle(Control control, string key)
    {
        if (Application.Current.Resources.ContainsKey(key) && Application.Current.Resources[key] is Style style) control.Style = style;
    }
}

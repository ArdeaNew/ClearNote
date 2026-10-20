using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Folio.Core;
using DocumentList = System.Windows.Documents.List;

namespace Folio.App.Controls;

/// <summary>Native, undoable text editing with a small, text-only storage boundary.</summary>
public sealed class NoteEditor : UserControl
{
    private static readonly Color TextColor = Color.FromRgb(15, 20, 25);
    private static readonly Brush AutomaticForeground = FrozenBrush(TextColor);
    private static readonly double[] HeadingSizes = [30, 25, 22, 20, 19, 18];
    private readonly RichTextBox _editor;
    private bool _loading;
    private bool _editing;
    private bool _executingListCommand;
    private bool _inlineLayout;

    private static readonly HashSet<string> UnsupportedFormattingCommands =
    [
        "ToggleUnderline", "ToggleSubscript", "ToggleSuperscript",
        "IncreaseFontSize", "DecreaseFontSize", "ApplyFontSize", "ApplyFontFamily",
        "ApplyForeground", "ApplyBackground", "AlignLeft", "AlignCenter", "AlignRight", "AlignJustify",
        "RemoveListMarkers", "IncreaseIndentation", "DecreaseIndentation",
        "ApplySingleSpace", "ApplyOneAndAHalfSpace", "ApplyDoubleSpace",
        "ApplyParagraphFlowDirectionLTR", "ApplyParagraphFlowDirectionRTL",
        "ApplyInlineFlowDirectionLTR", "ApplyInlineFlowDirectionRTL",
        "ResetFormat", "CopyFormat", "PasteFormat"
    ];

    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;
    public Func<IDataObject, bool>? AttachmentPasteHandler { get; set; }
    public event Action<Exception>? InputRejected;
    public RichTextBox TextBox => _editor;
    public bool CanUndo => _editing && _editor.CanUndo;
    public bool CanRedo => _editing && _editor.CanRedo;
    public bool IsBold => _editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 700;
    public bool IsItalic => Equals(_editor.Selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
    public bool IsHeading => AllSelected(paragraph => HeadingLevel(paragraph) > 0);
    public bool IsBulleted => AllSelected(paragraph => paragraph.Parent is ListItem { Parent: DocumentList { MarkerStyle: TextMarkerStyle.Disc } });
    public bool IsNumbered => AllSelected(paragraph => paragraph.Parent is ListItem { Parent: DocumentList { MarkerStyle: TextMarkerStyle.Decimal } });
    public bool IsQuote => AllSelected(IsQuoteParagraph);

    public string PlainText
    {
        get => string.Join("\n", Capture().Paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text))));
        set
        {
            if (!_editing) return;
            _editor.BeginChange();
            try
            {
                _editor.SelectAll();
                _editor.Selection.Text = value ?? "";
                _editor.CaretPosition = _editor.Document.ContentEnd;
            }
            finally { _editor.EndChange(); }
        }
    }

    public NoteEditor()
    {
        Background = Brushes.Transparent;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _editor = new RichTextBox
        {
            Background = Brushes.Transparent, Foreground = AutomaticForeground,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            FontFamily = AppTypography.Text, FontSize = 16,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsReturn = true, AcceptsTab = false, AllowDrop = false,
            IsUndoEnabled = true, UndoLimit = 100,
            SelectionBrush = new SolidColorBrush(Color.FromRgb(218, 218, 218)),
            Document = BuildDocument(new NoteContent())
        };
        System.Windows.Automation.AutomationProperties.SetName(_editor, "Note text");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_editor, "NoteText");
        _editor.TextChanged += (_, _) =>
        {
            if (_inlineLayout) { _editor.InvalidateMeasure(); InvalidateMeasure(); }
            if (!_loading && _editing) Changed?.Invoke(this, EventArgs.Empty);
        };
        _editor.SelectionChanged += (_, _) => { if (!_loading) SelectionChanged?.Invoke(this, EventArgs.Empty); };
        _editor.PreviewTextInput += OnPreviewTextInput;
        _editor.PreviewKeyDown += OnPreviewKeyDown;
        _editor.PreviewMouseWheel += OnPreviewMouseWheel;
        CommandManager.AddPreviewCanExecuteHandler(_editor, OnPreviewCanExecute);
        CommandManager.AddPreviewExecutedHandler(_editor, OnPreviewExecuted);
        DataObject.AddPastingHandler(_editor, OnPasting);
        _editor.PreviewDragOver += RejectDrop;
        _editor.PreviewDrop += RejectDrop;
        Content = _editor;
    }

    public void Load(CardDocument card, bool editing)
    {
        ArgumentNullException.ThrowIfNull(card);
        _loading = true;
        try
        {
            _editing = editing;
            _editor.Document = BuildDocument(NoteText.Read(card));
            _editor.IsReadOnly = !editing;
            _editor.IsReadOnlyCaretVisible = editing;
            _editor.IsUndoEnabled = editing;
            _editor.AllowDrop = false;
            _editor.ScrollToHome();
        }
        finally { _loading = false; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Feed cards take their natural document height and let the containing feed scroll.</summary>
    public void SetInlineLayout(bool inline)
    {
        _inlineLayout = inline;
        _editor.VerticalScrollBarVisibility = inline ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _editor.VerticalAlignment = inline ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        VerticalContentAlignment = inline ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        _editor.Height = double.NaN;
        _editor.MinHeight = 0;
        _editor.MaxHeight = double.PositiveInfinity;
        _editor.Document.PagePadding = new Thickness(0);
        if (inline)
        {
            if (_editor.Document.Blocks.LastBlock is { } last)
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
            if (Paragraphs(_editor.Document.Blocks).LastOrDefault() is { } paragraph)
                paragraph.Margin = new Thickness(paragraph.Margin.Left, paragraph.Margin.Top, paragraph.Margin.Right, 0);
        }
        _editor.InvalidateMeasure();
        InvalidateMeasure();
    }

    public NoteContent Capture()
    {
        var content = new NoteContent();
        ReadBlocks(_editor.Document.Blocks, content.Paragraphs);
        if (content.Paragraphs.Count == 0) content.Paragraphs.Add(new NoteParagraph());
        return content;
    }

    public void FocusEditor() => _editor.Focus();
    public void Undo() { if (CanUndo) _editor.Undo(); }
    public void Redo() { if (CanRedo) _editor.Redo(); }
    public void ToggleBold() => Execute(EditingCommands.ToggleBold);
    public void ToggleItalic() => Execute(EditingCommands.ToggleItalic);
    public void ToggleBullets() => ToggleList(numbered: false);
    public void ToggleNumbering() => ToggleList(numbered: true);

    public void ToggleHeading()
    {
        if (!_editing) return;
        SetParagraphKind(IsHeading ? "body" : "heading", 2);
    }

    public void SetHeading(int level)
    {
        if (!_editing) return;
        if (level is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        SetParagraphKind("heading", level);
    }

    public void ToggleQuote()
    {
        if (!_editing) return;
        SetParagraphKind(IsQuote ? "body" : "quote");
    }

    public void SetForeground(string? color) => SetTextColor(TextElement.ForegroundProperty, color);
    public void SetBackground(string? color) => SetTextColor(TextElement.BackgroundProperty, color);

    private void SetTextColor(DependencyProperty property, string? color)
    {
        if (!_editing) return;
        if (color is not null && !NoteColors.IsValid(color)) throw new ArgumentException("Use a six-digit RGB color.", nameof(color));
        var brush = color is null
            ? property == TextElement.BackgroundProperty ? Brushes.Transparent : AutomaticForeground
            : ColorBrush(color);
        _editor.BeginChange();
        try { _editor.Selection.ApplyPropertyValue(property, brush); }
        finally { _editor.EndChange(); }
        _editor.Focus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleList(bool numbered)
    {
        if (!_editing) return;
        _editor.BeginChange();
        try
        {
            foreach (var paragraph in SelectedParagraphs().ToArray()) ApplyParagraphStyle(paragraph, "body");
            ExecuteListCommand(numbered ? EditingCommands.ToggleNumbering : EditingCommands.ToggleBullets);
            foreach (var paragraph in SelectedParagraphs().Where(paragraph => paragraph.Parent is ListItem))
                paragraph.Margin = new Thickness(0, 0, 0, 5);
            foreach (var list in SelectedParagraphs().Select(paragraph => paragraph.Parent).OfType<ListItem>()
                         .Select(item => item.Parent).OfType<DocumentList>().Distinct()) ApplyListLayout(list);
        }
        finally { _editor.EndChange(); }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetParagraphKind(string kind, int level = 2)
    {
        if (!_editing) return;
        var start = _editor.Selection.Start;
        var end = _editor.Selection.End;
        var paragraphs = SelectedParagraphs().ToArray();
        _editor.BeginChange();
        try
        {
            foreach (var paragraph in paragraphs.Where(paragraph => paragraph.Parent is ListItem))
            {
                _editor.Selection.Select(paragraph.ContentStart, paragraph.ContentEnd);
                var numbered = paragraph.Parent is ListItem { Parent: DocumentList { MarkerStyle: TextMarkerStyle.Decimal } };
                ExecuteListCommand(numbered ? EditingCommands.ToggleNumbering : EditingCommands.ToggleBullets);
            }
            _editor.Selection.Select(start, end);
            foreach (var paragraph in SelectedParagraphs()) ApplyParagraphStyle(paragraph, kind, level);
        }
        finally { _editor.EndChange(); }
        _editor.Focus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Execute(RoutedUICommand command)
    {
        if (!_editing) return;
        _editor.Focus();
        if (command.CanExecute(null, _editor)) command.Execute(null, _editor);
    }

    private void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == EditingCommands.EnterParagraphBreak)
        {
            e.CanExecute = _editing;
            e.Handled = true;
            return;
        }
        if (!IsUnsupportedFormatting(e.Command)) return;
        e.CanExecute = false;
        e.Handled = true;
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsUnsupportedFormatting(e.Command)) { e.Handled = true; return; }
        if (e.Command == EditingCommands.EnterParagraphBreak) { e.Handled = true; BreakParagraph(); return; }
        if (_executingListCommand) return;
        if (e.Command == EditingCommands.ToggleBullets) { e.Handled = true; ToggleBullets(); }
        else if (e.Command == EditingCommands.ToggleNumbering) { e.Handled = true; ToggleNumbering(); }
    }

    private static bool IsUnsupportedFormatting(ICommand command) => command is RoutedCommand routed &&
        routed.OwnerType == typeof(EditingCommands) && UnsupportedFormattingCommands.Contains(routed.Name);

    private void ExecuteListCommand(RoutedUICommand command)
    {
        var previous = _executingListCommand;
        _executingListCommand = true;
        try { Execute(command); }
        finally { _executingListCommand = previous; }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_editing || _loading || !_editor.Selection.IsEmpty) return;
        if (e.Text == " " && TryConvertParagraphPrefix()) e.Handled = true;
        else if (e.Text is "*" or "_" && TryConvertInlineDelimiter(e.Text)) e.Handled = true;
    }

    private bool TryConvertParagraphPrefix()
    {
        if (!_editing || !_editor.Selection.IsEmpty || _editor.CaretPosition.Paragraph is not { } paragraph) return false;
        var prefix = TextBetween(paragraph.ContentStart, _editor.CaretPosition);
        var match = Regex.Match(prefix, @"^(?:(?<heading>#{1,6})|(?<ordered>[1-9][0-9]{0,6})(?<orderedDelimiter>[.)])?|(?<bullet>[-*+])|(?<quote>>))$", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        // A list already supplies its marker. Bare digits there are item content, not a new marker.
        if (match.Groups["ordered"].Success && !match.Groups["orderedDelimiter"].Success && paragraph.Parent is ListItem) return false;
        var kind = match.Groups["heading"].Success ? "heading" : match.Groups["ordered"].Success ? "ordered" : match.Groups["bullet"].Success ? "bullet" : "quote";
        var number = 1;
        if (match.Groups["ordered"].Success && (!int.TryParse(match.Groups["ordered"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out number) || number is < 1 or > 1000000)) return false;
        _editor.BeginChange();
        try
        {
            new TextRange(paragraph.ContentStart, _editor.CaretPosition).Text = "";
            _editor.CaretPosition = paragraph.ContentStart;
            ApplyKindAtCaret(kind, match.Groups["heading"].Length, number);
        }
        finally { _editor.EndChange(); }
        return true;
    }

    private bool TryConvertInlineDelimiter(string delimiter)
    {
        if (!_editing || !_editor.Selection.IsEmpty || _editor.CaretPosition.Paragraph is not { } paragraph) return false;
        var prefix = TextBetween(paragraph.ContentStart, _editor.CaretPosition);
        var text = prefix + delimiter;
        var bold = delimiter == "*" && Regex.IsMatch(text, @"(?<![\w*\\])\*\*[^*\r\n]+\*\*$", RegexOptions.CultureInvariant);
        var pattern = bold ? @"(?<![\w*\\])\*\*(?<text>[^*\r\n]+)\*\*$"
            : delimiter == "*" ? @"(?<![\w*\\])\*(?<text>[^*\r\n]+)\*$"
            : @"(?<![\w_\\])_(?<text>[^_\r\n]+)_$";
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var content = match.Groups["text"];
        var openingLength = bold ? 2 : 1;
        _editor.BeginChange();
        try
        {
            // The final delimiter is still in PreviewTextInput. Remove only the already typed delimiters.
            var closingStart = PointerAtTextOffset(paragraph.ContentStart, content.Index + content.Length);
            new TextRange(closingStart, _editor.CaretPosition).Text = "";
            var openingStart = PointerAtTextOffset(paragraph.ContentStart, match.Index);
            var openingEnd = PointerAtTextOffset(paragraph.ContentStart, match.Index + openingLength);
            new TextRange(openingStart, openingEnd).Text = "";
            var formattedStart = PointerAtTextOffset(paragraph.ContentStart, match.Index);
            var formattedEnd = PointerAtTextOffset(paragraph.ContentStart, match.Index + content.Length);
            new TextRange(formattedStart, formattedEnd).ApplyPropertyValue(
                bold ? TextElement.FontWeightProperty : TextElement.FontStyleProperty,
                bold ? (object)FontWeights.Bold : FontStyles.Italic);
            _editor.Selection.Select(formattedEnd, formattedEnd);
            _editor.Selection.ApplyPropertyValue(bold ? TextElement.FontWeightProperty : TextElement.FontStyleProperty,
                bold ? (object)paragraph.FontWeight : FontStyles.Normal);
        }
        finally { _editor.EndChange(); }
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_editing || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        BreakParagraph();
    }

    private void BreakParagraph(bool exitBlankList = true)
    {
        if (!_editing) return;
        var current = _editor.CaretPosition.Paragraph;
        if (exitBlankList && _editor.Selection.IsEmpty && current?.Parent is ListItem &&
            !HasText(current.Inlines))
        {
            SetParagraphKind("body");
            return;
        }
        _editor.BeginChange();
        try
        {
            _editor.Selection.Text = "";
            CollapseAtSelectionStart();
            current = _editor.CaretPosition.Paragraph;
            var item = current?.Parent as ListItem;
            var listAtLimit = item?.Parent is DocumentList { MarkerStyle: TextMarkerStyle.Decimal } numbered &&
                (long)numbered.StartIndex + numbered.ListItems.Count > 1000000;
            var next = _editor.CaretPosition.InsertParagraphBreak();
            var paragraph = next.Paragraph;
            // TextPointer supplies undoable native paragraph splitting. List items remain native too.
            if (paragraph is not null && paragraph != current && item?.Parent is DocumentList list && paragraph.Parent == item)
            {
                var following = new ListItem();
                list.ListItems.InsertAfter(item, following);
                item.Blocks.Remove(paragraph);
                following.Blocks.Add(paragraph);
                next = paragraph.ContentStart;
            }
            if (paragraph is not null && current is not null && HeadingLevel(current) > 0)
                ApplyParagraphStyle(paragraph, "body");
            _editor.Selection.Select(next, next);
            if (listAtLimit) SetParagraphKind("body");
            if (paragraph?.Parent is ListItem { Parent: DocumentList continuedList }) ApplyListLayout(continuedList);
        }
        finally { _editor.EndChange(); }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_inlineLayout || VisualTreeHelper.GetParent(this) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent, Source = this
        });
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        PasteTextData(e.SourceDataObject);
    }

    /// <summary>Shared by the real paste handler and checks; rich clipboard representations are never opened.</summary>
    internal bool PasteTextData(IDataObject data)
    {
        if (!_editing) return false;
        try { if (AttachmentPasteHandler?.Invoke(data) == true) return true; }
        catch (Exception error) when (error is System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        {
            InputRejected?.Invoke(new ImageInputException(error));
            return false;
        }
        string? text = null;
        if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: false)) text = data.GetData(DataFormats.UnicodeText, autoConvert: false) as string;
        else if (data.GetDataPresent(DataFormats.Text, autoConvert: false)) text = data.GetData(DataFormats.Text, autoConvert: false) as string;
        if (string.IsNullOrEmpty(text)) return false;
        return TryInsertMarkdown(text);
    }

    /// <summary>Insert the supported Markdown subset as native paragraphs and runs, retaining native Undo.</summary>
    public void InsertMarkdown(string text)
    {
        TryInsertMarkdown(text);
    }

    private bool TryInsertMarkdown(string text)
    {
        if (!_editing || string.IsNullOrEmpty(text)) return false;
        NoteContent content;
        try { content = MarkdownText.Parse(text); }
        catch (Exception error) when (error is System.IO.InvalidDataException or ArgumentException or OverflowException)
        {
            // Parse and validate before opening a native edit transaction or replacing the selection.
            InputRejected?.Invoke(error);
            return false;
        }
        _editor.BeginChange();
        try
        {
            _editor.Selection.Text = "";
            CollapseAtSelectionStart();
            for (var index = 0; index < content.Paragraphs.Count; index++)
            {
                if (index > 0) BreakParagraph(exitBlankList: false);
                var paragraph = content.Paragraphs[index];
                ApplyKindAtCaret(paragraph.Kind, paragraph.HeadingLevel, paragraph.Number);
                foreach (var item in paragraph.Runs)
                {
                    if (item.Text.Length == 0) continue;
                    var run = new Run(item.Text, _editor.CaretPosition)
                    {
                        FontWeight = item.Bold ? FontWeights.Bold : _editor.CaretPosition.Paragraph?.FontWeight ?? FontWeights.Normal,
                        FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
                        Foreground = item.Foreground is null ? AutomaticForeground : ColorBrush(item.Foreground),
                        Background = item.Background is null ? Brushes.Transparent : ColorBrush(item.Background)
                    };
                    _editor.Selection.Select(run.ContentEnd, run.ContentEnd);
                }
            }
        }
        finally { _editor.EndChange(); }
        _editor.Focus();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CollapseAtSelectionStart()
    {
        if (_editor.Document.Blocks.Count == 0)
        {
            // Deleting SelectAll removes the final paragraph too; create its native replacement before styling.
            var paragraph = new Paragraph();
            ApplyParagraphStyle(paragraph, "body");
            _editor.Document.Blocks.Add(paragraph);
            _editor.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
            return;
        }
        var insertion = _editor.Selection.Start.GetInsertionPosition(LogicalDirection.Forward);
        _editor.Selection.Select(insertion, insertion);
    }

    private void ApplyKindAtCaret(string kind, int level, int number)
    {
        if (kind is "bullet" or "ordered")
        {
            if (_editor.CaretPosition.Paragraph is { } paragraph) ApplyParagraphStyle(paragraph, "body");
            if ((kind == "ordered" && !IsNumbered) || (kind == "bullet" && !IsBulleted)) ToggleList(kind == "ordered");
            if (kind == "ordered" && _editor.CaretPosition.Paragraph?.Parent is ListItem { Parent: DocumentList list } item && list.ListItems.FirstListItem == item)
            {
                list.StartIndex = Math.Max(1, number);
                ApplyListLayout(list);
            }
        }
        else SetParagraphKind(kind is "heading" or "quote" ? kind : "body", level is >= 1 and <= 6 ? level : 2);
    }

    private static void RejectDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static FlowDocument BuildDocument(NoteContent content)
    {
        var document = new FlowDocument
        {
            FontFamily = AppTypography.Text, FontSize = 16, LineHeight = 24,
            Foreground = AutomaticForeground, Background = Brushes.Transparent, PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity, TextAlignment = TextAlignment.Left
        };
        DocumentList? list = null;
        var nextNumber = 1;
        foreach (var item in content.Paragraphs)
        {
            var paragraph = new Paragraph();
            ApplyParagraphStyle(paragraph, item.Kind, item.HeadingLevel);
            foreach (var itemRun in item.Runs)
            {
                var run = new Run(itemRun.Text);
                if (itemRun.Bold) run.FontWeight = FontWeights.Bold;
                if (itemRun.Italic) run.FontStyle = FontStyles.Italic;
                if (itemRun.Foreground is not null) run.Foreground = ColorBrush(itemRun.Foreground);
                if (itemRun.Background is not null) run.Background = ColorBrush(itemRun.Background);
                paragraph.Inlines.Add(run);
            }
            if (item.Kind is "bullet" or "ordered")
            {
                var marker = item.Kind == "ordered" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || list.MarkerStyle != marker || (marker == TextMarkerStyle.Decimal && item.Number != nextNumber))
                {
                    list = new DocumentList
                    {
                        MarkerStyle = marker, StartIndex = item.Kind == "ordered" ? item.Number : 1,
                    };
                    document.Blocks.Add(list);
                }
                paragraph.Margin = new Thickness(0, 0, 0, 5);
                list.ListItems.Add(new ListItem(paragraph) { Margin = new Thickness(0) });
                nextNumber = item.Number == int.MaxValue ? int.MaxValue : item.Number + 1;
            }
            else { list = null; document.Blocks.Add(paragraph); }
        }
        if (document.Blocks.Count == 0)
        {
            var paragraph = new Paragraph();
            ApplyParagraphStyle(paragraph, "body");
            document.Blocks.Add(paragraph);
        }
        foreach (var item in document.Blocks.OfType<DocumentList>()) ApplyListLayout(item);
        return document;
    }

    private static void ApplyListLayout(DocumentList list)
    {
        list.FontFamily = AppTypography.Text;
        list.FontSize = 16;
        list.FontWeight = FontWeights.Normal;
        list.Margin = new Thickness(0, 0, 0, 5);
        list.MarkerOffset = 6;
        var last = (long)list.StartIndex + Math.Max(0, list.ListItems.Count - 1);
        var marker = list.MarkerStyle == TextMarkerStyle.Decimal ? last.ToString(CultureInfo.InvariantCulture) + "." : "\u2022";
        var markerWidth = new FormattedText(marker, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(AppTypography.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            16, AutomaticForeground, 1).WidthIncludingTrailingWhitespace;
        // Markers hang into the list's own padding. A margin cannot protect that ink from the editor's clip.
        list.Padding = new Thickness(Math.Max(30, Math.Ceiling(markerWidth) + 10), 0, 0, 0);
    }

    private static void ApplyParagraphStyle(Paragraph paragraph, string kind, int level = 2)
    {
        var heading = kind == "heading";
        var quote = kind == "quote";
        var size = heading ? HeadingSizes[Math.Clamp(level, 1, 6) - 1] : 16;
        paragraph.FontSize = size;
        paragraph.FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal;
        paragraph.LineHeight = heading ? Math.Max(29, size * 1.4) : 24;
        paragraph.Margin = heading ? new Thickness(0, 8, 0, 6) : new Thickness(0, 0, 0, 5);
        paragraph.BorderThickness = quote ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        paragraph.BorderBrush = quote ? new SolidColorBrush(Color.FromRgb(210, 210, 210)) : null;
        paragraph.Padding = quote ? new Thickness(12, 0, 0, 0) : new Thickness(0);
    }

    private bool AllSelected(Func<Paragraph, bool> predicate)
    {
        var paragraphs = SelectedParagraphs().ToArray();
        return paragraphs.Length > 0 && paragraphs.All(predicate);
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        if (_editor.Selection.IsEmpty)
        {
            if (_editor.CaretPosition.Paragraph is { } paragraph) yield return paragraph;
            yield break;
        }
        foreach (var paragraph in Paragraphs(_editor.Document.Blocks))
            if (paragraph.ContentStart.CompareTo(_editor.Selection.End) < 0 && paragraph.ContentEnd.CompareTo(_editor.Selection.Start) >= 0)
                yield return paragraph;
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is DocumentList list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
            else if (block is Section section)
                foreach (var child in Paragraphs(section.Blocks)) yield return child;
        }
    }

    private static int HeadingLevel(Paragraph paragraph)
    {
        if (paragraph.FontWeight.ToOpenTypeWeight() < 600) return 0;
        for (var index = 0; index < HeadingSizes.Length; index++)
            if (Math.Abs(paragraph.FontSize - HeadingSizes[index]) < .01) return index + 1;
        return 0;
    }

    private static bool IsQuoteParagraph(Paragraph paragraph) => paragraph.BorderThickness.Left > 0;

    private static bool HasText(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Run run && !string.IsNullOrWhiteSpace(run.Text) || inline is Span span && HasText(span.Inlines)) return true;
        return false;
    }

    private static void ReadBlocks(BlockCollection blocks, List<NoteParagraph> output, string? listKind = null, int number = 1)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
            {
                var level = HeadingLevel(paragraph);
                var item = new NoteParagraph
                {
                    Kind = listKind ?? (level > 0 ? "heading" : IsQuoteParagraph(paragraph) ? "quote" : "body"),
                    HeadingLevel = level > 0 ? level : 2, Number = listKind == "ordered" ? number : 1
                };
                ReadInlines(paragraph.Inlines, item.Runs);
                output.Add(item);
            }
            else if (block is DocumentList list)
            {
                var index = list.StartIndex;
                foreach (var item in list.ListItems)
                {
                    ReadBlocks(item.Blocks, output, list.MarkerStyle == TextMarkerStyle.Decimal ? "ordered" : "bullet", index);
                    if (index < int.MaxValue) index++;
                }
            }
            else if (block is Section section) ReadBlocks(section.Blocks, output, listKind, number);
        }
    }

    private static void ReadInlines(InlineCollection inlines, List<NoteRun> output)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) AppendRun(output, run.Text, run.FontWeight.ToOpenTypeWeight() >= 700,
                run.FontStyle == FontStyles.Italic, ReadColor(run.Foreground, foreground: true), ReadColor(run.Background, foreground: false));
            else if (inline is LineBreak) AppendRun(output, "\n", false, false, null, null);
            else if (inline is Span span) ReadInlines(span.Inlines, output);
            // Embedded objects have no text-only representation.
        }
    }

    private static string? ReadColor(Brush? brush, bool foreground)
    {
        if (brush is not SolidColorBrush { Color: var color } || color.A == 0 || (foreground && ReferenceEquals(brush, AutomaticForeground))) return null;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush ColorBrush(string color) => new SolidColorBrush(Color.FromRgb(
        byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    private static void AppendRun(List<NoteRun> runs, string text, bool bold, bool italic, string? foreground, string? background)
    {
        if (text.Length == 0) return;
        var previous = runs.LastOrDefault();
        if (previous is not null && previous.Bold == bold && previous.Italic == italic && previous.Foreground == foreground && previous.Background == background)
            previous.Text += text;
        else runs.Add(new NoteRun { Text = text, Bold = bold, Italic = italic, Foreground = foreground, Background = background });
    }

    private static TextPointer PointerAtTextOffset(TextPointer start, int offset)
    {
        var pointer = start;
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= length) return pointer.GetPositionAtOffset(offset, LogicalDirection.Forward)!;
                offset -= length;
            }
            else if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart &&
                     pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak lineBreak)
            {
                if (offset == 0) return pointer;
                if (offset == 1) return lineBreak.ElementEnd;
                offset--;
            }
            var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null) return pointer;
            pointer = next;
        }
        return start;
    }

    private static string TextBetween(TextPointer start, TextPointer end)
    {
        // TextRange.Text adds native list markers; pointer offsets must count only document characters.
        var text = new StringBuilder();
        for (var pointer = start; pointer is not null && pointer.CompareTo(end) < 0;
             pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                text.Append(run.AsSpan(0, Math.Min(run.Length, pointer.GetOffsetToPosition(end))));
            }
            else if (context == TextPointerContext.ElementStart && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
                text.Append('\n');
        }
        return text.ToString();
    }
}

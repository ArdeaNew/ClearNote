using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Controlled async imports exercise the real UI continuation without a picker or visible window.</summary>
internal static class ImageImportLifecycleChecks
{
    public static void Run(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            ComposerImportPreservesDraft(root);
            FailedImportRestoresControls(root);
            InlineImportBlocksSaveAndNavigation(root);
            ClosingWaitsForImport(root);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void ComposerImportPreservesDraft(string root)
    {
        var store = Store(root, "composer");
        var image = Picture(store);
        var window = new MainWindow(store);
        try
        {
            var composer = Get<NoteEditor>(window, "Composer");
            var panel = Field<AttachmentPanel>(window, "_composerMedia");
            var states = new List<bool>();
            panel.ImportingChanged += (_, _) => states.Add(panel.IsImporting);
            composer.PlainText = "Draft before import";
            var completion = Pending();
            var import = StartImport(panel, () => completion.Task);
            Assert(panel.IsImporting && !panel.IsEnabled && NavigationButtons(window).All(button => !button.IsEnabled) &&
                   !Get<Button>(window, "PostButton").IsEnabled && !Get<FeedView>(window, "NotesFeed").IsEnabled,
                "A pending composer image must disable posting and actions that could replace or detach its draft.");
            var duplicateStarted = false;
            var duplicate = StartImport(panel, () => { duplicateStarted = true; return Task.FromResult<IReadOnlyList<NoteImage>>([image]); });
            Assert(duplicate.IsCompletedSuccessfully && !duplicateStarted,
                "A second import must not start while the same panel already owns a pending selection.");
            composer.PlainText = "Draft typed while importing";
            Assert(composer.TextBox.IsEnabled, "Importing pictures must leave the composer available for typing.");
            Invoke(window, "PostNote");
            var switched = (bool)Invoke(window, "SwitchCollection", NoteCollection.Memo, true)!;
            Assert(!switched && Field<NoteCollection>(window, "_collection") == NoteCollection.Notes && store.Cards.Count == 0 &&
                   composer.PlainText.Contains("typed while importing", StringComparison.Ordinal),
                "Direct post and navigation handlers must also guard pending imports, preserving text entered during the wait.");

            completion.SetResult([image]);
            Pump(import);
            Assert(!panel.IsImporting && panel.IsEnabled && panel.Count == 1 &&
                   states.SequenceEqual([true, false]) && NavigationButtons(window).All(button => button.IsEnabled) &&
                   Get<Button>(window, "PostButton").IsEnabled && Get<FeedView>(window, "NotesFeed").IsEnabled,
                "Successful import must attach once, publish balanced busy notifications and restore enabled controls.");
            Assert((bool)Invoke(window, "SaveDraft")!, "The completed image draft must use the normal durable save path.");
            var reopened = new LibraryStore(store.RootPath); reopened.Load();
            var draft = reopened.LoadDraft();
            Assert(draft is not null && draft.Content.Images.Single().AssetName == image.AssetName &&
                   draft.Content.Paragraphs.SelectMany(paragraph => paragraph.Runs).Any(run => run.Text.Contains("typed while importing", StringComparison.Ordinal)),
                "The image and text typed during decoding must reopen as one draft after the UI continuation completes.");
        }
        finally { window.Close(); }
    }

    private static void FailedImportRestoresControls(string root)
    {
        var store = Store(root, "failure");
        var image = Picture(store);
        var window = new MainWindow(store);
        try
        {
            var composer = Get<NoteEditor>(window, "Composer");
            var panel = Field<AttachmentPanel>(window, "_composerMedia");
            composer.PlainText = "Keep this existing draft";
            panel.AddImages([image]);
            var completion = Pending();
            var import = StartImport(panel, () => completion.Task);
            completion.SetException(new InvalidDataException("Synthetic decoder failure"));
            try { Pump(import); throw new InvalidOperationException("The synthetic import should fail."); }
            catch (InvalidDataException) { }
            Assert(!panel.IsImporting && panel.IsEnabled && panel.Capture().Single().AssetName == image.AssetName &&
                   composer.PlainText.Contains("Keep this existing draft", StringComparison.Ordinal) &&
                   NavigationButtons(window).All(button => button.IsEnabled) && Get<Button>(window, "PostButton").IsEnabled,
                "An import exception must restore controls without discarding existing text or attachments.");
            Assert((bool)Invoke(window, "SaveDraft")! && store.LoadDraft()!.Content.Images.Count == 1,
                "An import failure must leave the preceding draft saveable.");
        }
        finally { window.Close(); }
    }

    private static void InlineImportBlocksSaveAndNavigation(string root)
    {
        var store = Store(root, "inline");
        var note = new CardDocument { Note = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Saved note" }] }] } };
        store.Upsert(note);
        var image = Picture(store);
        var window = new MainWindow(store);
        try
        {
            var feed = Get<FeedView>(window, "NotesFeed");
            var card = feed.RenderedCards.Single();
            card.BeginEdit();
            card.Body.PlainText = "An edit waiting for its image";
            var saves = 0;
            var save = card.SaveNote!;
            card.SaveNote = (document, content) => { saves++; return save(document, content); };
            var completion = Pending();
            var import = StartImport(card.Attachments!, () => completion.Task);
            Assert(card.IsImportingImages && feed.IsImportingImages && !card.TrySave() && !feed.SavePendingEdits() && saves == 0,
                "An inline save or feed-wide save must not commit text before its pending image is attached.");
            card.CancelEdit();
            Assert(card.IsEditing && card.Body.PlainText.Contains("waiting for its image", StringComparison.Ordinal) &&
                   NavigationButtons(window).All(button => !button.IsEnabled),
                "Cancel and collection navigation must not detach an inline editor while its import owns the attachment target.");
            completion.SetResult([image]);
            Pump(import);
            Assert(!feed.IsImportingImages && card.Attachments!.Count == 1 && NavigationButtons(window).All(button => button.IsEnabled) &&
                   feed.SavePendingEdits() && saves == 1 && !card.IsEditing,
                "Completing the inline import must unblock exactly one normal save and restore feed controls.");
            var reopened = new LibraryStore(store.RootPath); reopened.Load();
            Assert(reopened.Cards.Single().Note!.Images.Single().AssetName == image.AssetName &&
                   NoteText.PlainText(reopened.Cards.Single()).Contains("waiting for its image", StringComparison.Ordinal),
                "The completed inline edit must persist its text and image together.");
        }
        finally { window.Close(); }
    }

    private static void ClosingWaitsForImport(string root)
    {
        var store = Store(root, "closing");
        var image = Picture(store);
        var window = new MainWindow(store);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            Get<NoteEditor>(window, "Composer").PlainText = "Save this when import completes";
            var panel = Field<AttachmentPanel>(window, "_composerMedia");
            var completion = Pending();
            var import = StartImport(panel, () => completion.Task);
            window.Close();
            Assert(!closed && panel.IsImporting, "Closing must defer until a pending import can attach and save its draft.");
            completion.SetResult([image]);
            Pump(import);
            Assert(closed, "The deferred close must finish automatically after its image import completes.");
            var reopened = new LibraryStore(store.RootPath); reopened.Load();
            var draft = reopened.LoadDraft();
            Assert(draft is not null && draft.Content.Images.Single().AssetName == image.AssetName &&
                   draft.Content.Paragraphs.SelectMany(paragraph => paragraph.Runs).Any(run => run.Text.Contains("Save this", StringComparison.Ordinal)),
                "A deferred close must durably save both the completed image and the original draft text.");
        }
        finally { if (!closed) window.Close(); }
    }

    private static TaskCompletionSource<IReadOnlyList<NoteImage>> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task StartImport(AttachmentPanel panel, Func<Task<IReadOnlyList<NoteImage>>> import) =>
        (Task)Invoke(panel, "ImportAsync", import)!;

    private static void Pump(Task operation)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
        var expired = false;
        timeout.Tick += (_, _) => { expired = true; frame.Continue = false; };
        timeout.Start();
        _ = operation.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false)), TaskScheduler.Default);
        try { if (!operation.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert(!expired && operation.IsCompleted, "The controlled image import must finish on the UI dispatcher.");
        var idle = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle.Continue = false));
        Dispatcher.PushFrame(idle);
        operation.GetAwaiter().GetResult();
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "image-import-lifecycle", name, "Data"));
        store.Load();
        return store;
    }
    private static NoteImage Picture(LibraryStore store) => store.ImportImage(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"), 1, 1);
    private static IEnumerable<Button> NavigationButtons(MainWindow window) =>
        new[] { "NotesButton", "ReviewButton", "MemoButton", "SearchButton", "MoreButton", "ImageButton" }.Select(name => Get<Button>(window, name));
    private static T Get<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

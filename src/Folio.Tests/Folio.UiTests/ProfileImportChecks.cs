using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Folio.App;
using Folio.App.Controls;
using Folio.Core;

/// <summary>Controlled avatar imports use the real dispatcher without a picker or visible window.</summary>
internal static class ProfileImportChecks
{
    public static void Run(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            SuccessKeepsCandidateUntilSave(root);
            FailureAndPickerCancelPreserveProfile(root);
            ClosingWaitsWithoutSaving(root, fail: false);
            ClosingWaitsWithoutSaving(root, fail: true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void SuccessKeepsCandidateUntilSave(string root)
    {
        var store = Store(root, "success");
        var original = Picture(store, 30);
        var replacement = Picture(store, 180);
        store.SaveProfile(new UserProfile { DisplayName = "Original", Avatar = original });
        var accepted = 0;
        var dialog = Dialog(store, profile => { store.SaveProfile(profile); accepted++; return true; });
        try
        {
            var events = new List<bool>();
            EventHandler changed = (_, _) => events.Add(IsImporting(dialog));
            dialog.GetType().GetEvent("ImportingChanged")!.AddEventHandler(dialog, changed);
            var completion = Pending();
            var import = StartImport(dialog, () => completion.Task);
            Assert(IsImporting(dialog) && !FindButton(dialog, "Choose picture").IsEnabled &&
                !FindButton(dialog, "Remove").IsEnabled && !FindButton(dialog, "Save").IsEnabled &&
                FindButton(dialog, "Cancel").IsEnabled && !(bool)Invoke(dialog, "TrySave")! && accepted == 0,
                "A pending avatar must disable replacement, removal and Save while retaining a way to request close.");
            var duplicateStarted = false;
            var duplicate = StartImport(dialog, () => { duplicateStarted = true; return Task.FromResult<IReadOnlyList<NoteImage>>([replacement]); });
            Assert(duplicate.IsCompletedSuccessfully && !duplicateStarted,
                "Choosing another avatar while importing must not start a second operation.");
            Name(dialog).Text = "Typed while importing";
            Assert(Name(dialog).IsEnabled, "The profile name must remain editable while a picture decodes.");
            completion.SetResult([replacement]);
            Pump(import);
            Assert(!IsImporting(dialog) && events.SequenceEqual([true, false]) &&
                FindButton(dialog, "Choose picture").IsEnabled && FindButton(dialog, "Remove").IsEnabled &&
                FindButton(dialog, "Save").IsEnabled && Name(dialog).Text == "Typed while importing" &&
                Avatar(dialog)?.AssetName == replacement.AssetName && accepted == 0,
                "A completed import must attach exactly one independent candidate and restore actions without saving automatically.");
            Assert(store.LoadProfile().DisplayName == "Original" && store.LoadProfile().Avatar?.AssetName == original.AssetName,
                "Import completion must not mutate the durable profile before Save.");
            replacement.PixelWidth = 99;
            Assert(Avatar(dialog)?.PixelWidth == 2 && (bool)Invoke(dialog, "TrySave")! && accepted == 1,
                "The dialog must own a copied image reference and accept one explicit save after import.");
            var reopened = new LibraryStore(store.RootPath); reopened.Load();
            var profile = reopened.LoadProfile();
            Assert(profile.DisplayName == "Typed while importing" && profile.Avatar?.AssetName == replacement.AssetName,
                "The completed avatar and name must persist together after the accepted save.");
        }
        finally { dialog.Close(); }
    }

    private static void FailureAndPickerCancelPreserveProfile(string root)
    {
        var store = Store(root, "failure");
        var original = Picture(store, 60);
        store.SaveProfile(new UserProfile { DisplayName = "Original", Avatar = original });
        var profilePath = Path.Combine(store.RootPath, "profile.json");
        var before = File.ReadAllBytes(profilePath);
        var accepted = 0;
        var dialog = Dialog(store, _ => { accepted++; return true; });
        try
        {
            Name(dialog).Text = "Keep this proposed name";
            var completion = Pending();
            var import = StartImport(dialog, () => completion.Task);
            completion.SetException(new InvalidDataException("Synthetic avatar decoder failure"));
            Pump(import);
            Assert(!IsImporting(dialog) && FindButton(dialog, "Choose picture").IsEnabled && FindButton(dialog, "Save").IsEnabled &&
                Avatar(dialog)?.AssetName == original.AssetName && Name(dialog).Text == "Keep this proposed name" &&
                Field<TextBlock>(dialog, "_status").Visibility == Visibility.Visible && accepted == 0 &&
                File.ReadAllBytes(profilePath).SequenceEqual(before),
                "A failed avatar import must explain the error, restore actions and retain the previous candidate, proposed name and saved profile.");
            var canceled = Pending();
            import = StartImport(dialog, () => canceled.Task);
            canceled.SetResult([]);
            Pump(import);
            Assert(!IsImporting(dialog) && Avatar(dialog)?.AssetName == original.AssetName &&
                Name(dialog).Text == "Keep this proposed name" && accepted == 0 &&
                File.ReadAllBytes(profilePath).SequenceEqual(before),
                "Canceling the picture picker must preserve the proposed profile and must not save or clear its picture.");
        }
        finally { dialog.Close(); }

        dialog = Dialog(store, _ => false);
        try
        {
            Invoke(dialog, "SetAvatar", new object?[] { null });
            Assert(!FindButton(dialog, "Remove").IsEnabled && FindButton(dialog, "Choose picture").IsEnabled,
                "Remove must be disabled when there is no profile picture to remove.");
        }
        finally { dialog.Close(); }
    }

    private static void ClosingWaitsWithoutSaving(string root, bool fail)
    {
        var store = Store(root, fail ? "close-failure" : "close-success");
        var original = Picture(store, 90);
        var replacement = Picture(store, 210);
        store.SaveProfile(new UserProfile { DisplayName = "Original", Avatar = original });
        var before = File.ReadAllBytes(Path.Combine(store.RootPath, "profile.json"));
        var accepted = 0;
        var dialog = Dialog(store, _ => { accepted++; return true; });
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            Name(dialog).Text = "Discard this proposed profile";
            var completion = Pending();
            var import = StartImport(dialog, () => completion.Task);
            FindButton(dialog, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!closed && IsImporting(dialog) && !FindButton(dialog, "Cancel").IsEnabled &&
                !(bool)Invoke(dialog, "TrySave")! && accepted == 0,
                "Cancel during decoding must defer close, reject Save and avoid detaching a running import.");
            if (fail) completion.SetException(new InvalidDataException("Synthetic closing import failure"));
            else completion.SetResult([replacement]);
            Pump(import);
            Assert(closed && !IsImporting(dialog) && accepted == 0 &&
                File.ReadAllBytes(Path.Combine(store.RootPath, "profile.json")).SequenceEqual(before),
                "A deferred Cancel must close after success or failure without saving a proposed name or avatar.");
            var reopened = new LibraryStore(store.RootPath); reopened.Load();
            Assert(reopened.LoadProfile().DisplayName == "Original" && reopened.LoadProfile().Avatar?.AssetName == original.AssetName,
                "Closing during an avatar import must preserve the independent saved identity.");
        }
        finally { if (!closed) dialog.Close(); }
    }

    private static TaskCompletionSource<IReadOnlyList<NoteImage>> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task StartImport(Window dialog, Func<Task<IReadOnlyList<NoteImage>>> import) => (Task)Invoke(dialog, "ImportPictureAsync", import)!;
    private static bool IsImporting(Window dialog) => (bool)dialog.GetType().GetProperty("IsImporting")!.GetValue(dialog)!;
    private static NoteImage? Avatar(Window dialog) => Field<NoteImage?>(dialog, "_avatarImage");
    private static TextBox Name(Window dialog) => (TextBox)dialog.GetType().GetProperty("NameInput")!.GetValue(dialog)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] values) =>
        target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
    private static Window Dialog(LibraryStore store, Func<UserProfile, bool> save) =>
        (Window)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Folio.App.ProfileDialog")!, store, store.LoadProfile(), save)!;
    private static Button FindButton(Window dialog, string content) =>
        Descendants((DependencyObject)dialog.Content).OfType<Button>().Single(button => Equals(button.Content, content));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "profile-import", name, "Data"));
        store.Load();
        return store;
    }
    private static NoteImage Picture(LibraryStore store, byte value) => ImageAttachments.ImportBitmap(
        BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, 120, 140, 255, 160, value, 200, 128 }, 8), store);
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
        Assert(!expired && operation.IsCompleted, "The controlled avatar import must complete on the UI dispatcher.");
        var idle = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle.Continue = false));
        Dispatcher.PushFrame(idle);
        operation.GetAwaiter().GetResult();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

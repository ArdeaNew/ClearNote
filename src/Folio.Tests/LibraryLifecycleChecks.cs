using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Folio.Core;

/// <summary>Lifecycle checks create independent synthetic libraries and generated local image copies.</summary>
internal static class LibraryLifecycleChecks
{
    public static void FirstRun(string root)
    {
        var store = new LibraryStore(Path.Combine(root, "initialization"));
        Assert(!store.IsNewLibrary, "A store which has not loaded must not report first-run readiness.");
        Throws<InvalidOperationException>(() => store.InitializeNewLibrary([]), "Initialization must require a successful load.");
        store.Load();
        var first = Card("First welcome");
        var second = Card("Second welcome");
        var invalid = Card("Duplicate"); invalid.Id = first.Id;
        Throws<InvalidDataException>(() => store.InitializeNewLibrary([first, invalid]), "The entire initial collection must validate before any save.");
        Assert(store.IsNewLibrary && !File.Exists(Path.Combine(store.RootPath, "library.json")) &&
            !File.Exists(Path.Combine(store.RootPath, "library.previous.json")), "Failed validation cannot create a partially initialized collection.");
        Assert(store.InitializeNewLibrary([first, second]) && store.Cards.Count == 2 && !store.IsNewLibrary,
            "One successful initialization must save both ordinary notes.");
        Assert(!store.InitializeNewLibrary([Card("Do not append")]) && store.Cards.Count == 2, "A second call must be a no-op.");
        var initial = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
        Assert(initial.SequenceEqual(File.ReadAllBytes(Path.Combine(store.RootPath, "library.previous.json"))),
            "The first complete collection must have an identical usable recovery copy.");
        store.Delete(first.Id); store.Delete(second.Id);
        var reopened = Reopen(store);
        Assert(!reopened.IsNewLibrary && !reopened.InitializeNewLibrary([first, second]) && reopened.Cards.Count == 0,
            "An intentionally empty saved library must never receive welcome notes again.");
        var empty = Store(root, "explicit-empty-initialization");
        Assert(empty.InitializeNewLibrary([]) && !Reopen(empty).IsNewLibrary, "An explicitly saved empty collection also ends first-run status.");
        var obstructed = Store(root, "obstructed-initialization");
        var previous = Path.Combine(obstructed.RootPath, "library.previous.json");
        Directory.CreateDirectory(previous);
        Throws<UnauthorizedAccessException>(() => obstructed.InitializeNewLibrary([first, second]), "A folder occupying the Windows recovery-file path must report access failure before initialization succeeds.");
        Assert(!File.Exists(Path.Combine(obstructed.RootPath, "library.json")) && obstructed.Cards.Count == 0,
            "A failed first save must not publish either one of the two notes.");
        Directory.Delete(previous);
        Assert(obstructed.InitializeNewLibrary([first, second]), "A temporary initialization failure must permit retry.");
    }

    public static void DeleteExclusiveImages(string root)
    {
        var store = Store(root, "delete-exclusive");
        var original = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "original-picture.png");
        File.WriteAllBytes(original, Png());
        var sourceHash = SHA256.HashData(File.ReadAllBytes(original));
        var image = store.ImportImage(File.ReadAllBytes(original), 1, 1);
        var owned = store.GetImagePath(image);
        var removed = Card("A post with a picture", image);
        var remaining = Card("A post to retain");
        store.Upsert(removed); store.Upsert(remaining);
        var backup = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "explicit-before-delete.zip");
        store.Backup(backup);
        var backupHash = SHA256.HashData(File.ReadAllBytes(backup));
        store.Delete(removed.Id);
        Assert(store.Cards.Single().Id == remaining.Id && !File.Exists(owned), "Delete must remove the post and its dedicated managed image.");
        foreach (var name in new[] { "library.json", "library.previous.json" })
        {
            var json = File.ReadAllText(Path.Combine(store.RootPath, name));
            Assert(!json.Contains(removed.Id, StringComparison.Ordinal) && !json.Contains(image.AssetName, StringComparison.Ordinal),
                "Live and recovery data must both forget the deleted post and image reference.");
        }
        Assert(!File.Exists(Path.Combine(store.RootPath, "delete.pending.json")) &&
            !Directory.EnumerateFiles(store.RootPath, "*.tmp", SearchOption.AllDirectories).Any(), "A successful deletion must leave no journal or temporary copy.");
        Assert(SHA256.HashData(File.ReadAllBytes(original)).SequenceEqual(sourceHash) &&
            SHA256.HashData(File.ReadAllBytes(backup)).SequenceEqual(backupHash), "Deletion must not alter the uploaded original or an explicit manual backup.");
        using (var archive = ZipFile.OpenRead(backup))
            Assert(archive.GetEntry("assets/" + image.AssetName) is not null, "An explicit snapshot remains independently restorable.");
        File.WriteAllText(Path.Combine(store.RootPath, "library.json"), "{ damaged primary after deletion");
        var recovered = Reopen(store);
        Assert(recovered.Cards.Single().Id == remaining.Id && recovered.RecoveryNotice is not null,
            "Recovering after deletion must retain other posts without resurrecting a removed one.");
        recovered.Delete(removed.Id);
        Assert(recovered.Cards.Single().Id == remaining.Id, "Repeated deletion of an absent post must be harmless.");
    }

    public static void KeepSharedImages(string root)
    {
        var shared = Store(root, "delete-shared");
        var image = shared.ImportImage(Png(), 1, 1);
        var path = shared.GetImagePath(image);
        var first = Card("First owner", image); var second = Card("Second owner", image);
        shared.Upsert(first); shared.Upsert(second);
        shared.Delete(first.Id);
        Assert(File.Exists(path) && Reopen(shared).Cards.Single().Id == second.Id, "A picture shared by another saved post must remain usable.");
        shared.Delete(second.Id);
        Assert(!File.Exists(path), "Deleting the final saved owner must remove an otherwise unowned picture.");

        foreach (var owner in new[] { "profile", "draft", "previous-draft" })
        {
            var store = Store(root, "delete-retain-" + owner);
            image = store.ImportImage(Png(), 1, 1); path = store.GetImagePath(image);
            var post = Card("Deleted post", image); store.Upsert(post);
            if (owner == "profile") store.SaveProfile(new UserProfile { DisplayName = "Owner", Avatar = image });
            else
            {
                var draft = new ComposerDraft { Content = new NoteContent { Images = [image] } };
                store.SaveDraft(draft);
                if (owner == "previous-draft")
                {
                    draft.Content = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Newer draft text" }] }] };
                    store.SaveDraft(draft);
                }
            }
            store.Delete(post.Id);
            Assert(File.Exists(path) && Reopen(store).Cards.Count == 0, "A picture referenced by the " + owner + " must outlive a deleted post.");
            if (owner == "profile") Assert(Reopen(store).LoadProfile().Avatar!.AssetName == image.AssetName, "The avatar must reopen unchanged.");
            if (owner == "draft") Assert(Reopen(store).LoadDraft()!.Content.Images.Single().AssetName == image.AssetName, "The image-only draft must reopen unchanged.");
            if (owner == "previous-draft")
            {
                File.Delete(Path.Combine(store.RootPath, "draft.json"));
                Assert(Reopen(store).LoadDraft()!.Content.Images.Single().AssetName == image.AssetName,
                    "The previous draft's picture must remain available for actual recovery.");
            }
        }
    }

    public static void DeletionFailures(string root)
    {
        var store = Store(root, "delete-access-failures");
        var image = store.ImportImage(Png(), 1, 1);
        var path = store.GetImagePath(image);
        var card = Card("Retain on failed delete", image); store.Upsert(card);
        var primary = Path.Combine(store.RootPath, "library.json");
        var previous = Path.Combine(store.RootPath, "library.previous.json");
        var original = File.ReadAllBytes(primary); var old = File.ReadAllBytes(previous);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Throws<IOException>(() => store.Delete(card.Id), "A locked picture must fail before the note is removed.");
        Assert(File.ReadAllBytes(primary).SequenceEqual(original) && File.ReadAllBytes(previous).SequenceEqual(old) &&
            store.Cards.Single().Id == card.Id && !File.Exists(Path.Combine(store.RootPath, "delete.pending.json")),
            "A preflight access failure cannot change either library snapshot or create a pending deletion.");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try { Throws<UnauthorizedAccessException>(() => store.Delete(card.Id), "A read-only image must not be silently retained after a reported success."); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        File.Delete(previous); Directory.CreateDirectory(previous);
        Throws<IOException>(() => store.Delete(card.Id), "A blocked recovery path must be rejected before note or image deletion.");
        Assert(File.ReadAllBytes(primary).SequenceEqual(original) && File.Exists(path), "A blocked recovery file must leave the live post and picture intact.");
        Directory.Delete(previous); File.WriteAllBytes(previous, old);
        store.Delete(card.Id);
        Assert(Reopen(store).Cards.Count == 0 && !File.Exists(path), "Resolving a temporary access failure must allow a complete retry.");

        var damagedDraft = Store(root, "delete-with-damaged-draft");
        image = damagedDraft.ImportImage(Png(), 1, 1);
        card = Card("Keep while draft ownership is unknown", image); damagedDraft.Upsert(card);
        File.WriteAllText(Path.Combine(damagedDraft.RootPath, "draft.json"), "{ unknown picture references");
        Throws<Exception>(() => damagedDraft.Delete(card.Id), "Unknown draft ownership must block destructive picture cleanup.");
        Assert(damagedDraft.Cards.Count == 1 && File.Exists(damagedDraft.GetImagePath(image)), "A damaged draft must not cause a shared picture to be lost.");
        var plain = Card("A text-only post"); damagedDraft.Upsert(plain); damagedDraft.Delete(plain.Id);
        Assert(damagedDraft.Cards.Single().Id == card.Id, "Unrelated draft corruption must not prevent deleting text-only posts.");
    }

    public static void InterruptedDeletion(string root)
    {
        foreach (var state in new[] { "intent", "missing-picture", "blocked-cleanup" })
        {
            var store = Store(root, "delete-interrupted-" + state);
            var image = store.ImportImage(Png(), 1, 1);
            var path = store.GetImagePath(image);
            var removed = Card("Interrupted deletion", image); var remaining = Card("Still here");
            store.Upsert(remaining); store.Upsert(removed);
            // Force both old snapshots to contain the removed post, as a crash may leave them.
            File.Copy(Path.Combine(store.RootPath, "library.json"), Path.Combine(store.RootPath, "library.previous.json"), true);
            File.WriteAllText(Path.Combine(store.RootPath, "delete.pending.json"), JsonSerializer.Serialize(new
            {
                version = 1, cardId = removed.Id, assets = new[] { image.AssetName }
            }));
            if (state == "missing-picture")
            {
                File.Delete(path);
                File.WriteAllText(Path.Combine(store.RootPath, "library.json"), "{ interrupted damaged primary");
            }
            if (state == "blocked-cleanup")
            {
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws<IOException>(() => Reopen(store), "An unfinished deletion must report cleanup failure while retaining its retry journal.");
                Assert(File.Exists(Path.Combine(store.RootPath, "delete.pending.json")), "Failed cleanup must preserve its durable retry intent.");
            }
            var reopened = Reopen(store);
            Assert(reopened.Cards.Single().Id == remaining.Id && !File.Exists(path) &&
                !File.Exists(Path.Combine(store.RootPath, "delete.pending.json")), "Reopening must finish an interrupted deletion without losing other notes.");
            File.WriteAllText(Path.Combine(store.RootPath, "library.json"), "{ later primary corruption");
            Assert(Reopen(store).Cards.Single().Id == remaining.Id, "Even recovery after cleanup must never resurrect the deleted content.");
        }
    }

    public static void BackupPendingDeletion(string root)
    {
        foreach (var lockedCleanup in new[] { false, true })
        {
            var suffix = lockedCleanup ? "blocked" : "ready";
            var store = Store(root, "backup-pending-delete-" + suffix);
            var image = store.ImportImage(Png(), 1, 1);
            var imagePath = store.GetImagePath(image);
            var removed = Card("Delete before export", image); var remaining = Card("Export this note");
            store.Upsert(remaining); store.Upsert(removed);
            var backup = Path.Combine(root, "pending-delete-" + suffix + ".zip");
            store.Backup(backup);
            var oldArchive = File.ReadAllBytes(backup);
            // Simulate an interruption after the durable intent but before either library
            // snapshot or in-memory collection changed. Export must honor that intent.
            var journal = Path.Combine(store.RootPath, "delete.pending.json");
            File.WriteAllText(journal, JsonSerializer.Serialize(new
            {
                version = 1, cardId = removed.Id, assets = new[] { image.AssetName }
            }));
            if (lockedCleanup)
            {
                using (var locked = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws<IOException>(() => store.Backup(backup), "Unfinished locked-picture cleanup must prevent backup publication.");
                Assert(File.ReadAllBytes(backup).SequenceEqual(oldArchive) && File.Exists(journal),
                    "A blocked deletion cannot replace an existing archive or discard the retry intent.");
            }
            store.Backup(backup);
            Assert(store.Cards.Single().Id == remaining.Id && !File.Exists(imagePath) && !File.Exists(journal),
                "Backup must finish the durable deletion before exporting the current collection.");
            using (var archive = ZipFile.OpenRead(backup))
            {
                Assert(archive.GetEntry("assets/" + image.AssetName) is null, "A new archive cannot retain the pending-deleted picture.");
                using var json = new StreamReader(archive.GetEntry("library.json")!.Open());
                Assert(!json.ReadToEnd().Contains(removed.Id, StringComparison.Ordinal), "A new archive cannot serialize the pending-deleted note.");
            }
            var restored = Store(root, "restore-after-pending-delete-" + suffix);
            restored.Restore(backup);
            Assert(restored.Cards.Single().Id == remaining.Id,
                "Restoring an archive created after a pending deletion must never resurrect the removed post.");
        }
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, name)); store.Load(); return store;
    }
    private static LibraryStore Reopen(LibraryStore original)
    {
        var store = new LibraryStore(original.RootPath); store.Load(); return store;
    }
    private static CardDocument Card(string text, NoteImage? image = null) => new()
    {
        Note = new NoteContent
        {
            Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }],
            Images = image is null ? [] : [image]
        }
    };
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException(message);
    }
}

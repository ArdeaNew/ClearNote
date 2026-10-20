using System.Text.Json;
using System.Text.Json.Nodes;
using Folio.Core;

internal static class MemoChecks
{
    public static void Run(string root)
    {
        PersistenceAndLegacy(root);
        DraftIsolationAndRecovery(root);
        SharedImages(root);
        ReviewBoundary(root);
    }

    private static void PersistenceAndLegacy(string root)
    {
        var store = Store(root, "roundtrip");
        var note = Card("A learning idea #learn");
        ReviewScheduler.RecordBrowse(note, DateTimeOffset.Now);
        var memo = Card("An address #home", NoteCollection.Memo);
        store.Upsert(note); store.Upsert(memo);
        store.SaveProfile(new UserProfile { DisplayName = "Avery" });
        var noteDraft = Draft("Unsent learning");
        var memoDraft = Draft("Unsent address");
        store.SaveDraft(noteDraft);
        store.SaveDraft(memoDraft, NoteCollection.Memo);
        store = Reopen(store);
        Assert(store.Cards.Single(card => card.Collection == NoteCollection.Notes).Reviews.Count == 1 &&
            store.Cards.Single(card => card.Collection == NoteCollection.Memo).Id == memo.Id &&
            store.LoadDraft()!.CardId == noteDraft.CardId && store.LoadDraft(NoteCollection.Memo)!.CardId == memoDraft.CardId,
            "Both streams, history and independent drafts must survive restart.");
        var backup = Path.Combine(root, "memo-roundtrip.zip");
        store.Backup(backup);
        var restored = Store(root, "restored");
        restored.Restore(backup);
        Assert(JsonSerializer.Serialize(restored.Cards) == JsonSerializer.Serialize(store.Cards) &&
            restored.LoadDraft()!.CardId == noteDraft.CardId && restored.LoadDraft(NoteCollection.Memo)!.CardId == memoDraft.CardId &&
            restored.LoadProfile().DisplayName == "Avery", "Backup must retain both streams, drafts, history and shared profile.");

        var legacy = Store(root, "legacy");
        legacy.Upsert(note);
        var path = Path.Combine(legacy.RootPath, "library.json");
        var payload = JsonNode.Parse(File.ReadAllText(path))!;
        payload["version"] = 4;
        payload["cards"]![0]!.AsObject().Remove("collection");
        File.WriteAllText(path, payload.ToJsonString());
        legacy = Reopen(legacy);
        Assert(legacy.Cards.Single().Collection == NoteCollection.Notes && legacy.Cards.Single().Reviews.Count == 1,
            "Libraries without a discriminator must retain Notes and all previous memory history.");
        var old = File.ReadAllBytes(path);
        var invalid = legacy.Cards.Single(); invalid.Collection = (NoteCollection)99;
        Throws<InvalidDataException>(() => legacy.Upsert(invalid));
        Assert(File.ReadAllBytes(path).SequenceEqual(old), "Invalid collection values cannot replace a healthy library.");
        invalid.Collection = NoteCollection.Memo;
        Throws<InvalidOperationException>(() => legacy.Upsert(invalid));
    }

    private static void DraftIsolationAndRecovery(string root)
    {
        var store = Store(root, "draft-isolation");
        var note = Draft("Learning draft");
        var memo = Draft("Address draft");
        store.SaveDraft(note); store.SaveDraft(memo, NoteCollection.Memo);
        File.WriteAllText(Path.Combine(store.RootPath, "draft.json"), "damaged");
        Assert(store.LoadDraft()!.CardId == note.CardId && store.DraftSaveBlocked &&
            !store.IsDraftSaveBlocked(NoteCollection.Memo), "Notes recovery must not disable Memo draft saving.");
        memo.Content = Content("A revised address");
        store.SaveDraft(memo, NoteCollection.Memo);
        Throws<InvalidDataException>(() => store.SaveDraft(note));
        Assert(store.LoadDraft(NoteCollection.Memo)!.CardId == memo.CardId && store.DraftSaveBlocked,
            "Loading the other stream cannot clear a recovery flag.");
        var backup = Path.Combine(root, "memo-recovered-drafts.zip");
        store.Backup(backup);
        var restored = Store(root, "recovered-drafts");
        restored.Restore(backup);
        Assert(restored.LoadDraft()!.CardId == note.CardId && restored.LoadDraft(NoteCollection.Memo)!.CardId == memo.CardId,
            "Backup must include a healthy recovery draft and the other stream's current draft independently.");
        var external = Draft("External address");
        var writer = Reopen(restored); writer.SaveDraft(external, NoteCollection.Memo);
        Throws<InvalidOperationException>(() => restored.SaveDraft(memo, NoteCollection.Memo));
        restored.SaveDraft(Draft("Notes remain available"));
        Assert(Reopen(restored).LoadDraft(NoteCollection.Memo)!.CardId == external.CardId,
            "A stale Memo composer cannot overwrite an external draft, while Notes stays writable.");

        var posted = Card("Posted address", NoteCollection.Memo); posted.Id = external.CardId;
        writer = Reopen(restored); writer.Upsert(posted);
        writer = Reopen(writer);
        Assert(writer.LoadDraft(NoteCollection.Memo) is null && writer.LoadDraft() is not null,
            "A crash between Memo posting and draft clearing cannot duplicate that post or clear the Notes draft.");
    }

    private static void SharedImages(string root)
    {
        var store = Store(root, "shared-images");
        var image = store.ImportImage(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"), 1, 1);
        var path = store.GetImagePath(image);
        var note = Card("Learning picture"); note.Note!.Images = [image];
        var memo = Card("Reference picture", NoteCollection.Memo); memo.Note!.Images = [image];
        store.Upsert(note); store.Upsert(memo);
        store.Delete(note.Id);
        Assert(File.Exists(path) && store.Cards.Single().Collection == NoteCollection.Memo,
            "Deleting a learning note cannot remove a picture owned by a Memo post.");
        var draft = Draft("Memo draft owns image"); draft.Content.Images = [image];
        store.SaveDraft(draft, NoteCollection.Memo);
        draft.Content = Content("Current memo without image");
        store.SaveDraft(draft, NoteCollection.Memo);
        store.Delete(memo.Id);
        Assert(File.Exists(path) && store.LoadDraft(NoteCollection.Memo) is not null,
            "A recoverable previous Memo draft must retain its images after a post is deleted.");
        var last = Card("Final owner"); last.Note!.Images = [image];
        store.Upsert(last);
        store.ClearDraft(NoteCollection.Memo);
        store.Delete(last.Id);
        Assert(!File.Exists(path) && Reopen(store).Cards.Count == 0,
            "Clearing both Memo draft versions releases a dedicated image when its final post is deleted.");
    }

    private static void ReviewBoundary(string root)
    {
        var memo = Card("Everyday reference", NoteCollection.Memo);
        var now = DateTimeOffset.Now;
        Assert(ReviewScheduler.Pick([memo], new HashSet<string>(), now) is null,
            "Scheduler selection must exclude Memo even outside the UI.");
        Throws<InvalidOperationException>(() => ReviewScheduler.RecordBrowse(memo, now));
        Assert(memo.Reviews.Count == 0 && memo.Memory.Repetitions == 0, "Memo browsing cannot mutate memory history.");
        var note = Card("Learning");
        Assert(ReviewScheduler.Pick([memo, note], new HashSet<string>(), now)?.Id == note.Id,
            "Memo cannot compete with Notes in a review pool.");
    }

    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, "memo-" + name)); store.Load(); return store;
    }
    private static LibraryStore Reopen(LibraryStore store) { var copy = new LibraryStore(store.RootPath); copy.Load(); return copy; }
    private static CardDocument Card(string text, NoteCollection collection = NoteCollection.Notes) => new() { Collection = collection, Note = Content(text) };
    private static ComposerDraft Draft(string text) => new() { Content = Content(text) };
    private static NoteContent Content(string text) => new() { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] };
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Folio.Core;

/// <summary>All records and pictures in these lifecycle checks are synthetic.</summary>
internal static class CollectionChecks
{
    private static readonly NoteCollection[] Collections = Enum.GetValues<NoteCollection>();
    private static readonly Dictionary<NoteCollection, string> DraftFiles = new()
    {
        [NoteCollection.Notes] = "draft.json",
        [NoteCollection.Memo] = "memo-draft.json",
        [NoteCollection.Opinions] = "opinions-draft.json",
        [NoteCollection.EconomicAnalysis] = "economic-analysis-draft.json"
    };
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    public static void Persistence(string root)
    {
        Assert((int)NoteCollection.Notes == 0 && (int)NoteCollection.Memo == 1 &&
            (int)NoteCollection.Opinions == 2 && (int)NoteCollection.EconomicAnalysis == 3,
            "Persisted collection identities must retain their original numeric values.");
        var store = Store(root, "roundtrip");
        var image = Picture(store);
        var drafts = new Dictionary<NoteCollection, ComposerDraft>();
        foreach (var collection in Collections)
        {
            var card = Card(collection + " #shared", collection, image);
            if (collection == NoteCollection.Notes) ReviewScheduler.RecordBrowse(card, Now);
            store.Upsert(card);
            drafts[collection] = Draft("Unsent " + collection, image);
            store.SaveDraft(drafts[collection], collection);
        }
        store.SaveProfile(new UserProfile { DisplayName = "Avery", Avatar = image });
        store = Reopen(store);
        foreach (var collection in Collections)
        {
            Assert(store.Cards.Count(card => card.Collection == collection) == 1 &&
                store.LoadDraft(collection)!.CardId == drafts[collection].CardId &&
                File.Exists(Path.Combine(store.RootPath, DraftFiles[collection])) &&
                File.Exists(Path.Combine(store.RootPath, PreviousDraftFile(collection))),
                "Each feed must reopen its own post and its own current and recovery draft files.");
            var post = store.Cards.Single(card => card.Collection == collection);
            Assert(NoteTags.Matches(post, "shared") && NoteText.PlainText(post).Contains(collection.ToString(), StringComparison.Ordinal),
                "Tags and visible text remain available in every independent feed.");
            if (collection != NoteCollection.Notes)
            {
                var before = JsonSerializer.Serialize(post);
                Throws<InvalidOperationException>(() => ReviewScheduler.RecordBrowse(post, Now));
                Throws<InvalidOperationException>(() => ReviewScheduler.Apply(post, ReviewRating.Good, Now));
                Assert(JsonSerializer.Serialize(post) == before,
                    "Reference-feed browsing and rejected direct reviews must not change content or memory history.");
            }
            foreach (var destination in Collections.Where(other => other != collection))
            {
                var moved = store.Cards.Single(card => card.Collection == collection);
                moved.Collection = destination;
                Throws<InvalidOperationException>(() => store.Upsert(moved));
            }
        }
        Assert(ReviewScheduler.Pick(store.Cards, new HashSet<string>(), Now)?.Collection == NoteCollection.Notes &&
            ReviewScheduler.Pick(store.Cards.Where(card => card.Collection != NoteCollection.Notes), new HashSet<string>(), Now) is null,
            "Only Notes may enter Review, including a pool containing all three reference feeds.");
        var backup = Path.Combine(root, "collections-roundtrip.zip");
        store.Backup(backup);
        using (var archive = ZipFile.OpenRead(backup))
            Assert(DraftFiles.Values.All(name => archive.GetEntry(name) is not null), "A backup must contain all four unsent feeds.");
        var restored = Store(root, "roundtrip-restored");
        restored.Restore(backup);
        Assert(JsonSerializer.Serialize(restored.Cards) == JsonSerializer.Serialize(store.Cards) &&
            restored.LoadProfile().DisplayName == "Avery" && restored.LoadProfile().Avatar!.AssetName == image.AssetName,
            "Restore preserves all posts, creation dates, review history and the shared identity.");
        foreach (var collection in Collections)
            Assert(restored.LoadDraft(collection)!.CardId == drafts[collection].CardId &&
                restored.GetImagePath(restored.LoadDraft(collection)!.Content.Images.Single()).EndsWith(image.AssetName, StringComparison.Ordinal),
                "Restored drafts retain independent stable identities and copied pictures.");

        var invalid = Card("Unsupported collection", (NoteCollection)99);
        var beforeInvalid = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
        Throws<InvalidDataException>(() => store.Upsert(invalid));
        Throws<ArgumentOutOfRangeException>(() => store.SaveDraft(Draft("Unsupported"), (NoteCollection)99));
        Assert(File.ReadAllBytes(Path.Combine(store.RootPath, "library.json")).SequenceEqual(beforeInvalid),
            "Unknown collections cannot overwrite saved posts.");

        // Optional draft content is validated before the healthy target directory is exchanged.
        var damaged = Path.Combine(root, "collections-invalid-draft.zip");
        RewriteArchive(backup, damaged, "economic-analysis-draft.json", "{not a draft"u8.ToArray());
        var targetBefore = File.ReadAllBytes(Path.Combine(restored.RootPath, "library.json"));
        Throws<JsonException>(() => restored.Restore(damaged));
        Assert(File.ReadAllBytes(Path.Combine(restored.RootPath, "library.json")).SequenceEqual(targetBefore) &&
            restored.LoadDraft(NoteCollection.Opinions)!.CardId == drafts[NoteCollection.Opinions].CardId,
            "A malformed new-feed draft cannot replace an otherwise healthy restore target.");
    }

    public static void DraftRecovery(string root)
    {
        foreach (var damagedCollection in Collections)
        {
            var store = Store(root, "recovery-" + damagedCollection);
            var drafts = Collections.ToDictionary(collection => collection, collection => Draft("Draft " + collection));
            foreach (var collection in Collections) store.SaveDraft(drafts[collection], collection);
            var damagedPath = Path.Combine(store.RootPath, DraftFiles[damagedCollection]);
            File.WriteAllText(damagedPath, "{ damaged original");
            Assert(store.LoadDraft(damagedCollection)!.CardId == drafts[damagedCollection].CardId &&
                store.IsDraftSaveBlocked(damagedCollection) && store.GetDraftRecoveryNotice(damagedCollection) is not null,
                "Each feed can recover its own previous draft while preserving the damaged original.");
            Throws<InvalidDataException>(() => store.SaveDraft(drafts[damagedCollection], damagedCollection));
            foreach (var collection in Collections.Where(collection => collection != damagedCollection))
            {
                drafts[collection].Content = Content("Revised " + collection);
                store.SaveDraft(drafts[collection], collection);
                Assert(!store.IsDraftSaveBlocked(collection), "A damaged feed must not disable any other composer's saving.");
            }
            var backup = Path.Combine(root, "collections-recovery-" + damagedCollection + ".zip");
            store.Backup(backup);
            var restored = Store(root, "recovery-restored-" + damagedCollection);
            restored.Restore(backup);
            foreach (var collection in Collections)
                Assert(restored.LoadDraft(collection)!.CardId == drafts[collection].CardId,
                    "Backup independently selects each feed's valid current or recovery draft.");
            Assert(File.ReadAllText(damagedPath) == "{ damaged original", "Exporting must preserve damaged source evidence.");
        }

        foreach (var collection in Collections)
        {
            var stale = Store(root, "conflict-" + collection);
            stale.Upsert(Card("Existing post", NoteCollection.Notes));
            foreach (var feed in Collections) stale.SaveDraft(Draft("Original " + feed), feed);
            var writer = Reopen(stale);
            var external = Draft("Other window " + collection);
            writer.SaveDraft(external, collection);
            Throws<InvalidOperationException>(() => stale.SaveDraft(Draft("Stale autosave"), collection));
            var unaffected = Collections.First(feed => feed != collection);
            stale.SaveDraft(Draft("Independent autosave"), unaffected);
            Assert(Reopen(stale).LoadDraft(collection)!.CardId == external.CardId,
                "A stale composer cannot replace another window's draft, while other feed drafts stay writable.");

            // A previous-only external edit is also ownership and recovery state, not disposable history.
            var guarded = Reopen(writer); guarded.LoadDraft(collection);
            var previous = Path.Combine(guarded.RootPath, PreviousDraftFile(collection));
            File.WriteAllText(previous, File.ReadAllText(previous) + " ");
            Throws<InvalidOperationException>(() => guarded.ClearDraft(collection));
            Assert(Reopen(guarded).LoadDraft(collection)!.CardId == external.CardId,
                "Changing only the recovery copy must prevent a stale clear from discarding its ownership.");

            var posted = Card("Sent exactly once", collection); posted.Id = external.CardId;
            writer = Reopen(guarded); writer.Upsert(posted); writer = Reopen(writer);
            Assert(writer.LoadDraft(collection) is null && writer.LoadDraft(unaffected) is not null,
                "Recovery after posting suppresses only the submitted feed's stable draft identity.");
            writer.Delete(posted.Id);
            Assert(Reopen(writer).LoadDraft(collection) is null,
                "Deleting a posted record cannot resurrect a composer cleared after interrupted posting.");
        }
    }

    public static void MediaOwnership(string root)
    {
        foreach (var draftOwner in Collections)
        {
            var store = Store(root, "media-" + draftOwner);
            var image = Picture(store);
            var path = store.GetImagePath(image);
            var posts = Collections.Select(collection => Card("Picture " + collection, collection, image)).ToArray();
            foreach (var post in posts) store.Upsert(post);
            var draft = Draft("Recoverable picture", image);
            store.SaveDraft(draft, draftOwner);
            draft.Content = Content("Current draft without picture");
            store.SaveDraft(draft, draftOwner);
            // Do not load any drafts before deletion: ownership must inspect all feeds on disk.
            store = Reopen(store);
            foreach (var post in posts)
            {
                store.Delete(post.Id);
                Assert(File.Exists(path), "Any feed's recoverable previous draft must retain a picture after its final post is deleted.");
            }
            var backup = Path.Combine(root, "collections-media-" + draftOwner + ".zip");
            store.Backup(backup);
            var restored = Store(root, "media-restored-" + draftOwner); restored.Restore(backup);
            Assert(File.Exists(Path.Combine(restored.AssetDirectory, image.AssetName)),
                "An exported library retains pictures owned only by a previous draft.");
            store.ClearDraft(draftOwner);
            var last = Card("Last saved owner", NoteCollection.Notes, image); store.Upsert(last);
            store.Delete(last.Id);
            Assert(!File.Exists(path) && Reopen(store).Cards.Count == 0,
                "Clearing both versions releases the final draft owner so later post deletion can remove its picture.");
        }

        foreach (var draftOwner in new[] { NoteCollection.Opinions, NoteCollection.EconomicAnalysis })
        {
            var store = Store(root, "invalid-owner-" + draftOwner);
            var image = Picture(store);
            var post = Card("Preserve until ownership is known", NoteCollection.Notes, image); store.Upsert(post);
            store.SaveDraft(Draft("Picture owner", image), draftOwner);
            var primary = Path.Combine(store.RootPath, "library.json");
            var original = File.ReadAllBytes(primary);
            File.WriteAllText(Path.Combine(store.RootPath, PreviousDraftFile(draftOwner)), "damaged recovery");
            store = Reopen(store);
            Throws<JsonException>(() => store.Delete(post.Id));
            Assert(File.ReadAllBytes(primary).SequenceEqual(original) && File.Exists(store.GetImagePath(image)) &&
                !File.Exists(Path.Combine(store.RootPath, "delete.pending.json")),
                "Unknown new-feed recovery ownership must block image deletion before changing the post or writing a journal.");
        }
    }

    public static void FormatUpgrade(string root)
    {
        var store = Version5Store(root, "upgrade");
        var oldCards = JsonSerializer.Serialize(store.Cards);
        var newPost = Card("New opinion", NoteCollection.Opinions);
        store.Upsert(newPost);
        AssertSnapshotVersions(store.RootPath, 6, 6);
        var recovery = JsonNode.Parse(File.ReadAllText(Path.Combine(store.RootPath, "library.previous.json")))!;
        Assert(recovery["cards"]!.AsArray().Count == 2 && !recovery.ToJsonString().Contains(newPost.Id, StringComparison.Ordinal),
            "First upgrade preserves the preceding complete cards while fencing the recovery copy against older readers.");
        Assert(JsonSerializer.Serialize(store.Cards.Where(card => card.Id != newPost.Id).ToList()) == oldCards,
            "Format promotion cannot alter existing Notes, Memo or review history.");
        File.WriteAllText(Path.Combine(store.RootPath, "library.json"), "damaged new save");
        var recovered = Reopen(store);
        Assert(recovered.Cards.Count == 2 && recovered.RecoveryNotice is not null,
            "The promoted previous snapshot remains independently readable after a damaged first new-feed save.");

        foreach (var collection in new[] { NoteCollection.Opinions, NoteCollection.EconomicAnalysis })
        {
            var draftOnly = Version5Store(root, "draft-fence-" + collection);
            var image = Picture(draftOnly);
            var draft = Draft("New feed without a submitted post", image);
            draftOnly.SaveDraft(draft, collection);
            AssertSnapshotVersions(draftOnly.RootPath, 6, 6);
            Assert(Reopen(draftOnly).LoadDraft(collection)!.Content.Images.Single().AssetName == image.AssetName,
                "Saving a draft alone must fence both library snapshots before older applications can ignore its image ownership.");
            var empty = Store(root, "empty-fence-" + collection);
            empty.SaveDraft(Draft("First unsent draft"), collection);
            AssertSnapshotVersions(empty.RootPath, 6, 6);
            Assert(Reopen(empty).Cards.Count == 0 && Reopen(empty).LoadDraft(collection) is not null,
                "A draft in an empty library establishes a complete readable format boundary without creating a post.");
        }

        var concurrent = Version5Store(root, "concurrent-upgrade");
        var stale = Reopen(concurrent);
        concurrent.Upsert(Card("First writer", NoteCollection.Opinions));
        var primary = Path.Combine(concurrent.RootPath, "library.json");
        var previous = Path.Combine(concurrent.RootPath, "library.previous.json");
        var primaryBytes = File.ReadAllBytes(primary); var previousBytes = File.ReadAllBytes(previous);
        Throws<InvalidOperationException>(() => stale.Upsert(Card("Stale writer", NoteCollection.EconomicAnalysis)));
        Assert(File.ReadAllBytes(primary).SequenceEqual(primaryBytes) && File.ReadAllBytes(previous).SequenceEqual(previousBytes),
            "A writer loaded before the upgrade cannot overwrite the winning post or its promoted recovery snapshot.");
        var afterUpgrade = Reopen(concurrent);
        concurrent.Upsert(Card("Second winner", NoteCollection.EconomicAnalysis));
        Throws<InvalidOperationException>(() => afterUpgrade.Upsert(Card("Stale schema 6 writer", NoteCollection.Opinions)));
        Assert(Reopen(concurrent).Cards.Count == 4, "Optimistic conflicts remain enforced after a schema upgrade.");

        if (OperatingSystem.IsWindows()) FailedUpgradeAndRetry(root);

        // A future format must not silently fall back and overwrite newer data with an old snapshot.
        var future = Store(root, "future-format"); future.Upsert(Card("Healthy old recovery", NoteCollection.Notes));
        var futurePath = Path.Combine(future.RootPath, "library.json");
        var futurePayload = JsonNode.Parse(File.ReadAllText(futurePath))!;
        futurePayload["version"] = 7;
        futurePayload["cards"]![0]!["collection"] = "FutureFeed";
        File.WriteAllText(futurePath, futurePayload.ToJsonString());
        var futureBytes = File.ReadAllBytes(futurePath);
        var futureRecovery = File.ReadAllBytes(Path.Combine(future.RootPath, "library.previous.json"));
        var blocked = new LibraryStore(future.RootPath);
        Throws<NotSupportedException>(blocked.Load);
        Throws<InvalidOperationException>(() => blocked.Upsert(Card("Cannot overwrite", NoteCollection.Notes)));
        Assert(File.ReadAllBytes(futurePath).SequenceEqual(futureBytes) &&
            File.ReadAllBytes(Path.Combine(future.RootPath, "library.previous.json")).SequenceEqual(futureRecovery),
            "An unsupported future primary and its valid older recovery must both remain untouched.");

        // Restore canonicalizes legacy snapshots before publishing any optional new-feed drafts.
        var archiveSource = Store(root, "legacy-archive"); archiveSource.Upsert(Card("Archived", NoteCollection.Notes));
        archiveSource.SaveDraft(Draft("Archived opinion"), NoteCollection.Opinions);
        var archivePath = Path.Combine(root, "collections-archive.zip"); archiveSource.Backup(archivePath);
        var legacyBytes = JsonNode.Parse(File.ReadAllText(Path.Combine(archiveSource.RootPath, "library.json")))!;
        legacyBytes["version"] = 5;
        var legacyArchive = Path.Combine(root, "collections-legacy-archive.zip");
        RewriteArchive(archivePath, legacyArchive, "library.json", JsonSerializer.SerializeToUtf8Bytes(legacyBytes));
        var restored = Store(root, "legacy-archive-restored"); restored.Restore(legacyArchive);
        AssertSnapshotVersions(restored.RootPath, 6, 6);
        Assert(restored.LoadDraft(NoteCollection.Opinions) is not null && restored.Cards.Count == 1,
            "A valid legacy-format archive retains its draft and cards while establishing downgrade protection.");
    }

    private static void FailedUpgradeAndRetry(string root)
    {
        var store = Version5Store(root, "blocked-upgrade");
        var primary = Path.Combine(store.RootPath, "library.json");
        var previous = Path.Combine(store.RootPath, "library.previous.json");
        var oldPrimary = File.ReadAllBytes(primary); var oldPrevious = File.ReadAllBytes(previous);
        using (var lockedRecovery = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read))
            ThrowsAccessFailure(() => store.Upsert(Card("Cannot promote recovery", NoteCollection.Opinions)));
        Assert(File.ReadAllBytes(primary).SequenceEqual(oldPrimary) && File.ReadAllBytes(previous).SequenceEqual(oldPrevious) &&
            store.Cards.Count == 2, "A failed recovery upgrade must leave the valid schema 5 primary and previous cards untouched.");

        // Another writer can still have loaded schema 5 when only the recovery promotion succeeds.
        var staleBeforePromotion = Reopen(store);
        using (var lockedPrimary = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read))
            ThrowsAccessFailure(() => store.Upsert(Card("Cannot replace primary", NoteCollection.Opinions)));
        AssertSnapshotVersions(store.RootPath, 5, 6);
        Assert(File.ReadAllBytes(primary).SequenceEqual(oldPrimary) && Reopen(store).Cards.Count == 2,
            "An interrupted format promotion must retain the complete old primary and a readable promoted recovery copy.");
        var afterPromotion = Reopen(store);
        staleBeforePromotion.Upsert(Card("Winning retry", NoteCollection.EconomicAnalysis));
        var winningPrimary = File.ReadAllBytes(primary); var winningPrevious = File.ReadAllBytes(previous);
        Throws<InvalidOperationException>(() => store.Upsert(Card("Stale pre-promotion writer", NoteCollection.Opinions)));
        Throws<InvalidOperationException>(() => afterPromotion.Upsert(Card("Stale post-promotion writer", NoteCollection.Opinions)));
        AssertSnapshotVersions(store.RootPath, 6, 6);
        Assert(File.ReadAllBytes(primary).SequenceEqual(winningPrimary) && File.ReadAllBytes(previous).SequenceEqual(winningPrevious) &&
            !Directory.EnumerateFiles(store.RootPath, "*.tmp").Any(),
            "Retry safely completes both snapshots; stale writers around the intermediate state cannot erase the winner or leave temporary files.");
    }

    private static LibraryStore Version5Store(string root, string name)
    {
        var store = Store(root, name);
        var note = Card("Existing learning #learn", NoteCollection.Notes); ReviewScheduler.RecordBrowse(note, Now);
        store.Upsert(note); store.Upsert(Card("Existing address #home", NoteCollection.Memo));
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(store.RootPath, "library.json")))!;
        payload["version"] = 5;
        foreach (var file in new[] { "library.json", "library.previous.json" })
            File.WriteAllText(Path.Combine(store.RootPath, file), payload.ToJsonString());
        return Reopen(store);
    }

    private static void AssertSnapshotVersions(string root, int primary, int previous)
    {
        using var current = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "library.json")));
        using var recovery = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "library.previous.json")));
        Assert(current.RootElement.GetProperty("version").GetInt32() == primary &&
            recovery.RootElement.GetProperty("version").GetInt32() == previous,
            "The active and recovery snapshots must expose the expected safe format boundary.");
    }

    private static void RewriteArchive(string source, string destination, string changedEntry, byte[] changedBytes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ZipFile.OpenRead(source))
            foreach (var entry in archive.Entries.Where(entry => entry.FullName != "backup-manifest.json"))
            {
                using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
                files[entry.FullName] = entry.FullName == changedEntry ? changedBytes : output.ToArray();
            }
        using var target = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var output = target.CreateEntry(file.Key).Open(); output.Write(file.Value);
        }
        using var manifest = target.CreateEntry("backup-manifest.json").Open();
        JsonSerializer.Serialize(manifest, new
        {
            version = 1,
            files = files.ToDictionary(file => file.Key, file => new
            {
                length = file.Value.LongLength,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Value))
            })
        });
    }

    private static string PreviousDraftFile(NoteCollection collection) => DraftFiles[collection].Replace(".json", ".previous.json", StringComparison.Ordinal);
    private static LibraryStore Store(string root, string name) { var store = new LibraryStore(Path.Combine(root, "collections-" + name)); store.Load(); return store; }
    private static LibraryStore Reopen(LibraryStore store) { var reopened = new LibraryStore(store.RootPath); reopened.Load(); return reopened; }
    private static CardDocument Card(string text, NoteCollection collection, NoteImage? image = null) => new() { Collection = collection, Note = Content(text, image) };
    private static ComposerDraft Draft(string text, NoteImage? image = null) => new() { Content = Content(text, image) };
    private static NoteContent Content(string text, NoteImage? image = null) => new()
    {
        Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }],
        Images = image is null ? [] : [image]
    };
    private static NoteImage Picture(LibraryStore store) => store.ImportImage(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC"), 1, 1);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ThrowsAccessFailure(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected a locked-file access failure.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

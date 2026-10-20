using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Reflection;
using Folio.Core;

// Dependency-free integration checks. Every run uses a new directory containing only synthetic data.
var dataRoot = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("FOLIO_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "Folio.Tests");
var testRoot = Path.Combine(Path.GetFullPath(dataRoot), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(testRoot);
var now = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
var tests = new (string Name, Action Run)[]
{
    ("Remembered cards gain longer intervals; recall decays over time", SchedulerProgression),
    ("Forgotten cards return soon with valid memory state", SchedulerLapse),
    ("Random review prioritizes due cards and respects exclusions", Selection),
    ("Repeated saves reopen the latest complete card", Persistence),
    ("An interrupted save preserves the last complete data", InterruptedSave),
    ("Corrupt files recover or block writes without losing originals", CorruptLibrary),
    ("Structured paragraphs and inline styles persist without coordinates", StructuredText),
    ("Text reads and writes use independent copies", IndependentText),
    ("Local profiles normalize names and preserve independent avatar references", () => ProfileChecks.RoundTrip(testRoot)),
    ("Invalid profiles and interrupted replacements preserve the saved identity", () => ProfileChecks.Safety(testRoot)),
    ("Profile corruption and external changes leave note and draft saving available", () => ProfileChecks.RecoveryAndConflicts(testRoot)),
    ("Backups preserve profiles while legacy archives and unsafe profiles remain safe", () => ProfileChecks.BackupAndLegacy(testRoot)),
    ("Long continuous notes survive complete save and reopen", LongText),
    ("Legacy text migrates in reading order without changing review history", LegacyText),
    ("Legacy v1 backups preserve original image assets without exposing them", LegacyBackup),
    ("Invalid text models cannot overwrite a healthy note", InvalidText),
    ("Text-only libraries keep optional images and assets absent", OptionalImages),
    ("Imported PNG and JPEG images persist independently and atomically", ImageImportRoundTrip),
    ("Malformed images and unsafe references cannot overwrite healthy notes", ImageSafety),
    ("Image-only drafts retain assets through send, backup and restore", ImageDraftBackup),
    ("Missing and changed image assets trigger safe library recovery", ImageRecovery),
    ("Missing image assets and invalid drafts cannot replace a healthy restore target", ImageRestoreSafety),
    ("Concurrent stores reuse one complete imported image without a false failure", () => CoreImageStorageChecks.ConcurrentImport(testRoot)),
    ("Image storage failures preserve drafts, clean temporary files and permit retry", () => CoreImageStorageChecks.FailedWritesAndRetry(testRoot)),
    ("Images and drafts survive Unicode library paths beyond 260 characters", () => CoreImageStorageChecks.UnicodeAndLongPaths(testRoot)),
    ("New-library initialization is atomic and never reseeds a saved empty collection", () => LibraryLifecycleChecks.FirstRun(testRoot)),
    ("Notes and Memo preserve separate drafts, search boundaries, images and backup state", () => MemoChecks.Run(testRoot)),
    ("Deleting a note purges its dedicated local image and both recovery snapshots", () => LibraryLifecycleChecks.DeleteExclusiveImages(testRoot)),
    ("Deleting notes preserves pictures shared by notes, profiles and both draft versions", () => LibraryLifecycleChecks.KeepSharedImages(testRoot)),
    ("Blocked image deletion fails before changing saved content and permits retry", () => LibraryLifecycleChecks.DeletionFailures(testRoot)),
    ("Interrupted deletions finish safely without resurrecting a removed note", () => LibraryLifecycleChecks.InterruptedDeletion(testRoot)),
    ("Backup finishes pending deletions or preserves the prior archive on blocked cleanup", () => LibraryLifecycleChecks.BackupPendingDeletion(testRoot)),
    ("Hashtags support Unicode, headings, style boundaries and bounded deduplication", Tags),
    ("Backup round-trip preserves structured text and review history", BackupRoundTrip),
    ("Unsafe legacy asset paths and malicious archives are rejected", PathTraversal),
    ("Backup destinations cannot overwrite either library copy", BackupDestination),
    ("A damaged backup cannot replace a healthy library", InvalidRestore),
    ("Backup planning and restore share expanded-size limits", BackupLimits),
    ("Markdown shorthand formats headings, lists, quotes and inline styles", MarkdownFormats),
    ("Markdown keeps fences, indentation and external syntax literal", MarkdownLiterals),
    ("Long and adversarial shorthand stays bounded", MarkdownBounds),
    ("Text colors and paragraph formats validate and copy safely", TextFormatsAndColors),
    ("Schema 1 through 5 retain history and default new fields", SchemaCompatibility),
    ("Automatic browsing applies the existing Good update exactly once", AutomaticBrowse),
    ("Unsent drafts persist separately and post with one stable identity", DraftPersistence),
    ("Draft corruption, failed saves and external changes preserve originals", DraftSafety),
    ("Backups optionally retain unsent drafts", DraftBackup),
    ("Backup cannot reload a stale composer or overwrite external draft changes", DraftBackupIsolation),
    ("Portable migration keeps all usage data under Log and preserves originals", PortableMigration),
    ("Portable migration rejects conflicting or damaged libraries", PortableMigrationSafety),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"\n{tests.Length - failed}/{tests.Length} passed. Synthetic test data: {testRoot}");
return failed == 0 ? 0 : 1;

CardDocument Card(string title = "Synthetic test card") => new()
{
    Id = Guid.NewGuid().ToString("N"), Title = title, CreatedAt = now, UpdatedAt = now,
    Note = Content("A synthetic learning note"), Blocks = [],
    Memory = new MemoryState { DueAt = now }, Reviews = []
};

NoteContent Content(string text) => new()
{
    Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }]
};

string Area(string name)
{
    var path = Path.Combine(testRoot, name);
    Directory.CreateDirectory(path);
    return path;
}

LibraryStore Store(string name)
{
    var store = new LibraryStore(Area(name));
    store.Load();
    return store;
}

void SchedulerProgression()
{
    var card = Card();
    ReviewScheduler.Apply(card, ReviewRating.Good, now);
    Assert(card.Memory.Repetitions == 1, "First feedback must count one review.");
    Assert(card.Reviews.Count == 1, "Review history must be recorded.");
    Assert(card.Memory.DueAt > now, "Good must schedule a future review.");
    var firstInterval = card.Memory.DueAt - now;
    var at = card.Memory.DueAt;
    var near = ReviewScheduler.RecallProbability(card, now);
    var later = ReviewScheduler.RecallProbability(card, at.AddDays(10));
    Assert(double.IsFinite(near) && near is >= 0 and <= 1, "Recall probability must be valid.");
    Assert(later < near && later >= 0, "Recall probability must decay over time.");
    ReviewScheduler.Apply(card, ReviewRating.Good, at);
    Assert(card.Memory.DueAt - at > firstInterval, "Remembering at due time must extend interval.");
    Assert(card.Memory.Repetitions == 2 && card.Reviews.Count == 2, "Every feedback must be recorded once.");
    Assert(card.Memory.Stability > 0 && card.Memory.Difficulty is >= 1 and <= 10, "FSRS state must stay within bounds.");
}

void SchedulerLapse()
{
    var card = Card();
    ReviewScheduler.Apply(card, ReviewRating.Good, now);
    ReviewScheduler.Apply(card, ReviewRating.Good, card.Memory.DueAt);
    var at = card.Memory.DueAt;
    var oldStability = card.Memory.Stability;
    ReviewScheduler.Apply(card, ReviewRating.Again, at);
    Assert(card.Memory.DueAt > at && card.Memory.DueAt <= at.AddHours(1), "Forgotten card must return soon for relearning.");
    Assert(card.Memory.Lapses >= 1, "Forgetting must count a lapse.");
    Assert(card.Memory.Stability > 0 && card.Memory.Stability < oldStability, "Forgetting should lower, not invalidate, stability.");
    Assert(card.Memory.Repetitions == 3 && card.Reviews.Count == 3, "Lapse history must be saved.");
    var hard = Card();
    var good = Card();
    ReviewScheduler.Apply(hard, ReviewRating.Hard, now);
    ReviewScheduler.Apply(good, ReviewRating.Good, now);
    Assert(hard.Memory.DueAt < good.Memory.DueAt, "Hard should return before Good for a new card.");
}

void Selection()
{
    var due = Card("Due now");
    due.Memory.DueAt = now.AddDays(-1);
    var future = Card("Due later");
    future.Memory.DueAt = now.AddDays(5);
    for (var i = 0; i < 30; i++)
        Assert(ReviewScheduler.Pick([future, due], new HashSet<string>(), now, new Random(i))?.Id == due.Id, "Due cards must have priority.");
    Assert(ReviewScheduler.Pick([due, future], new HashSet<string> { due.Id }, now, new Random(1))?.Id == future.Id, "Excluded card must never be selected.");
    Assert(ReviewScheduler.Pick([due, future], new HashSet<string> { due.Id, future.Id }, now) is null, "Excluding all cards should end the round.");
    Assert(ReviewScheduler.Pick([], new HashSet<string>(), now) is null, "Empty library should produce no card.");
}

void Persistence()
{
    var store = Store("persistence");
    var card = Card();
    for (var i = 0; i < 12; i++)
    {
        card.Title = $"Saved version {i}";
        NoteText.SetContent(card, Content(new string('x', 1024) + i));
        store.Upsert(card);
    }
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Title == "Saved version 11", "Upserts must save a single latest card.");
    Assert(NoteText.PlainText(reopened.Cards[0]).EndsWith("11"), "Saved content must be complete.");
    reopened.Delete(card.Id);
    var deleted = new LibraryStore(store.RootPath);
    deleted.Load();
    Assert(deleted.Cards.Count == 0, "Deletion must persist after restart.");
}

void OptionalImages()
{
    var methods = typeof(LibraryStore).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(method => method.Name).ToArray();
    Assert(methods.Contains("ImportImage") && methods.Contains("GetImagePath") && !methods.Contains("GetAssetPath"), "Imported pictures need a validated API while generic legacy asset paths remain private.");
    Assert(new NoteContent().Images.Count == 0, "Images must default to an empty optional list.");
    Assert(typeof(NoteParagraph).GetProperties().All(property => property.Name is "Kind" or "Runs" or "HeadingLevel" or "Number"), "Paragraphs must contain no image or positioning fields.");
    Assert(typeof(NoteRun).GetProperties().All(property => property.Name is "Text" or "Bold" or "Italic" or "Foreground" or "Background"), "Runs support text and inline styles only.");
    var store = Store("no-image-api");
    Assert(!Directory.Exists(store.AssetDirectory), "Loading a new library must not create an assets directory.");
    var card = Card();
    store.Upsert(card);
    Assert(!Directory.Exists(store.AssetDirectory), "Saving text must not create an assets directory.");
    var imageCard = Card("Rejected image");
    imageCard.Blocks.Add(new NoteBlock { Kind = "image", AssetName = "new.png" });
    Throws<InvalidDataException>(() => store.Upsert(imageCard), "New image blocks must be rejected even through the legacy model.");
    var zip = Path.Combine(testRoot, "text-only.zip");
    store.Backup(zip);
    var restored = Store("text-only-restore");
    restored.Restore(zip);
    Assert(!Directory.Exists(restored.AssetDirectory), "Restoring a text-only backup must not create assets.");
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(restored.RootPath, "library.json")));
    Assert(document.RootElement.GetProperty("version").GetInt32() == 5, "New saves must use library schema 5.");
}

void ImageImportRoundTrip()
{
    var store = Store("image-import");
    var png = Png(); var image = store.ImportImage(png, 1, 1);
    var duplicate = store.ImportImage(png, 1, 1);
    var jpeg = store.ImportImage(Jpeg(), 1, 1);
    Assert(image.AssetName == Convert.ToHexStringLower(SHA256.HashData(png)) + ".png" && duplicate.AssetName == image.AssetName && jpeg.AssetName.EndsWith(".jpg"), "Imports must use stable content hashes and encoded-format extensions.");
    Assert(Directory.GetFiles(store.AssetDirectory).Length == 2 && !Directory.GetFiles(store.AssetDirectory, "*.tmp").Any(), "A duplicate import must reuse the existing durable asset and leave no temporary files.");
    var content = new NoteContent { Images = [image, jpeg] };
    var card = Card("Only pictures"); NoteText.SetContent(card, content);
    image.PixelWidth = 999;
    Assert(NoteText.HasContent(card) && NoteText.HasContent(card.Note!) && NoteText.PlainText(card) == "", "An image-only card is sendable without inventing text, and SetContent must copy references.");
    store.Upsert(card);
    card.Note!.Images[0].PixelWidth = 999;
    var snapshot = store.Cards.Single();
    var copy = NoteText.Read(snapshot); copy.Images[0].PixelHeight = 999;
    Assert(snapshot.Note!.Images[0].PixelWidth == 1 && snapshot.Note.Images[0].PixelHeight == 1, "Read and stored snapshots must own independent image references.");
    var reopened = new LibraryStore(store.RootPath); reopened.Load();
    var saved = reopened.Cards.Single();
    Assert(saved.Note!.Images.Count == 2 && File.ReadAllBytes(reopened.GetImagePath(saved.Note.Images[0])).SequenceEqual(png) && File.ReadAllBytes(reopened.GetImagePath(saved.Note.Images[1])).SequenceEqual(Jpeg()), "Both supported formats must reopen with their original bytes and dimensions.");
    reopened.Delete(saved.Id);
    Assert(Directory.GetFiles(reopened.AssetDirectory).Length == 0, "Deleting a card must remove its unshared local pictures.");
}

void ImageSafety()
{
    var store = Store("image-safety"); store.Upsert(Card("Healthy original"));
    var primary = Path.Combine(store.RootPath, "library.json");
    var original = File.ReadAllBytes(primary);
    var broken = Png(); broken[45] ^= 1;
    foreach (var invalid in new[] { Array.Empty<byte>(), "GIF89a invalid"u8.ToArray(), Png()[..24], Png()[..^1], broken, Jpeg()[..^2], new byte[NoteImages.MaxImportBytes + 1] })
        Throws<InvalidDataException>(() => store.ImportImage(invalid, 1, 1), "Invalid, truncated or oversized images must be rejected before asset publication.");
    foreach (var (width, height) in new[] { (0, 1), (-1, 1), (16001, 1), (8000, 8000), (2, 1) })
        Throws<InvalidDataException>(() => store.ImportImage(Png(), width, height), "Invalid or mismatched dimensions must be rejected.");
    Assert(!Directory.Exists(store.AssetDirectory), "Rejected imports must not create the asset folder.");
    var good = store.ImportImage(Png(), 1, 1);
    var invalidNotes = new[]
    {
        new NoteContent { Images = null! },
        new NoteContent { Images = [null!] },
        new NoteContent { Images = Enumerable.Range(0, 5).Select(_ => good).ToList() },
        new NoteContent { Images = [new NoteImage { AssetName = "../outside.png", PixelWidth = 1, PixelHeight = 1 }] },
        new NoteContent { Images = [new NoteImage { AssetName = good.AssetName, PixelWidth = 0, PixelHeight = 1 }] },
        new NoteContent { Images = [new NoteImage { AssetName = new string('a', 64) + ".png", PixelWidth = 1, PixelHeight = 1 }] }
    };
    foreach (var content in invalidNotes)
    {
        var bad = Card(); bad.Note = content;
        Throws<InvalidDataException>(() => store.Upsert(bad), "Invalid image models or missing files cannot enter the library.");
        Assert(File.ReadAllBytes(primary).SequenceEqual(original), "Every rejected picture must preserve healthy library bytes.");
    }
    Throws<InvalidDataException>(() => store.GetImagePath(new NoteImage { AssetName = "C:/outside.png", PixelWidth = 1, PixelHeight = 1 }), "Display paths must reject path injection.");
    var asset = store.GetImagePath(good);
    File.WriteAllBytes(asset, Jpeg());
    Throws<InvalidDataException>(() => store.GetImagePath(good), "An asset with changed content cannot be displayed under its previous hash.");
    Throws<InvalidDataException>(() => store.ImportImage(Png(), 1, 1), "A repeated import cannot overwrite evidence at a damaged immutable asset name.");
    Assert(File.ReadAllBytes(asset).SequenceEqual(Jpeg()) && File.ReadAllBytes(primary).SequenceEqual(original), "Damaged assets and saved notes must stay intact after rejection.");
}

void ImageDraftBackup()
{
    var source = Store("image-draft-backup");
    var cardImage = source.ImportImage(Png(), 1, 1);
    var draftImage = source.ImportImage(Jpeg(), 1, 1);
    var card = Card("Saved picture"); card.Note!.Images = [cardImage]; source.Upsert(card);
    var draft = new ComposerDraft { Content = new NoteContent { Images = [draftImage] } }; source.SaveDraft(draft);
    draftImage.PixelWidth = 999;
    var restoredDraft = source.LoadDraft()!;
    Assert(NoteText.HasContent(restoredDraft.Content) && restoredDraft.Content.Images.Single().PixelWidth == 1 && source.Cards.Count == 1, "An image-only draft is valid, independent and separate from posted notes.");
    var backup = Path.Combine(testRoot, "image-draft.zip"); source.Backup(backup);
    using (var archive = ZipFile.OpenRead(backup))
        Assert(archive.GetEntry("assets/" + cardImage.AssetName) is not null && archive.GetEntry("assets/" + restoredDraft.Content.Images[0].AssetName) is not null && archive.GetEntry("draft.json") is not null, "Backups must include both posted and draft-only assets.");
    var target = Store("image-draft-target"); target.Restore(backup);
    var opened = target.LoadDraft()!;
    Assert(File.ReadAllBytes(target.GetImagePath(opened.Content.Images.Single())).SequenceEqual(Jpeg()) && target.Cards.Single().Note!.Images.Count == 1, "Restoring must validate and retain draft-only and posted pictures.");
    var sent = Card("Sent image only"); sent.Id = opened.CardId; NoteText.SetContent(sent, opened.Content); target.Upsert(sent); target.ClearDraft();
    Assert(target.Cards.Count == 2 && target.LoadDraft() is null && Directory.GetFiles(target.AssetDirectory).Length == 2, "Sending and clearing must retain the image asset used by the posted note.");
    var oldDraft = new ComposerDraft { Content = Content("Healthy draft") }; target.SaveDraft(oldDraft);
    var before = File.ReadAllBytes(Path.Combine(target.RootPath, "draft.json"));
    var missing = new ComposerDraft { Content = new NoteContent { Images = [new NoteImage { AssetName = new string('b', 64) + ".png", PixelWidth = 1, PixelHeight = 1 }] } };
    Throws<InvalidDataException>(() => target.SaveDraft(missing), "A missing draft image must reject saving.");
    Assert(File.ReadAllBytes(Path.Combine(target.RootPath, "draft.json")).SequenceEqual(before), "Missing draft assets cannot replace a complete draft.");
}

void ImageRecovery()
{
    var store = Store("image-missing-recovery");
    var card = Card("Previous safe note"); store.Upsert(card);
    var image = store.ImportImage(Png(), 1, 1); card.Note!.Images.Add(image); store.Upsert(card);
    File.Delete(store.GetImagePath(image));
    var reopened = new LibraryStore(store.RootPath); reopened.Load();
    Assert(reopened.RecoveryNotice is not null && reopened.Cards.Single().Note!.Images.Count == 0 && reopened.Cards.Single().Id == card.Id, "A missing image must recover the complete previous note instead of publishing a broken reference.");
    var both = Store("image-changed-recovery"); var picture = both.ImportImage(Png(), 1, 1);
    var only = Card(); only.Note!.Images = [picture]; both.Upsert(only);
    var bytes = File.ReadAllBytes(Path.Combine(both.RootPath, "library.json"));
    File.WriteAllBytes(both.GetImagePath(picture), Jpeg());
    var blocked = new LibraryStore(both.RootPath);
    Throws<InvalidDataException>(blocked.Load, "Two library copies referencing a changed image must block unsafe loading.");
    Throws<InvalidOperationException>(() => blocked.Upsert(Card()), "Unverified image references must leave saving disabled.");
    Assert(File.ReadAllBytes(Path.Combine(both.RootPath, "library.json")).SequenceEqual(bytes), "Image validation cannot rewrite either damaged library copy.");
    var draftStore = Store("image-missing-draft"); var draftPicture = draftStore.ImportImage(Png(), 1, 1);
    draftStore.SaveDraft(new ComposerDraft { Content = Content("Previous text draft") });
    draftStore.SaveDraft(new ComposerDraft { Content = new NoteContent { Images = [draftPicture] } });
    File.Delete(draftStore.GetImagePath(draftPicture));
    var draftReopen = new LibraryStore(draftStore.RootPath); draftReopen.Load();
    Assert(draftReopen.LoadDraft()?.Content.Paragraphs.Single().Runs.Single().Text == "Previous text draft" && draftReopen.DraftSaveBlocked, "A missing draft picture must preserve damaged files, offer the previous draft and block autosave.");
}

void ImageRestoreSafety()
{
    var source = Store("image-restore-source"); var image = source.ImportImage(Png(), 1, 1);
    var card = Card(); card.Note!.Images = [image]; source.Upsert(card);
    var zip = Path.Combine(testRoot, "image-restore-damaged.zip"); source.Backup(zip);
    var target = Store("image-restore-safe-target"); target.Upsert(Card("Healthy target"));
    var before = File.ReadAllBytes(Path.Combine(target.RootPath, "library.json"));
    // Update the manifest as well, so failure must come from reference validation rather than archive hashing.
    RewriteArchive(zip, files => files.Remove("assets/" + image.AssetName));
    Throws<InvalidDataException>(() => target.Restore(zip), "A hash-consistent archive missing a referenced image cannot replace the library.");
    Assert(File.ReadAllBytes(Path.Combine(target.RootPath, "library.json")).SequenceEqual(before), "Image validation must complete before replacing a healthy library.");
    var draftZip = Path.Combine(testRoot, "image-restore-draft-damaged.zip"); source.Backup(draftZip);
    RewriteArchive(draftZip, files => files["draft.json"] = JsonSerializer.SerializeToUtf8Bytes(new ComposerDraft
    {
        Content = new NoteContent { Images = [new NoteImage { AssetName = new string('c', 64) + ".png", PixelWidth = 1, PixelHeight = 1 }] }
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Throws<InvalidDataException>(() => target.Restore(draftZip), "A hash-consistent archive with a missing draft image must reject restoration.");
    Assert(File.ReadAllBytes(Path.Combine(target.RootPath, "library.json")).SequenceEqual(before), "Draft image validation must finish before replacing healthy notes.");
}

void RewriteArchive(string zip, Action<Dictionary<string, byte[]>> edit)
{
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using (var archive = ZipFile.OpenRead(zip))
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "backup-manifest.json"))
        {
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); files[entry.FullName] = output.ToArray();
        }
    edit(files);
    var temporary = zip + ".rewritten";
    using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
    {
        foreach (var file in files) { using var output = archive.CreateEntry(file.Key).Open(); output.Write(file.Value); }
        using var manifest = archive.CreateEntry("backup-manifest.json").Open();
        JsonSerializer.Serialize(manifest, new { version = 1, files = files.ToDictionary(file => file.Key, file => new { length = file.Value.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Value)) }) });
    }
    File.Move(temporary, zip, true);
}

void Tags()
{
    var content = Content("# Heading\n#Work #work #工作 #中文-笔记 #under_score (#2026) word#hidden ##heading # ");
    var tags = NoteTags.Read(content);
    Assert(tags.SequenceEqual(new[] { "Work", "工作", "中文-笔记", "under_score", "2026" }), "Tags must recognize CJK, digits, underscores and hyphens; ignore headings and inside-word hashes; and keep first spelling.");
    var card = Card(); card.Note = content;
    Assert(NoteTags.Matches(card, "#WORK") && NoteTags.Matches(card, "工作") && NoteTags.Matches(card, null) && !NoteTags.Matches(card, "missing") && !NoteTags.Matches(card, "#"), "Optional filters must match whole tags case-insensitively with or without the hash.");
    content.Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "#split", Bold = true }, new NoteRun { Text = "Tag #𠮷野家" }] }];
    Assert(NoteTags.Read(content).SequenceEqual(new[] { "splitTag", "𠮷野家" }), "Formatting boundaries must not split a tag, and supplementary Unicode letters must be supported.");
    content = Content("#" + new string('a', 65) + " #" + new string('b', 64) + " " + string.Join(" ", Enumerable.Range(0, 100).Select(index => "#t" + index)));
    tags = NoteTags.Read(content);
    Assert(tags.Count == 64 && tags[0] == new string('b', 64) && !tags.Contains(new string('a', 64)), "Overlong tags must be omitted rather than truncated, and extraction must stop at 64 distinct tags.");
    var legacy = Card(); legacy.Note = null; legacy.Blocks = [new NoteBlock { Text = "Legacy #旧笔记 #Work" }];
    Assert(NoteTags.Read(legacy).SequenceEqual(new[] { "旧笔记", "Work" }), "Legacy text must support tags without rewriting its original blocks.");
    var parsed = MarkdownText.Parse("# Actual heading\nBody #topic");
    Assert(NoteTags.Read(parsed).SequenceEqual(new[] { "topic" }), "Markdown heading syntax must not become a hashtag.");
}

void StructuredText()
{
    var store = Store("structured-text");
    var card = Card();
    NoteText.SetContent(card, new NoteContent
    {
        Paragraphs =
        [
            new NoteParagraph { Kind = "heading", Runs = [new NoteRun { Text = "How memory works", Bold = true }] },
            new NoteParagraph { Kind = "body", Runs = [new NoteRun { Text = "Spacing " }, new NoteRun { Text = "supports recall", Bold = true, Italic = true }] },
            new NoteParagraph { Kind = "bullet", Runs = [new NoteRun { Text = "Review at the right time", Italic = true }] },
            new NoteParagraph { Kind = "body", Runs = [] }
        ]
    });
    store.Upsert(card);
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    var note = NoteText.Read(reopened.Cards.Single());
    Assert(note.Paragraphs.Count == 4 && note.Paragraphs[0].Kind == "heading" && note.Paragraphs[2].Kind == "bullet", "Paragraph kinds and empty paragraphs must survive reopening.");
    Assert(note.Paragraphs[1].Runs[1].Bold && note.Paragraphs[1].Runs[1].Italic && note.Paragraphs[2].Runs[0].Italic, "Inline bold and italic must round-trip independently.");
    Assert(NoteText.PlainText(card) == "How memory works\nSpacing supports recall\nReview at the right time\n", "Plain text must join runs and preserve paragraph boundaries without formatting markup.");
    Assert(NoteText.HasContent(card) && card.Blocks.Count == 0, "A structured note is content without canvas blocks.");
    NoteText.SetContent(card, Content(" \n\t "));
    Assert(!NoteText.HasContent(card), "Whitespace-only notes must not count as content.");
}

void IndependentText()
{
    var card = Card();
    var content = Content("Original text");
    NoteText.SetContent(card, content);
    content.Paragraphs[0].Runs[0].Text = "External mutation";
    var read = NoteText.Read(card);
    Assert(read.Paragraphs[0].Runs[0].Text == "Original text", "SetContent must take an independent copy.");
    read.Paragraphs[0].Runs[0].Text = "Read mutation";
    read.Paragraphs.Add(new NoteParagraph());
    Assert(NoteText.PlainText(card) == "Original text" && card.Note!.Paragraphs.Count == 1, "Read must return a deep copy.");
    var store = Store("snapshot-text");
    store.Upsert(card);
    store.Cards[0].Note!.Paragraphs[0].Runs[0].Text = "Snapshot mutation";
    Assert(NoteText.PlainText(store.Cards.Single()) == "Original text", "Library snapshots must isolate nested note objects.");
}

void LongText()
{
    var store = Store("long-text");
    var card = Card("A long continuous note");
    var paragraphs = Enumerable.Range(0, 4000).Select(index => new NoteParagraph
    {
        Kind = index % 17 == 0 ? "heading" : index % 5 == 0 ? "bullet" : "body",
        Runs = [new NoteRun { Text = $"Paragraph {index}: " }, new NoteRun { Text = new string('a', 180), Italic = index % 2 == 0 }]
    }).ToList();
    NoteText.SetContent(card, new NoteContent { Paragraphs = paragraphs });
    var before = NoteText.PlainText(card);
    store.Upsert(card);
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    Assert(NoteText.PlainText(reopened.Cards.Single()) == before && before.Length > 750000, "A long note must save completely without a fixed canvas cutoff.");
    Assert(NoteText.Read(reopened.Cards.Single()).Paragraphs.Count == 4000, "Long note paragraph count must be retained.");
}

void LegacyText()
{
    var card = Card("Legacy text");
    card.Note = null;
    card.Blocks =
    [
        new NoteBlock { Text = "Last", X = 15, Y = 300 },
        new NoteBlock { Text = "Right", X = 250, Y = 80 },
        new NoteBlock { Kind = "image", AssetName = "preserved.png", X = 20, Y = 10 },
        new NoteBlock { Text = "Heading\r\nSecond heading", X = 30, Y = 20, FontSize = 28, Bold = true },
        new NoteBlock { Text = "Left\n\nAfter a blank", X = 20, Y = 80 }
    ];
    ReviewScheduler.Apply(card, ReviewRating.Good, now);
    var beforeCreated = card.CreatedAt;
    var beforeUpdated = card.UpdatedAt;
    var beforeMemory = JsonSerializer.Serialize(card.Memory);
    var beforeReviews = JsonSerializer.Serialize(card.Reviews);
    var beforeBlocks = JsonSerializer.Serialize(card.Blocks);
    var note = NoteText.Read(card);
    Assert(NoteText.PlainText(card) == "Heading\nSecond heading\nLeft\n\nAfter a blank\nRight\nLast", "Legacy text must follow Y then X and preserve line breaks while omitting images.");
    Assert(note.Paragraphs[0].Kind == "heading" && note.Paragraphs[1].Kind == "heading" && note.Paragraphs[0].Runs[0].Bold, "Large legacy text must become headings with bold preserved.");
    Assert(card.Note is null, "Reading legacy text must not implicitly mutate the stored document.");
    NoteText.SetContent(card, note);
    Assert(card.CreatedAt == beforeCreated && card.UpdatedAt == beforeUpdated, "Text conversion must not alter timestamps.");
    Assert(JsonSerializer.Serialize(card.Memory) == beforeMemory && JsonSerializer.Serialize(card.Reviews) == beforeReviews, "Text conversion must not alter review state or history.");
    Assert(JsonSerializer.Serialize(card.Blocks) == beforeBlocks, "Legacy blocks must remain intact for recovery.");
    var imageOnly = Card();
    imageOnly.Note = null;
    imageOnly.Blocks = [new NoteBlock { Kind = "image", AssetName = "original.png" }];
    Assert(!NoteText.HasContent(imageOnly) && NoteText.PlainText(imageOnly) == "", "Legacy images must not appear as text content.");
}

void LegacyBackup()
{
    var root = Area("legacy-v1-library");
    var card = Card("Legacy library");
    card.Note = null;
    card.Blocks =
    [
        new NoteBlock { Text = "Preserved legacy text", X = 25, Y = 40 },
        new NoteBlock { Kind = "image", AssetName = "original.png", X = 25, Y = 160, Radius = 18 }
    ];
    ReviewScheduler.Apply(card, ReviewRating.Good, now);
    WriteLegacyLibrary(root, card);
    Directory.CreateDirectory(Path.Combine(root, "assets"));
    File.WriteAllBytes(Path.Combine(root, "assets", "original.png"), Png());
    File.WriteAllBytes(Path.Combine(root, "assets", "retained-orphan.png"), Png());
    var originalBackup = Path.Combine(testRoot, "original-v1.zip");
    WriteLegacyBackup(root, originalBackup);
    var source = new LibraryStore(root);
    source.Load();
    var loaded = source.Cards.Single();
    var memoryBefore = JsonSerializer.Serialize(loaded.Memory);
    NoteText.SetContent(loaded, Content("Edited as continuous text"));
    source.Upsert(loaded);
    var backup = Path.Combine(testRoot, "migrated-v2.zip");
    source.Backup(backup);
    var restored = Store("legacy-v2-restore");
    restored.Restore(backup);
    var saved = restored.Cards.Single();
    Assert(NoteText.PlainText(saved) == "Edited as continuous text" && saved.Blocks.Count == 2, "The new note takes precedence while original blocks remain recoverable.");
    Assert(saved.CreatedAt == card.CreatedAt && JsonSerializer.Serialize(saved.Memory) == memoryBefore && saved.Reviews.Count == 1, "Migration and backup must preserve creation and review history.");
    foreach (var name in new[] { "original.png", "retained-orphan.png" })
        Assert(File.ReadAllBytes(Path.Combine(restored.AssetDirectory, name)).SequenceEqual(Png()), "Every existing legacy image must survive the backup, including retained orphan assets.");
    restored.Delete(saved.Id);
    Assert(!File.Exists(Path.Combine(restored.AssetDirectory, "original.png")) && File.Exists(Path.Combine(restored.AssetDirectory, "retained-orphan.png")),
        "Deleting a card removes its owned legacy image while leaving unrelated local files intact.");
    var oldRestore = Store("original-v1-restore");
    oldRestore.Restore(originalBackup);
    Assert(oldRestore.Cards.Single().Note is null && NoteText.PlainText(oldRestore.Cards.Single()) == "Preserved legacy text", "Original v1 backups must restore and read through the legacy text adapter.");
    Assert(File.ReadAllBytes(Path.Combine(oldRestore.AssetDirectory, "original.png")).SequenceEqual(Png()), "Original v1 assets must remain intact.");
}

void InvalidText()
{
    var store = Store("invalid-text");
    var card = Card("Healthy text");
    store.Upsert(card);
    var original = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
    var invalid = new[]
    {
        new NoteContent { Paragraphs = null! },
        new NoteContent { Paragraphs = [null!] },
        new NoteContent { Paragraphs = [new NoteParagraph { Kind = "image" }] },
        new NoteContent { Paragraphs = [new NoteParagraph { Runs = null! }] },
        new NoteContent { Paragraphs = [new NoteParagraph { Runs = [null!] }] },
        new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = null! }] }] },
        Content(new string('x', NoteText.MaxTextLength + 1)),
        new NoteContent { Paragraphs = Enumerable.Range(0, NoteText.MaxParagraphs + 1).Select(_ => new NoteParagraph()).ToList() },
        new NoteContent { Paragraphs = [new NoteParagraph { Runs = Enumerable.Range(0, NoteText.MaxRuns + 1).Select(_ => new NoteRun()).ToList() }] }
    };
    foreach (var content in invalid)
    {
        Throws<InvalidDataException>(() => NoteText.SetContent(card, content), "SetContent must reject an unsafe text model before changing the note.");
        var attempt = Card();
        attempt.Note = content;
        Throws<InvalidDataException>(() => store.Upsert(attempt), "Storage must also validate directly assigned text models.");
        Assert(File.ReadAllBytes(Path.Combine(store.RootPath, "library.json")).SequenceEqual(original), "Rejected text must leave the original library bytes unchanged.");
    }
    var incompleteRoot = Area("missing-note-runs");
    var payload = JsonNode.Parse(LibraryBytes(Card(), 2))!;
    payload["cards"]![0]!["note"]!["paragraphs"]![0]!.AsObject().Remove("runs");
    File.WriteAllText(Path.Combine(incompleteRoot, "library.json"), payload.ToJsonString());
    Throws<InvalidDataException>(() => new LibraryStore(incompleteRoot).Load(), "Missing required note fields must not silently create empty text.");
}

byte[] LibraryBytes(CardDocument card, int version)
{
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    var payload = JsonSerializer.SerializeToNode(new { version, cards = new[] { card } }, options)!;
    if (version == 1) payload["cards"]![0]!.AsObject().Remove("note");
    return Encoding.UTF8.GetBytes(payload.ToJsonString());
}

void WriteLegacyLibrary(string root, CardDocument card) => File.WriteAllBytes(Path.Combine(root, "library.json"), LibraryBytes(card, 1));

void WriteLegacyBackup(string root, string zip)
{
    var files = new Dictionary<string, byte[]> { ["library.json"] = File.ReadAllBytes(Path.Combine(root, "library.json")) };
    foreach (var file in Directory.GetFiles(Path.Combine(root, "assets"))) files["assets/" + Path.GetFileName(file)] = File.ReadAllBytes(file);
    var manifest = new
    {
        version = 1,
        files = files.ToDictionary(pair => pair.Key, pair => new { length = pair.Value.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) })
    };
    using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
    foreach (var pair in files)
    {
        using var output = archive.CreateEntry(pair.Key).Open();
        output.Write(pair.Value);
    }
    using var manifestOutput = archive.CreateEntry("backup-manifest.json").Open();
    JsonSerializer.Serialize(manifestOutput, manifest);
}

void CorruptLibrary()
{
    var store = Store("corrupt-library");
    var card = Card("Recovery version");
    store.Upsert(card);
    card.Title = "Latest version";
    store.Upsert(card);
    File.WriteAllText(Path.Combine(store.RootPath, "library.json"), "{ deliberately broken");
    var recovered = new LibraryStore(store.RootPath);
    recovered.Load();
    Assert(recovered.Cards.Count == 1 && recovered.Cards[0].Title == "Recovery version", "Previous complete version must recover a damaged primary file.");

    var broken = Store("both-corrupt");
    File.WriteAllText(Path.Combine(broken.RootPath, "library.json"), "{ broken primary");
    File.WriteAllText(Path.Combine(broken.RootPath, "library.previous.json"), "{ broken previous");
    Throws<InvalidDataException>(() => broken.Load(), "Two broken copies must report data damage.");
    Throws<InvalidOperationException>(() => broken.Upsert(Card()), "A failed load must block writes.");
    Throws<InvalidOperationException>(() => broken.Delete("anything"), "A failed load must block deletes.");
    Throws<InvalidOperationException>(() => broken.Backup(Path.Combine(testRoot, "must-not-back-up.zip")), "A failed load must block misleading backups.");
    Assert(File.ReadAllText(Path.Combine(broken.RootPath, "library.json")) == "{ broken primary", "Failed load must preserve damaged files for recovery.");
    var healthy = Store("healthy-recovery-source");
    var rescue = Card("Restored from backup");
    healthy.Upsert(rescue);
    var backup = Path.Combine(testRoot, "rescue.zip");
    healthy.Backup(backup);
    broken.Restore(backup);
    rescue.Title = "Saving after recovery works";
    broken.Upsert(rescue);
    var unlocked = new LibraryStore(broken.RootPath);
    unlocked.Load();
    Assert(unlocked.Cards.Single().Title == rescue.Title, "A valid explicit restore must recover normal operation.");
    var incomplete = Store("incomplete-structure");
    File.WriteAllText(Path.Combine(incomplete.RootPath, "library.json"), "{\"version\":1,\"cards\":[{}]}");
    Throws<InvalidDataException>(() => incomplete.Load(), "Syntactically valid JSON missing required card data must not become an empty card.");
}

void InterruptedSave()
{
    var store = Store("interrupted-save");
    var card = Card("Fully saved version");
    store.Upsert(card);
    using (var locked = new FileStream(Path.Combine(store.RootPath, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        card.Title = "This save must fail";
        Throws<IOException>(() => store.Upsert(card), "Locked destination must report save failure.");
    }
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Title == "Fully saved version", "Interrupted save must retain the complete previous file.");
    var first = Store("interrupted-first-save");
    var previous = Path.Combine(first.RootPath, "library.previous.json");
    File.WriteAllText(previous, "locked synthetic previous");
    var rejected = false;
    using (var locked = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try { first.Upsert(Card()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
    }
    Assert(rejected, "A failed first recovery-copy write must report failure.");
    Assert(!File.Exists(Path.Combine(first.RootPath, "library.json")), "A failed first save must not publish a primary file prematurely.");
    first.Upsert(Card("Retry after unlock"));
    var retry = new LibraryStore(first.RootPath);
    retry.Load();
    Assert(retry.Cards.Single().Title == "Retry after unlock", "The first save can safely retry after the lock is released.");
}

void BackupRoundTrip()
{
    var source = Store("backup-source");
    var card = Card("Backed-up test card");
    card.Note!.Paragraphs.Add(new NoteParagraph { Kind = "bullet", Runs = [new NoteRun { Text = "A retained detail", Bold = true, Italic = true }] });
    ReviewScheduler.Apply(card, ReviewRating.Good, now);
    source.Upsert(card);
    var zip = Path.Combine(testRoot, "synthetic-backup.zip");
    source.Backup(zip);
    var target = Store("backup-target");
    target.Upsert(Card("Replaced by restore"));
    target.Restore(zip);
    var reopened = new LibraryStore(target.RootPath);
    reopened.Load();
    Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Id == card.Id, "Restore must replace the library.");
    var restored = reopened.Cards[0];
    Assert(restored.Note?.Paragraphs.Count == 2 && restored.Note.Paragraphs[1].Runs[0].Bold && restored.Note.Paragraphs[1].Runs[0].Italic, "Structured text and inline styles must survive backup.");
    Assert(restored.Memory.Repetitions == 1 && restored.Reviews.Count == 1, "Review state and history must survive backup.");
    Assert(restored.Blocks.Count == 0 && !Directory.Exists(reopened.AssetDirectory), "New notes must remain text-only after restore.");
}

void PathTraversal()
{
    var store = Store("traversal");
    var index = 0;
    foreach (var path in new[] { "../outside.png", "..\\outside.png", "C:\\outside.png", "/outside.png" })
    {
        var legacy = Card();
        legacy.Note = null;
        legacy.Blocks = [new NoteBlock { Kind = "image", AssetName = path }];
        var root = Area("unsafe-legacy-" + index++);
        WriteLegacyLibrary(root, legacy);
        Throws<InvalidDataException>(() => new LibraryStore(root).Load(), "Unsafe legacy asset paths must be rejected.");
    }
    var zip = Path.Combine(testRoot, "malicious.zip");
    store.Backup(zip);
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
    {
        var entry = archive.CreateEntry("../must-not-exist.txt");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write("synthetic malicious path");
    }
    Throws<InvalidDataException>(() => store.Restore(zip), "ZIP path traversal must be rejected.");
    Assert(!File.Exists(Path.Combine(testRoot, "must-not-exist.txt")), "Restore must not write outside its root.");
}

void InvalidRestore()
{
    var store = Store("invalid-restore");
    var card = Card("Healthy data");
    store.Upsert(card);
    var zip = Path.Combine(testRoot, "invalid-library.zip");
    store.Backup(zip);
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
    {
        archive.GetEntry("library.json")!.Delete();
        var entry = archive.CreateEntry("library.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("{ invalid json");
    }
    Throws<InvalidDataException>(() => store.Restore(zip), "Invalid backup must be rejected.");
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Id == card.Id, "Failed restore must leave the current library intact.");
}

void BackupDestination()
{
    var store = Store("backup-destination");
    var card = Card("Protected data");
    store.Upsert(card);
    foreach (var file in new[] { "library.json", "LIBRARY.JSON", "library.previous.json", "LIBRARY.PREVIOUS.JSON" })
        Throws<InvalidDataException>(() => store.Backup(Path.Combine(store.RootPath, file)), "Backup must protect both library copies regardless of Windows filename case.");
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    Assert(reopened.Cards.Count == 1 && reopened.Cards[0].Id == card.Id, "Rejected backup destination must preserve original data.");
}

void BackupLimits()
{
    // Synthetic metadata exercises GB-scale boundaries without creating large files.
    var exactTotal = Enumerable.Range(0, 32).Select(index => new ArchiveEntrySize("assets/file-" + index,
        ArchiveLimits.MaxFileBytes - (index == 31 ? 1 : 0))).Append(new ArchiveEntrySize("backup-manifest.json", 1));
    ArchiveLimits.Validate(exactTotal);
    Throws<InvalidDataException>(() => ArchiveLimits.Validate(Enumerable.Range(0, 32)
        .Select(index => new ArchiveEntrySize("assets/file-" + index, ArchiveLimits.MaxFileBytes))
        .Append(new ArchiveEntrySize("backup-manifest.json", 1))), "The manifest itself must count toward the 2 GB expanded-size limit.");
    ArchiveLimits.Validate([new ArchiveEntrySize("library.json", 1), new ArchiveEntrySize("backup-manifest.json", ArchiveLimits.MaxManifestBytes)]);
    Throws<InvalidDataException>(() => ArchiveLimits.Validate([new ArchiveEntrySize("backup-manifest.json", ArchiveLimits.MaxManifestBytes + 1)]),
        "A manifest one byte over 8 MB must be rejected before backup or restore.");
    Throws<InvalidDataException>(() => ArchiveLimits.Validate([new ArchiveEntrySize("assets/file", ArchiveLimits.MaxFileBytes + 1)]),
        "An individual expanded file one byte over 64 MB must be rejected.");
    ArchiveLimits.Validate(Enumerable.Range(0, ArchiveLimits.MaxEntries).Select(index => new ArchiveEntrySize("file-" + index, 1)));
    Throws<InvalidDataException>(() => ArchiveLimits.Validate(Enumerable.Range(0, ArchiveLimits.MaxEntries + 1)
        .Select(index => new ArchiveEntrySize("file-" + index, 1))), "The same archive entry limit must include library and manifest entries.");

    var store = Store("backup-preflight-protection");
    store.Upsert(Card("Protected by preflight"));
    var zip = Path.Combine(testRoot, "existing-valid-backup.zip");
    store.Backup(zip);
    var originalZip = File.ReadAllBytes(zip);
    var originalLibrary = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
    Directory.CreateDirectory(store.AssetDirectory);
    File.WriteAllBytes(Path.Combine(store.AssetDirectory, new string('a', 161) + ".png"), Png());
    Throws<InvalidDataException>(() => store.Backup(zip), "Invalid planned legacy input must fail before an existing backup is overwritten.");
    Assert(File.ReadAllBytes(zip).SequenceEqual(originalZip), "A rejected backup plan must preserve the existing backup bytes.");
    Assert(File.ReadAllBytes(Path.Combine(store.RootPath, "library.json")).SequenceEqual(originalLibrary), "A rejected backup plan must never modify the library.");
    Assert(!Directory.GetFiles(testRoot, "existing-valid-backup.zip.*.tmp").Any(), "Preflight rejection must not publish or leave a partial backup.");
}

void MarkdownFormats()
{
    var note = MarkdownText.Parse("# One\n## Two\n###### Six\n7. First\n1) Second\n99. Third\n\n2) Restart\n- Dash\n* Star\n+ Plus\n> Quoted\nPlain **bold** and *italic* and _other_\n***Both***\nEscaped \\*stars\\* and \\_marks\\_");
    Assert(note.Paragraphs.Take(3).Select(p => p.HeadingLevel).SequenceEqual(new[] { 1, 2, 6 }), "All supported heading levels must be retained.");
    Assert(note.Paragraphs.Skip(3).Take(3).All(p => p.Kind == "ordered") && note.Paragraphs.Skip(3).Take(3).Select(p => p.Number).SequenceEqual(new[] { 7, 8, 9 }), "Consecutive ordered paragraphs must normalize from their first start.");
    Assert(note.Paragraphs[7].Number == 2 && note.Paragraphs[7].Kind == "ordered", "A new numbered list must retain its own starting number.");
    Assert(note.Paragraphs.Skip(8).Take(3).All(p => p.Kind == "bullet") && note.Paragraphs[11].Kind == "quote", "Bullets and quotations need their own paragraph kinds.");
    Assert(note.Paragraphs[12].Runs.Any(r => r.Text == "bold" && r.Bold) && note.Paragraphs[12].Runs.Count(r => r.Italic) == 2, "Bold and both italic shorthand styles must parse.");
    Assert(note.Paragraphs[13].Runs.Single() is { Text: "Both", Bold: true, Italic: true }, "Nested bold and italic must remain combined.");
    Assert(note.Paragraphs[14].Runs.Single() is { Text: "Escaped *stars* and _marks_", Bold: false, Italic: false }, "Escaped emphasis must stay literal.");
    var unsupported = MarkdownText.Parse("####### Seven\n#nospace\n0. Zero\n1000001. Too large\n\\# Literal heading\nword_inside_word");
    Assert(unsupported.Paragraphs.All(p => p.Kind == "body") && unsupported.Paragraphs.All(p => p.Runs.All(r => !r.Bold && !r.Italic)), "Unsupported markers and intraword underscores stay ordinary text.");
}

void MarkdownLiterals()
{
    var source = "```csharp\n# Not a heading\n**not bold**\n```\n    **indented**\n\t_unchanged_\n![**picture**](file:///x.png)\n[**link**](https://example.invalid/a(b))\n<span title='*raw*'>Text</span>\n~~~\n1. literal list\n~~~";
    var note = MarkdownText.Parse(source);
    Assert(note.Paragraphs.All(p => p.Kind == "body"), "Code-looking and external syntax must never create a code or image paragraph.");
    Assert(note.Paragraphs.All(p => p.Runs.All(r => !r.Bold && !r.Italic)), "Literal fences, indentation, links, images and tag attributes must not turn into emphasis.");
    Assert(string.Join("\n", note.Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text)))) == source, "Literal text must survive exactly and must never import external content.");
    var unclosed = MarkdownText.Parse("```\n## literal\n*still literal*");
    Assert(unclosed.Paragraphs.All(p => p.Kind == "body" && p.Runs.All(r => !r.Italic)), "An unclosed fence keeps all remaining lines ordinary literal text.");
}

void MarkdownBounds()
{
    Throws<InvalidDataException>(() => MarkdownText.Parse(new string('x', NoteText.MaxTextLength + 1)), "Oversized input must fail before parsing.");
    Throws<InvalidDataException>(() => MarkdownText.Parse(new string('\n', NoteText.MaxParagraphs)), "The existing paragraph count must remain bounded.");
    Throws<InvalidDataException>(() => MarkdownText.Parse("1000000. Last\n1. Overflow"), "Normalized numbering must respect its declared upper bound.");
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var adversarial = new string('[', 250000) + new string('(', 250000) + new string('*', 250000);
    var parsed = MarkdownText.Parse(adversarial);
    Assert(parsed.Paragraphs.Single().Runs.Single().Text == adversarial, "Long unmatched brackets and markers must stay literal.");
    Assert(timer.Elapsed < TimeSpan.FromSeconds(15), "Unmatched marker parsing must be bounded rather than repeatedly rescanning the note.");
}

void TextFormatsAndColors()
{
    Assert(NoteColors.IsValid(null) && NoteColors.IsValid("#aBc012") && NoteColors.Normalize("#aBc012") == "#ABC012", "Colors accept only six hexadecimal digits and normalize case.");
    foreach (var value in new[] { "", "red", "#123", "#12345678", "#12ZZ34", " #123456", "#１２３４５６" })
    {
        Assert(!NoteColors.IsValid(value), "Invalid color spellings must be rejected.");
        Throws<ArgumentException>(() => NoteColors.Normalize(value), "Normalization must reject an invalid color.");
    }
    var store = Store("formats-colors");
    var card = Card();
    var content = new NoteContent { Paragraphs = [
        new NoteParagraph { Kind = "heading", HeadingLevel = 6, Runs = [new NoteRun { Text = "Colored", Foreground = "#aBc012", Background = "#ffe099", Bold = true }] },
        new NoteParagraph { Kind = "ordered", Number = 87, Runs = [new NoteRun { Text = "Numbered" }] },
        new NoteParagraph { Kind = "quote", Runs = [new NoteRun { Text = "Quoted", Italic = true }] }
    ] };
    NoteText.SetContent(card, content);
    content.Paragraphs[0].HeadingLevel = 1;
    content.Paragraphs[0].Runs[0].Foreground = "#000000";
    store.Upsert(card);
    var reopened = new LibraryStore(store.RootPath);
    reopened.Load();
    var saved = NoteText.Read(reopened.Cards.Single());
    Assert(saved.Paragraphs[0].HeadingLevel == 6 && saved.Paragraphs[0].Runs[0] is { Foreground: "#ABC012", Background: "#FFE099" } && saved.Paragraphs[1].Number == 87, "New text formats must deep-copy, normalize and survive persistence.");
    saved.Paragraphs[0].Runs[0].Background = null;
    Assert(NoteText.Read(reopened.Cards.Single()).Paragraphs[0].Runs[0].Background == "#FFE099", "Read snapshots must also isolate selected-text highlights.");
    var bytes = File.ReadAllBytes(Path.Combine(store.RootPath, "library.json"));
    foreach (var invalid in new[] {
        new NoteParagraph { HeadingLevel = 0 }, new NoteParagraph { HeadingLevel = 7 },
        new NoteParagraph { Number = 0 }, new NoteParagraph { Number = 1000001 },
        new NoteParagraph { Runs = [new NoteRun { Foreground = "red" }] }, new NoteParagraph { Runs = [new NoteRun { Background = "#123" }] }
    })
    {
        var attempt = Card(); attempt.Note = new NoteContent { Paragraphs = [invalid] };
        Throws<InvalidDataException>(() => store.Upsert(attempt), "Out-of-range paragraph styles and unsafe colors must not save.");
        Assert(File.ReadAllBytes(Path.Combine(store.RootPath, "library.json")).SequenceEqual(bytes), "Rejected styles must preserve the existing library bytes.");
    }
}

void SchemaCompatibility()
{
    foreach (var version in new[] { 1, 2, 3, 4, 5 })
    {
        var card = Card("Version " + version);
        ReviewScheduler.Apply(card, ReviewRating.Hard, now);
        card.Blocks = [new NoteBlock { Text = "Legacy source", Bold = true }];
        var payload = JsonNode.Parse(LibraryBytes(card, version))!;
        payload["cards"]![0]!.AsObject().Remove("collection");
        payload["cards"]![0]!["reviews"]![0]!.AsObject().Remove("automatic");
        if (version != 1)
        {
            if (version < 4) payload["cards"]![0]!["note"]!.AsObject().Remove("images");
            var paragraph = payload["cards"]![0]!["note"]!["paragraphs"]![0]!.AsObject();
            paragraph.Remove("headingLevel"); paragraph.Remove("number");
            paragraph["runs"]![0]!.AsObject().Remove("foreground");
            paragraph["runs"]![0]!.AsObject().Remove("background");
        }
        var root = Area("schema-" + version);
        File.WriteAllText(Path.Combine(root, "library.json"), payload.ToJsonString());
        var store = new LibraryStore(root); store.Load();
        var read = store.Cards.Single();
        var paragraphRead = NoteText.Read(read).Paragraphs.Single();
        Assert(paragraphRead.HeadingLevel == 2 && paragraphRead.Number == 1 && paragraphRead.Runs[0].Foreground is null && paragraphRead.Runs[0].Background is null, "Old text fields need compatible defaults.");
        Assert(read.Collection == NoteCollection.Notes && !read.Reviews.Single().Automatic && read.Memory.Repetitions == 1 && read.Blocks.Single().Text == "Legacy source" && NoteText.Read(read).Images.Count == 0, "Old review logs, original legacy blocks, collection and optional image defaults must remain intact.");
        store.Upsert(read);
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "library.json")));
        Assert(file.RootElement.GetProperty("version").GetInt32() == 5, "Every new write must publish schema 5.");
    }
}

void AutomaticBrowse()
{
    var automatic = Card();
    var manual = Card();
    ReviewScheduler.RecordBrowse(automatic, now);
    ReviewScheduler.Apply(manual, ReviewRating.Good, now);
    Assert(automatic.Memory.Repetitions == 1 && automatic.Reviews.Count == 1 && automatic.Reviews[0] is { Rating: ReviewRating.Good, Automatic: true }, "A browse must add exactly one automatic Good log.");
    Assert(JsonSerializer.Serialize(automatic.Memory) == JsonSerializer.Serialize(manual.Memory), "Automatic browse must use unchanged FSRS Good equations and intervals.");
    Assert(!manual.Reviews.Single().Automatic, "Explicit legacy ratings must remain distinguishable.");
    var previous = automatic.Reviews[0];
    ReviewScheduler.RecordBrowse(automatic, automatic.Memory.DueAt);
    Assert(automatic.Memory.Repetitions == 2 && automatic.Reviews.Count == 2 && automatic.Reviews.All(r => r.Automatic) && ReferenceEquals(previous, automatic.Reviews[0]), "Each accepted browse updates once without rewriting earlier history.");
    var before = JsonSerializer.Serialize(automatic);
    Throws<ArgumentOutOfRangeException>(() => ReviewScheduler.RecordBrowse(automatic, now.AddDays(-1)), "Clock reversal must remain protected.");
    Assert(JsonSerializer.Serialize(automatic) == before, "A rejected browse cannot partly mutate state.");
}

void DraftPersistence()
{
    var store = Store("draft-persistence");
    var draft = new ComposerDraft { CardId = Guid.NewGuid().ToString("N"), Content = MarkdownText.Parse("# Unsent\n3. Detail") };
    store.SaveDraft(draft);
    draft.Content.Paragraphs[0].Runs[0].Text = "Mutated caller";
    Assert(store.Cards.Count == 0, "Saving a composer draft must never add a card to the feed.");
    var reopened = new LibraryStore(store.RootPath); reopened.Load();
    var first = reopened.LoadDraft()!;
    Assert(first.CardId == draft.CardId && first.Content.Paragraphs[0].Runs[0].Text == "Unsent", "Draft content and its stable identity must survive reopening.");
    first.Content.Paragraphs[0].Runs[0].Text = "Changed snapshot";
    Assert(reopened.LoadDraft()!.Content.Paragraphs[0].Runs[0].Text == "Unsent", "Draft read snapshots must be independent.");
    var submitted = Card("Sent draft"); submitted.Id = draft.CardId;
    NoteText.SetContent(submitted, reopened.LoadDraft()!.Content);
    reopened.Upsert(submitted); reopened.Upsert(submitted);
    var crashBetweenSendAndClear = new LibraryStore(store.RootPath); crashBetweenSendAndClear.Load();
    Assert(crashBetweenSendAndClear.LoadDraft() is null, "A crash after posting but before draft clearing cannot resurrect an already-sent composer.");
    Throws<InvalidOperationException>(() => crashBetweenSendAndClear.SaveDraft(draft), "A stale autosave must not re-create a submitted draft.");
    Assert(File.ReadAllText(Path.Combine(store.RootPath, "draft.json")).Trim() == "null", "Reading an already-sent valid draft must finish its interrupted clear before a later card deletion can resurrect it.");
    reopened.LoadDraft(); // Explicitly reload after the other store's crash recovery changed the draft files.
    reopened.ClearDraft();
    var posted = new LibraryStore(store.RootPath); posted.Load();
    Assert(posted.Cards.Count == 1 && posted.Cards.Single().Id == draft.CardId && posted.LoadDraft() is null, "Retrying a send must upsert the same card and clearing the draft must persist.");
    Assert(File.ReadAllText(Path.Combine(store.RootPath, "draft.previous.json")).Trim() == "null", "A successful clear also tombstones the recovery copy.");
}

void DraftSafety()
{
    var store = Store("draft-safety"); store.Upsert(Card("Saved independently"));
    var draft = new ComposerDraft { Content = Content("First complete draft") };
    store.SaveDraft(draft);
    draft.Content = Content("Second complete draft"); store.SaveDraft(draft);
    var primary = Path.Combine(store.RootPath, "draft.json");
    var previous = Path.Combine(store.RootPath, "draft.previous.json");
    var bytes = File.ReadAllBytes(primary);
    using (var locked = new FileStream(primary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var rejected = false;
        try { store.SaveDraft(new ComposerDraft { Content = Content("Must not replace locked draft") }); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "A draft file access failure must be reported.");
    }
    Assert(File.ReadAllBytes(primary).SequenceEqual(bytes), "A failed draft save must preserve the last complete draft.");
    Throws<InvalidDataException>(() => store.SaveDraft(new ComposerDraft { Content = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Foreground = "invalid" }] }] } }), "Invalid drafts must fail before writing.");
    Assert(File.ReadAllBytes(primary).SequenceEqual(bytes), "Invalid draft content cannot overwrite the existing file.");
    var second = new LibraryStore(store.RootPath); second.Load(); second.LoadDraft();
    second.SaveDraft(new ComposerDraft { CardId = draft.CardId, Content = Content("External complete draft") });
    var external = File.ReadAllBytes(primary);
    Throws<InvalidOperationException>(() => store.SaveDraft(draft), "Concurrent external draft edits require an explicit reload.");
    Assert(File.ReadAllBytes(primary).SequenceEqual(external), "A stale composer must not overwrite a newer draft.");
    File.WriteAllText(primary, "{damaged draft");
    var damaged = File.ReadAllBytes(primary);
    var recovery = new LibraryStore(store.RootPath); recovery.Load();
    Assert(recovery.Cards.Count == 1, "A damaged unsent draft cannot block the regular note collection.");
    Assert(recovery.LoadDraft()?.Content.Paragraphs.Single().Runs.Single().Text == "Second complete draft" && recovery.DraftRecoveryNotice is not null && recovery.DraftSaveBlocked, "The previous complete draft must remain available with a recoverable notice and explicit write-block flag.");
    Throws<InvalidDataException>(() => recovery.SaveDraft(draft), "Autosave must not erase a damaged original.");
    Throws<InvalidDataException>(recovery.ClearDraft, "Clearing cannot silently erase an unresolved damaged original.");
    recovery.Upsert(Card("Notes remain writable"));
    Assert(File.ReadAllBytes(primary).SequenceEqual(damaged) && File.Exists(previous), "Draft recovery must preserve original evidence while regular notes remain writable.");
    File.WriteAllText(previous, "{damaged previous draft");
    var both = new LibraryStore(store.RootPath); both.Load();
    Assert(both.LoadDraft() is null && both.DraftRecoveryNotice is not null && both.Cards.Count == 2, "Two damaged draft copies must produce a notice without inventing content or blocking saved notes.");
    var missing = Store("draft-missing-primary"); missing.SaveDraft(new ComposerDraft { Content = Content("Recoverable previous draft") });
    File.Delete(Path.Combine(missing.RootPath, "draft.json"));
    var missingReopen = new LibraryStore(missing.RootPath); missingReopen.Load();
    var recoveredMissing = missingReopen.LoadDraft();
    Assert(recoveredMissing is not null && missingReopen.DraftRecoveryNotice is not null && !missingReopen.DraftSaveBlocked, "A missing primary with a valid recovery copy is a nonblocking notice.");
    missingReopen.SaveDraft(recoveredMissing!);
}

void DraftBackup()
{
    var source = Store("draft-backup-source"); source.Upsert(Card());
    var draft = new ComposerDraft { Content = MarkdownText.Parse("> Unsent detail") };
    source.SaveDraft(draft);
    var zip = Path.Combine(testRoot, "draft-backup.zip"); source.Backup(zip);
    using (var archive = ZipFile.OpenRead(zip)) Assert(archive.GetEntry("draft.json") is not null, "A valid nonempty draft must be included in the backup manifest and archive.");
    var target = Store("draft-backup-target"); target.Restore(zip);
    Assert(target.LoadDraft() is { } restored && restored.CardId == draft.CardId && restored.Content.Paragraphs.Single().Kind == "quote", "Restore must retain an optional draft and its stable posting identity.");
    var before = File.ReadAllBytes(Path.Combine(target.RootPath, "library.json"));
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
    {
        archive.GetEntry("draft.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("draft.json").Open()); writer.Write("invalid");
    }
    Throws<InvalidDataException>(() => target.Restore(zip), "Invalid optional draft data cannot replace a healthy library.");
    Assert(File.ReadAllBytes(Path.Combine(target.RootPath, "library.json")).SequenceEqual(before), "A failed draft restore preserves the current saved notes.");
    var invalid = Store("invalid-draft-backup"); invalid.Upsert(Card("Healthy saved note"));
    var invalidPrimary = Path.Combine(invalid.RootPath, "draft.json");
    var invalidPrevious = Path.Combine(invalid.RootPath, "draft.previous.json");
    File.WriteAllText(invalidPrimary, "{invalid current draft"); File.WriteAllText(invalidPrevious, "{invalid previous draft");
    var invalidZip = Path.Combine(testRoot, "invalid-draft-omitted.zip"); invalid.Backup(invalidZip);
    using (var archive = ZipFile.OpenRead(invalidZip))
        Assert(archive.GetEntry("draft.json") is null && archive.GetEntry("library.json") is not null, "An invalid draft without valid recovery is omitted while healthy notes can still be backed up.");
    Assert(invalid.DraftRecoveryNotice is not null && File.ReadAllText(invalidPrimary) == "{invalid current draft" && File.ReadAllText(invalidPrevious) == "{invalid previous draft", "Read-only backup inspection must report draft damage and preserve both original draft files.");
    invalid.Upsert(Card("Regular notes remain writable"));
    Assert(invalid.Cards.Count == 2, "Damaged draft backup inspection must not disable saving healthy cards.");
}

void DraftBackupIsolation()
{
    var source = Store("draft-backup-isolation"); source.Upsert(Card());
    var draft = new ComposerDraft { Content = Content("Original composer content") }; source.SaveDraft(draft);
    var zip = Path.Combine(testRoot, "draft-isolation-existing-backup.zip"); source.Backup(zip);
    var originalZip = File.ReadAllBytes(zip);
    var external = new LibraryStore(source.RootPath); external.Load(); external.LoadDraft();
    external.SaveDraft(new ComposerDraft { CardId = draft.CardId, Content = Content("Newer external content") });
    var primary = Path.Combine(source.RootPath, "draft.json");
    var newest = File.ReadAllBytes(primary);
    Throws<InvalidOperationException>(() => source.Backup(zip), "Backing up a stale composer must require an explicit reload before adopting an external draft change.");
    Throws<InvalidOperationException>(() => source.SaveDraft(draft), "A backup attempt cannot bless external hashes and let stale autosave overwrite newer content.");
    Assert(File.ReadAllBytes(primary).SequenceEqual(newest) && File.ReadAllBytes(zip).SequenceEqual(originalZip), "Rejected backup and stale autosave must preserve the external draft and existing completed backup.");
    Assert(source.LoadDraft()!.Content.Paragraphs.Single().Runs.Single().Text == "Newer external content", "Only an explicit draft reload may adopt the newer draft.");
    source.Backup(zip);
    var restored = Store("draft-isolation-restored"); restored.Restore(zip);
    Assert(restored.LoadDraft()!.Content.Paragraphs.Single().Runs.Single().Text == "Newer external content", "After an explicit reload, backup must export the newly accepted draft.");
}

void PortableMigration()
{
    var software = Area("portable-app");
    var oldRoot = Path.Combine(software, "Data"); Directory.CreateDirectory(Path.Combine(oldRoot, "assets"));
    var card = Card("Portable original"); card.Note = null;
    card.Blocks = [new NoteBlock { Text = "Portable legacy text" }, new NoteBlock { Kind = "image", AssetName = "portable-original.png", Y = 80 }];
    ReviewScheduler.RecordBrowse(card, now);
    File.WriteAllBytes(Path.Combine(oldRoot, "assets", "portable-original.png"), Png());
    WriteLegacyLibrary(oldRoot, card);
    var old = new LibraryStore(oldRoot); old.Load();
    old.SaveDraft(new ComposerDraft { Content = Content("Unsent portable draft") });
    var backup = Path.Combine(software, "Backups", "old-backup.zip"); old.Backup(backup);
    var original = File.ReadAllBytes(Path.Combine(old.RootPath, "library.json"));
    var draftOriginal = File.ReadAllBytes(Path.Combine(old.RootPath, "draft.json"));
    var paths = PortableStorage.Prepare(software);
    Assert(paths.LibraryPath == Path.Combine(software, "Log", "Data") && paths.BackupPath == Path.Combine(software, "Log", "Backups") && paths.MigrationNotice is not null, "Portable usage data needs dedicated Data and Backups folders under Log.");
    var migrated = new LibraryStore(paths.LibraryPath); migrated.Load();
    Assert(migrated.Cards.Single().Id == card.Id && migrated.Cards.Single().Reviews.Single().Automatic && migrated.LoadDraft()!.Content.Paragraphs.Single().Runs.Single().Text == "Unsent portable draft", "Migration must retain IDs, review history and unsent content.");
    Assert(File.ReadAllBytes(Path.Combine(migrated.AssetDirectory, "portable-original.png")).SequenceEqual(Png()) && File.ReadAllBytes(Path.Combine(old.AssetDirectory, "portable-original.png")).SequenceEqual(Png()), "Original legacy assets must be copied and preserved byte-for-byte.");
    Assert(File.ReadAllBytes(Path.Combine(paths.BackupPath, "old-backup.zip")).SequenceEqual(File.ReadAllBytes(backup)), "Existing backups must be copied byte-for-byte.");
    Assert(File.ReadAllBytes(Path.Combine(old.RootPath, "library.json")).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(old.RootPath, "draft.json")).SequenceEqual(draftOriginal), "Portable migration must preserve all original files.");
    var updated = migrated.Cards.Single(); updated.Title = "Active new portable data"; migrated.Upsert(updated);
    var again = PortableStorage.Prepare(software);
    var reopened = new LibraryStore(again.LibraryPath); reopened.Load();
    Assert(reopened.Cards.Single().Title == "Active new portable data" && again.MigrationNotice is null, "The migration marker must prevent preserved stale Data from overwriting later active notes.");
    Assert(!Directory.EnumerateDirectories(Path.Combine(software, "Log"), ".migration-*").Any(), "Completed migration must remove only its own temporary staging directory.");
}

void PortableMigrationSafety()
{
    var software = Area("portable-conflict");
    var old = new LibraryStore(Path.Combine(software, "Data")); old.Load(); old.Upsert(Card("Old source"));
    var active = new LibraryStore(Path.Combine(software, "Log", "Data")); active.Load(); active.Upsert(Card("Conflicting active source"));
    var beforeOld = File.ReadAllBytes(Path.Combine(old.RootPath, "library.json"));
    var beforeActive = File.ReadAllBytes(Path.Combine(active.RootPath, "library.json"));
    Throws<InvalidDataException>(() => PortableStorage.Prepare(software), "Different old and new libraries must never silently merge.");
    Assert(File.ReadAllBytes(Path.Combine(old.RootPath, "library.json")).SequenceEqual(beforeOld) && File.ReadAllBytes(Path.Combine(active.RootPath, "library.json")).SequenceEqual(beforeActive), "Rejected migration must preserve both competing libraries.");
    var damaged = Area("portable-damaged"); Directory.CreateDirectory(Path.Combine(damaged, "Data"));
    File.WriteAllText(Path.Combine(damaged, "Data", "library.json"), "invalid old library");
    Throws<InvalidDataException>(() => PortableStorage.Prepare(damaged), "An unverified old library cannot be installed into active Log storage.");
    Assert(File.ReadAllText(Path.Combine(damaged, "Data", "library.json")) == "invalid old library" && !File.Exists(Path.Combine(damaged, "Log", "Data", "library.json")), "A failed migration must leave the old evidence untouched and not install a partial library.");
    var fresh = PortableStorage.Prepare(Area("portable-fresh"));
    Assert(Directory.Exists(fresh.LibraryPath) && Directory.Exists(fresh.BackupPath) && fresh.MigrationNotice is null, "A new deployment must initialize only its Log storage directories.");
    var partial = Area("portable-partial");
    var partialOld = new LibraryStore(Path.Combine(partial, "Data")); partialOld.Load(); partialOld.Upsert(Card("Complete source for retry"));
    Directory.CreateDirectory(Path.Combine(partial, "Log", "Data"));
    File.Copy(Path.Combine(partialOld.RootPath, "library.json"), Path.Combine(partial, "Log", "Data", "library.json"));
    var resumed = PortableStorage.Prepare(partial);
    var resumedStore = new LibraryStore(resumed.LibraryPath); resumedStore.Load();
    Assert(resumedStore.Cards.Single().Title == "Complete source for retry" && File.Exists(Path.Combine(resumed.LibraryPath, "library.previous.json")), "A matching partial migration must resume and copy its missing recovery file without combining conflicting saves.");
    var recoveredPartial = Area("portable-recovered-partial");
    var recoverableOld = new LibraryStore(Path.Combine(recoveredPartial, "Data")); recoverableOld.Load(); recoverableOld.Upsert(Card("Valid previous save for retry"));
    File.WriteAllText(Path.Combine(recoverableOld.RootPath, "library.json"), "damaged old primary");
    Directory.CreateDirectory(Path.Combine(recoveredPartial, "Log", "Data"));
    File.Copy(Path.Combine(recoverableOld.RootPath, "library.previous.json"), Path.Combine(recoveredPartial, "Log", "Data", "library.json"));
    var repaired = PortableStorage.Prepare(recoveredPartial);
    var repairedStore = new LibraryStore(repaired.LibraryPath); repairedStore.Load();
    Assert(repairedStore.Cards.Single().Title == "Valid previous save for retry" && File.ReadAllText(Path.Combine(recoverableOld.RootPath, "library.json")) == "damaged old primary", "A resumed recovered migration must compare the verified staged result while preserving the damaged source evidence.");
}

static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
static byte[] Jpeg() => Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD9U6KKKAP/2Q==");
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}

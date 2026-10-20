using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Folio.Core;

/// <summary>Local author identity checks use generated assets and isolated libraries only.</summary>
internal static class ProfileChecks
{
    public static void RoundTrip(string testRoot)
    {
        var store = Store(testRoot, "profile-round-trip");
        var original = store.LoadProfile();
        Assert(original.DisplayName == "You" && original.Avatar is null && !File.Exists(Path.Combine(store.RootPath, "profile.json")),
            "An older library must receive an independent default without creating a profile file.");
        original.DisplayName = "Unpersisted";
        Assert(store.LoadProfile().DisplayName == "You", "Returned defaults must not become in-memory stored state.");
        var image = store.ImportImage(Png(), 1, 1);
        var avatarPath = store.GetImagePath(image);
        var profile = new UserProfile { DisplayName = "  Cafe\u0301 📝  ", Avatar = image };
        store.SaveProfile(profile);
        profile.DisplayName = "Changed caller";
        profile.Avatar!.AssetName = "bad.png";
        var reloaded = store.LoadProfile();
        Assert(reloaded.DisplayName == "Café 📝" && reloaded.Avatar!.PixelWidth == 1 && reloaded.Avatar!.AssetName.EndsWith(".png"),
            "Saving must normalize and trim names while taking an independent image reference.");
        reloaded.Avatar!.AssetName = "bad.png";
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        Assert(reopened.LoadProfile().DisplayName == "Café 📝" && reopened.LoadProfile().Avatar!.AssetName != "bad.png",
            "Both disk reload and returned profiles must remain independent.");
        reopened.SaveProfile(new UserProfile { DisplayName = string.Concat(Enumerable.Repeat("📝", 80)) });
        Assert(reopened.LoadProfile().Avatar is null, "Removing the avatar must persist without deleting retained image assets.");
        Assert(File.Exists(avatarPath), "Replacing a profile must retain its former immutable avatar asset.");
    }

    public static void Safety(string testRoot)
    {
        var store = Store(testRoot, "profile-safety");
        store.SaveProfile(new UserProfile { DisplayName = "Owner" });
        var path = Path.Combine(store.RootPath, "profile.json");
        var saved = File.ReadAllBytes(path);
        foreach (var name in new[] { "", "  ", "line\nbreak", "tab\there", new string('x', 81), "\ud800" })
            Throws<InvalidDataException>(() => store.SaveProfile(new UserProfile { DisplayName = name }), "Invalid display names must be rejected.");
        foreach (var avatar in new[]
        {
            new NoteImage { AssetName = "../escape.png", PixelWidth = 1, PixelHeight = 1 },
            new NoteImage { AssetName = new string('a', 64) + ".png", PixelWidth = 1, PixelHeight = 1 },
            new NoteImage { AssetName = new string('a', 64) + ".png", PixelWidth = -1, PixelHeight = 1 }
        })
            Throws<InvalidDataException>(() => store.SaveProfile(new UserProfile { DisplayName = "Owner", Avatar = avatar }), "Invalid or missing avatar references must be rejected.");
        Assert(File.ReadAllBytes(path).SequenceEqual(saved), "Validation failures must leave the saved profile untouched.");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => store.SaveProfile(new UserProfile { DisplayName = "Locked change" }), "A locked destination must fail without replacing the current identity.");
        Assert(File.ReadAllBytes(path).SequenceEqual(saved) && !Directory.EnumerateFiles(store.RootPath, "profile.json.*.tmp").Any(),
            "An interrupted profile replacement must preserve its former bytes and clean its own temporary file.");
        store.SaveProfile(new UserProfile { DisplayName = "Retried" });
        Assert(store.LoadProfile().DisplayName == "Retried", "A temporary file lock must permit a subsequent save.");
    }

    public static void RecoveryAndConflicts(string testRoot)
    {
        var store = Store(testRoot, "profile-recovery");
        store.SaveProfile(new UserProfile { DisplayName = "Original" });
        var stale = new LibraryStore(store.RootPath);
        stale.Load();
        stale.LoadProfile();
        store.SaveProfile(new UserProfile { DisplayName = "External" });
        Throws<InvalidOperationException>(() => stale.SaveProfile(new UserProfile { DisplayName = "Stale" }), "A stale profile must not overwrite an external change.");
        Assert(stale.LoadProfile().DisplayName == "External", "An explicit reload must adopt a valid external profile.");
        var path = Path.Combine(store.RootPath, "profile.json");
        File.WriteAllText(path, "{ damaged synthetic profile");
        var damaged = File.ReadAllBytes(path);
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        Assert(reopened.LoadProfile().DisplayName == "You" && reopened.ProfileSaveBlocked && reopened.ProfileRecoveryNotice is not null,
            "A damaged profile must return a safe default and independently block profile writes.");
        Throws<InvalidDataException>(() => reopened.SaveProfile(new UserProfile { DisplayName = "Do not overwrite" }), "A damaged profile must remain protected.");
        reopened.Upsert(new CardDocument { Title = "Independent saved note" });
        reopened.SaveDraft(new ComposerDraft { Content = Content("Independent draft") });
        Assert(reopened.Cards.Count == 1 && reopened.LoadDraft() is not null && File.ReadAllBytes(path).SequenceEqual(damaged),
            "A damaged profile must not disable notes or drafts, or replace the damaged file.");
        var backup = Path.Combine(testRoot, "profile-corrupt-backup.zip");
        File.WriteAllText(backup, "Existing backup must survive");
        Throws<InvalidDataException>(() => reopened.Backup(backup), "Backup must report an invalid profile instead of silently omitting it.");
        Assert(File.ReadAllText(backup) == "Existing backup must survive", "A failed profile backup must preserve the existing backup destination.");
    }

    public static void BackupAndLegacy(string testRoot)
    {
        var store = Store(testRoot, "profile-backup-source");
        var image = store.ImportImage(Png(), 1, 1);
        store.SaveProfile(new UserProfile { DisplayName = "Local author", Avatar = image });
        var backup = Path.Combine(testRoot, "profile-backup.zip");
        store.Backup(backup);
        using (var zip = ZipFile.OpenRead(backup))
            Assert(zip.GetEntry("profile.json") is not null && zip.GetEntry("assets/" + image.AssetName) is not null,
                "A backup must include the profile and its validated avatar asset.");
        var restored = Store(testRoot, "profile-restored");
        restored.Restore(backup);
        var profile = restored.LoadProfile();
        Assert(profile.DisplayName == "Local author" && profile.Avatar!.AssetName == image.AssetName && File.ReadAllBytes(restored.GetImagePath(profile.Avatar!)).SequenceEqual(Png()),
            "Backup and restore must retain the complete author identity and asset.");
        var legacy = Store(testRoot, "profile-legacy");
        var legacyBackup = Path.Combine(testRoot, "profile-legacy.zip");
        legacy.Backup(legacyBackup);
        restored.Restore(legacyBackup);
        Assert(restored.LoadProfile().DisplayName == "You" && restored.LoadProfile().Avatar is null && !restored.ProfileSaveBlocked,
            "A legacy archive without a profile must restore successfully with a default identity.");
        restored.SaveProfile(new UserProfile { DisplayName = "Healthy target" });
        var targetBytes = File.ReadAllBytes(Path.Combine(restored.RootPath, "profile.json"));
        var invalidBackup = Path.Combine(testRoot, "profile-invalid-reference.zip");
        MakeInvalidProfileBackup(backup, invalidBackup);
        Throws<InvalidDataException>(() => restored.Restore(invalidBackup), "A checksummed archive with an invalid avatar must fail before replacing the library.");
        Assert(File.ReadAllBytes(Path.Combine(restored.RootPath, "profile.json")).SequenceEqual(targetBytes) && restored.LoadProfile().DisplayName == "Healthy target",
            "A rejected profile restore must preserve the complete current target.");
    }

    private static void MakeInvalidProfileBackup(string source, string destination)
    {
        using var input = ZipFile.OpenRead(source);
        var files = input.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
        files["profile.json"] = Encoding.UTF8.GetBytes("{\"displayName\":\"Unsafe avatar\",\"avatar\":{\"assetName\":\"../escape.png\",\"pixelWidth\":1,\"pixelHeight\":1}}");
        var manifest = JsonNode.Parse(files["backup-manifest.json"])!;
        manifest["files"]!["profile.json"]!["length"] = files["profile.json"].Length;
        manifest["files"]!["profile.json"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(files["profile.json"]));
        files["backup-manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var stream = output.CreateEntry(file.Key).Open();
            stream.Write(file.Value);
        }
    }

    private static NoteContent Content(string text) => new() { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = text }] }] };
    private static LibraryStore Store(string root, string name)
    {
        var store = new LibraryStore(Path.Combine(root, name));
        store.Load();
        return store;
    }
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

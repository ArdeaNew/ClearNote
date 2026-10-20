using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Folio.Core;

/// <summary>Storage checks use only generated image bytes and isolated test directories.</summary>
internal static class CoreImageStorageChecks
{
    public static void ConcurrentImport(string testRoot)
    {
        var root = Path.Combine(testRoot, "concurrent-image-import");
        var stores = Enumerable.Range(0, 8).Select(_ => new LibraryStore(root)).ToArray();
        foreach (var store in stores) store.Load();
        for (var round = 0; round < 4; round++)
        {
            var bytes = PngWithComment(round, 1024 * 1024);
            using var start = new Barrier(stores.Length);
            var results = stores.Select(store => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                try { return (Image: store.ImportImage(bytes, 1, 1), Error: (Exception?)null); }
                catch (Exception error) { return (Image: (NoteImage?)null, Error: error); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(results);
            var failures = results.Where(result => result.Result.Error is not null).Select(result => result.Result.Error!).ToArray();
            Assert(failures.Length == 0, "Concurrent imports of equal bytes must all reuse one complete asset: " +
                string.Join(" | ", failures.Select(error => error.GetType().Name + ": " + error.Message)));
            var names = results.Select(result => result.Result.Image!.AssetName).Distinct().ToArray();
            Assert(names.Length == 1, "Concurrent imports must agree on the immutable asset name.");
            var path = stores[0].GetImagePath(results[0].Result.Image!);
            Assert(File.ReadAllBytes(path).SequenceEqual(bytes), "The published asset must contain the complete imported bytes.");
            Assert(!Directory.EnumerateFiles(stores[0].AssetDirectory, "*.tmp").Any(), "Concurrent imports must clean their temporary files.");
        }
    }

    public static void FailedWritesAndRetry(string testRoot)
    {
        var store = new LibraryStore(Path.Combine(testRoot, "image-storage-failures"));
        store.Load();
        var draft = new ComposerDraft { Content = new NoteContent { Paragraphs = [new NoteParagraph { Runs = [new NoteRun { Text = "Existing unsent text" }] }] } };
        store.SaveDraft(draft);
        var draftPath = Path.Combine(store.RootPath, "draft.json");
        var originalDraft = File.ReadAllBytes(draftPath);
        var bytes = PngWithComment(30, 0);

        File.WriteAllText(store.AssetDirectory, "Synthetic file blocking the assets directory");
        Throws<IOException>(() => store.ImportImage(bytes, 1, 1), "A blocked assets directory must report a write failure.");
        Assert(File.ReadAllBytes(draftPath).SequenceEqual(originalDraft) && store.Cards.Count == 0,
            "A failed import must preserve the unsent draft and saved notes.");
        File.Delete(store.AssetDirectory);

        var image = store.ImportImage(bytes, 1, 1);
        var path = store.GetImagePath(image);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Throws<IOException>(() => store.ImportImage(bytes, 1, 1), "An unreadable existing asset must be reported without replacement.");
        Assert(store.ImportImage(bytes, 1, 1).AssetName == image.AssetName && File.ReadAllBytes(path).SequenceEqual(bytes),
            "A temporary access failure must permit a complete retry without invalidating the store.");

        var secondBytes = PngWithComment(31, 0);
        var blockedDestination = Path.Combine(store.AssetDirectory, Convert.ToHexStringLower(SHA256.HashData(secondBytes)) + ".png");
        Directory.CreateDirectory(blockedDestination);
        Throws<IOException>(() => store.ImportImage(secondBytes, 1, 1), "An occupied destination must fail before a reference is returned.");
        Assert(!Directory.EnumerateFiles(store.AssetDirectory, "*.tmp").Any(), "Failed publication must clean only its temporary asset file.");
        Directory.Delete(blockedDestination);
        var retry = store.ImportImage(secondBytes, 1, 1);
        draft.Content.Images.Add(retry);
        store.SaveDraft(draft);
        var reopened = new LibraryStore(store.RootPath);
        reopened.Load();
        Assert(reopened.LoadDraft()!.Content.Images.Single().AssetName == retry.AssetName &&
            File.ReadAllBytes(reopened.GetImagePath(retry)).SequenceEqual(secondBytes),
            "After the obstruction is removed, a new image and the complete existing draft must save and reopen.");
    }

    public static void UnicodeAndLongPaths(string testRoot)
    {
        var root = Path.Combine(testRoot, "图片 café e\u0301 📝", new string('a', 80), new string('b', 80), new string('c', 40));
        var store = new LibraryStore(root);
        store.Load();
        var bytes = PngWithComment(40, 0);
        var image = store.ImportImage(bytes, 1, 1);
        var path = store.GetImagePath(image);
        Assert(path.Length > 260 && File.ReadAllBytes(path).SequenceEqual(bytes),
            "Unicode library paths exceeding the legacy Windows path limit must retain imported bytes.");
        var draft = new ComposerDraft { Content = new NoteContent { Images = [image] } };
        store.SaveDraft(draft);
        var reopened = new LibraryStore(root);
        reopened.Load();
        Assert(reopened.LoadDraft()!.Content.Images.Single().AssetName == image.AssetName && reopened.GetImagePath(image) == path,
            "Image-only drafts must reopen at the same Unicode and long library path.");
    }

    private static byte[] PngWithComment(int variant, int padding)
    {
        var original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");
        var comment = Encoding.ASCII.GetBytes("Synthetic\0" + variant + new string('x', padding));
        using var output = new MemoryStream();
        output.Write(original, 0, original.Length - 12);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)comment.Length);
        output.Write(number);
        var type = "tEXt"u8;
        output.Write(type);
        output.Write(comment);
        uint crc = uint.MaxValue;
        foreach (var value in type.ToArray().Concat(comment))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
        output.Write(original, original.Length - 12, 12);
        return output.ToArray();
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

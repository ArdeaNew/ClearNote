using System.Text.Json;
using System.Text.Json.Serialization;

namespace Folio.Core;

public sealed partial class LibraryStore
{
    private const string DeletionFile = "delete.pending.json";

    private void DeleteWithAssets(string id, List<CardDocument> remaining)
    {
        ValidateCards(remaining, AssetDirectory);
        EnsureUnchanged();
        var removed = _cards.Single(card => card.Id == id);
        var exclusive = CardAssets([removed]).Except(CardAssets(remaining), StringComparer.Ordinal).ToArray();
        var deletion = new PendingDeletion
        {
            CardId = id,
            Assets = exclusive.Length == 0 ? [] : exclusive.Except(RetainedAssets(remaining), StringComparer.Ordinal).ToList()
        };
        // Common access failures must leave both the note and its pictures intact.
        PreflightDeletion(deletion.Assets);
        var journal = Path.Combine(RootPath, DeletionFile);
        EnsureUnchanged();
        WritePrimary(JsonSerializer.SerializeToUtf8Bytes(deletion, Json), journal);
        CompleteDeletion(deletion);
    }

    private void CompletePendingDeletion()
    {
        var pending = ReadPendingDeletion();
        if (pending is not null) CompleteDeletion(pending);
    }

    private PendingDeletion? ReadPendingDeletion()
    {
        var path = Path.Combine(RootPath, DeletionFile);
        EnsureNoLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("The pending deletion path is occupied by a folder.");
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 2097152)
            throw new InvalidDataException("The pending note deletion is empty or too large. Its file was preserved.");
        PendingDeletion? pending;
        try { pending = JsonSerializer.Deserialize<PendingDeletion>(File.ReadAllBytes(path), Json); }
        catch (JsonException error) { throw new InvalidDataException("The pending note deletion could not be read. Its file was preserved.", error); }
        if (pending is null || pending.Version != 1 || string.IsNullOrWhiteSpace(pending.CardId) ||
            pending.CardId.Length > 128 || pending.Assets is null || pending.Assets.Count > 10004)
            throw new InvalidDataException("The pending note deletion has an unsupported format.");
        foreach (var name in pending.Assets) ValidateAssetName(name);
        if (pending.Assets.Distinct(StringComparer.Ordinal).Count() != pending.Assets.Count)
            throw new InvalidDataException("The pending note deletion contains duplicate picture references.");
        return pending;
    }

    private void CompleteDeletion(PendingDeletion deletion)
    {
        try
        {
            var remaining = Clone(_cards);
            remaining.RemoveAll(card => card.Id == deletion.CardId);
            ValidateCards(remaining, AssetDirectory);
            var retained = deletion.Assets.Count == 0 ? [] : RetainedAssets(remaining);
            var disposable = deletion.Assets.Where(name => !retained.Contains(name)).ToArray();
            var bytes = Serialize(remaining);
            if (bytes.LongLength > MaxDataBytes) throw new InvalidDataException("The library exceeds its size limit.");
            EnsureUnchanged();
            var primary = Path.Combine(RootPath, DataFile);
            var previous = Path.Combine(RootPath, PreviousFile);
            EnsureNoLinks(previous);
            // Keep no recovery copy containing a deleted note. The journal covers a crash
            // between these two atomic replacements without storing any deleted note text.
            WritePrimary(bytes, previous);
            WritePrimary(bytes, primary);
            _knownHash = HashFile(primary);
            _cards = remaining;
            _newLibrary = false;
            foreach (var name in disposable)
            {
                var path = GetAssetPath(name);
                if (Directory.Exists(path)) throw new IOException("A deleted picture path is occupied by a folder.");
                File.Delete(path);
            }
            File.Delete(Path.Combine(RootPath, DeletionFile));
        }
        catch (Exception error) when (IsDataError(error) || error is InvalidOperationException)
        {
            throw new IOException("The note deletion was recorded, but local cleanup could not finish. Resolve the file access problem and reload to finish deleting its local data.", error);
        }
    }

    private HashSet<string> RetainedAssets(List<CardDocument> remaining)
    {
        var retained = CardAssets(remaining).ToHashSet(StringComparer.Ordinal);
        var profile = Path.Combine(RootPath, ProfileFile);
        if (_profileInspected) EnsureProfileUnchanged();
        if (ReadProfileHash() is not null)
        {
            var avatar = ReadProfile(profile, AssetDirectory).Avatar;
            if (avatar is not null) retained.Add(avatar.AssetName);
        }
        foreach (var state in _draftStates.Values.Where(state => state.Inspected)) EnsureDraftUnchanged(state);
        // Both versions can be recovered independently. Never remove a picture still
        // owned by a current or recoverable previous draft, including image-only drafts.
        foreach (var name in new[] { DraftFile, PreviousDraftFile, MemoDraftFile, PreviousMemoDraftFile })
        {
            var path = Path.Combine(RootPath, name);
            EnsureNoLinks(path);
            if (Directory.Exists(path)) throw new InvalidDataException("A draft path is occupied by a folder.");
            if (!File.Exists(path)) continue;
            var draft = ReadDraft(path);
            if (draft is not null) retained.UnionWith(draft.Content.Images.Select(image => image.AssetName));
        }
        return retained;
    }

    private static IEnumerable<string> CardAssets(IEnumerable<CardDocument> cards) => cards
        .SelectMany(card => (card.Note?.Images.Select(image => image.AssetName) ?? [])
            .Concat(card.Blocks.Where(block => block.Kind == "image").Select(block => block.AssetName)))
        .Distinct(StringComparer.Ordinal);

    private void PreflightDeletion(IEnumerable<string> assets)
    {
        foreach (var name in new[] { DataFile, PreviousFile, DeletionFile })
        {
            var path = Path.Combine(RootPath, name);
            EnsureNoLinks(path);
            if (Directory.Exists(path)) throw new IOException("A note data path is occupied by a folder.");
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("A note data file is read-only.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        foreach (var name in assets)
        {
            var path = GetAssetPath(name);
            if (Directory.Exists(path)) throw new IOException("A deleted picture path is occupied by a folder.");
            if (!File.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("A deleted picture is read-only.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class PendingDeletion
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string CardId { get; set; } = "";
        [JsonRequired] public List<string> Assets { get; set; } = [];
    }
}

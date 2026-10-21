using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Folio.Core;

/// <summary>Owns continuous notes, imported pictures, preserved legacy assets and validated backups.</summary>
public sealed partial class LibraryStore
{
    private const string DataFile = "library.json";
    private const string PreviousFile = "library.previous.json";
    private const string ManifestFile = "backup-manifest.json";
    private const string DraftFile = "draft.json";
    private const string PreviousDraftFile = "draft.previous.json";
    private const string MemoDraftFile = "memo-draft.json";
    private const string PreviousMemoDraftFile = "memo-draft.previous.json";
    private const long MaxDataBytes = ArchiveLimits.MaxFileBytes;
    private const long MaxAssetBytes = ArchiveLimits.MaxFileBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();
    private List<CardDocument> _cards = [];
    private bool _loaded;
    private string? _knownHash;
    private readonly Dictionary<NoteCollection, DraftState> _draftStates = [];
    private bool _newLibrary;

    public LibraryStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (Path.GetPathRoot(RootPath) == RootPath)
            throw new ArgumentException("The library must use its own folder below the drive root.", nameof(rootPath));
        AssetDirectory = Path.Combine(RootPath, "assets");
        EnsureNoLinks(RootPath);
    }

    public string RootPath { get; }
    public string AssetDirectory { get; }
    /// <summary>Independent snapshots; call Upsert to persist an edited card.</summary>
    public IReadOnlyList<CardDocument> Cards
    {
        get { lock (_gate) return Clone(_cards).AsReadOnly(); }
    }
    public string? RecoveryNotice { get; private set; }
    /// <summary>Recoverable draft problems never disable the saved note collection.</summary>
    public string? DraftRecoveryNotice => GetDraftRecoveryNotice(NoteCollection.Notes);
    /// <summary>A notice can describe successful recovery; only this flag disables draft writes.</summary>
    public bool DraftSaveBlocked => IsDraftSaveBlocked(NoteCollection.Notes);
    /// <summary>True only after loading a folder which has never contained a saved library.</summary>
    public bool IsNewLibrary
    {
        get
        {
            lock (_gate) return _loaded && _newLibrary &&
                !File.Exists(Path.Combine(RootPath, DataFile)) &&
                !File.Exists(Path.Combine(RootPath, PreviousFile)) &&
                !File.Exists(Path.Combine(RootPath, DeletionFile));
        }
    }

    /// <summary>Creates an entire first-run collection in one save; empty existing collections stay empty.</summary>
    public bool InitializeNewLibrary(IEnumerable<CardDocument> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        lock (_gate)
        {
            RequireLoaded();
            if (!IsNewLibrary) return false;
            var candidate = Clone(cards.ToList());
            Commit(candidate);
            return true;
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _loaded = false;
            _newLibrary = false;
            _draftStates.Clear();
            ResetProfileState();
            RecoveryNotice = null;
            EnsureNoLinks(RootPath);
            var primary = Path.Combine(RootPath, DataFile);
            var previous = Path.Combine(RootPath, PreviousFile);
            var pendingDeletion = ReadPendingDeletion();
            EnsureNoLinks(primary);
            EnsureNoLinks(previous);
            if (Directory.Exists(primary) || Directory.Exists(previous))
                throw new InvalidDataException("A library file path is occupied by a folder.");
            if (!File.Exists(primary) && !File.Exists(previous))
            {
                Directory.CreateDirectory(RootPath);
                _cards = [];
                _knownHash = null;
                _loaded = true;
                _newLibrary = pendingDeletion is null;
                if (pendingDeletion is not null) CompleteDeletion(pendingDeletion);
                return;
            }

            Exception? primaryError = null;
            try
            {
                _cards = ReadLibrary(primary, AssetDirectory, pendingDeletion?.CardId);
            }
            catch (Exception error) when (IsDataError(error))
            {
                primaryError = error;
            }
            if (primaryError is not null)
            {
                try
                {
                    _cards = ReadLibrary(previous, AssetDirectory, pendingDeletion?.CardId);
                }
                catch (Exception error) when (IsDataError(error))
                {
                    throw new InvalidDataException("Neither the library nor its previous save could be read. Saving is disabled to protect your files. Restore a valid backup to continue.",
                        new AggregateException(primaryError, error));
                }
                // Repair the primary atomically without replacing the known-good previous file.
                WritePrimary(pendingDeletion is null ? File.ReadAllBytes(previous) : Serialize(_cards), primary);
                RecoveryNotice = "The library could not be read. Folio recovered the previous complete save.";
            }
            _knownHash = HashFile(primary);
            _loaded = true;
            if (pendingDeletion is not null) CompleteDeletion(pendingDeletion);
        }
    }

    public void Upsert(CardDocument card)
    {
        ArgumentNullException.ThrowIfNull(card);
        lock (_gate)
        {
            RequireLoaded();
            CompletePendingDeletion();
            if (card.Blocks is null || card.Blocks.Any(block => block is null))
                throw new InvalidDataException("A card has missing legacy data.");
            if (!Enum.IsDefined(card.Collection))
                throw new InvalidDataException("A card has an unsupported collection.");
            if (card.Note is not null) NoteText.Validate(card.Note);
            var existing = _cards.FirstOrDefault(saved => saved.Id == card.Id);
            if (card.Blocks.Where(block => block.Kind == "image").Any(block =>
                existing?.Blocks.Any(old => old.Kind == "image" && old.Id == block.Id && old.AssetName == block.AssetName) != true))
                throw new InvalidDataException("New pictures must be imported into the continuous note. Legacy image blocks are preserved for recovery only.");
            if (existing is not null && existing.Collection != card.Collection)
                throw new InvalidOperationException("A saved note cannot move between Notes and Memo.");
            var candidate = Clone(_cards);
            var saved = Clone(new List<CardDocument> { card })[0];
            if (saved.Note is not null) saved.Note = NoteText.Copy(saved.Note);
            var index = candidate.FindIndex(c => c.Id == card.Id);
            if (index < 0) candidate.Add(saved); else candidate[index] = saved;
            Commit(candidate);
        }
    }

    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            RequireLoaded();
            CompletePendingDeletion();
            var candidate = Clone(_cards);
            if (candidate.RemoveAll(c => c.Id == id) == 0) return;
            DeleteWithAssets(id, candidate);
        }
    }

    private string GetAssetPath(string assetName)
    {
        ValidateAssetName(assetName);
        var path = Path.GetFullPath(Path.Combine(AssetDirectory, assetName));
        if (!IsWithin(path, AssetDirectory)) throw new InvalidDataException("The image path is outside the library.");
        EnsureNoLinks(path);
        return path;
    }

    /// <summary>Durably imports a PNG or JPEG before a card or draft references it. Existing assets are never replaced.</summary>
    public NoteImage ImportImage(byte[] bytes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            RequireLoaded();
            if (bytes.Length > NoteImages.MaxImportBytes) throw new InvalidDataException("An imported image must be no larger than 20 MB.");
            var snapshot = bytes.ToArray();
            var extension = NoteImages.ValidateBytes(snapshot, width, height);
            var name = Convert.ToHexStringLower(SHA256.HashData(snapshot)) + "." + extension;
            var image = new NoteImage { AssetName = name, PixelWidth = width, PixelHeight = height };
            var destination = GetAssetPath(name);
            if (File.Exists(destination))
            {
                ValidateImageFile(image, AssetDirectory);
                return image;
            }
            Directory.CreateDirectory(AssetDirectory);
            EnsureNoLinks(AssetDirectory);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurably(temporary, snapshot);
                EnsureNoLinks(destination);
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another store may have published the same immutable asset while this
                    // import was writing. Accept it only after the complete normal checks.
                    ValidateImageFile(image, AssetDirectory);
                }
            }
            finally { TryDeleteFile(temporary); }
            return image;
        }
    }

    /// <summary>Returns a validated local asset path. Missing, changed and linked assets are rejected.</summary>
    public string GetImagePath(NoteImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
        {
            RequireLoaded();
            return ValidateImageFile(image, AssetDirectory);
        }
    }

    public void Backup(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        lock (_gate)
        {
            RequireLoaded();
            CompletePendingDeletion();
            EnsureUnchanged();
            ValidateCards(_cards, AssetDirectory);
            var target = Path.GetFullPath(zipPath);
            if (IsWithin(target, RootPath) || string.Equals(target, RootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Save the backup outside the library folder to protect your library and images.");
            var data = Serialize(_cards);
            var drafts = Enum.GetValues<NoteCollection>().Select(collection =>
            {
                var state = StateFor(collection);
                var snapshot = ReadDraftForBackup(state);
                return (State: state, Snapshot: snapshot, Bytes: snapshot.Draft is null ? null : JsonSerializer.SerializeToUtf8Bytes(snapshot.Draft, Json));
            }).ToArray();
            if (_profileInspected) EnsureProfileUnchanged();
            var profilePath = Path.Combine(RootPath, ProfileFile);
            var profileHash = ReadProfileHash();
            var profileBytes = profileHash is null ? null : JsonSerializer.SerializeToUtf8Bytes(ReadProfile(profilePath, AssetDirectory), Json);
            var assets = PreservedAssetNames().ToDictionary(asset => "assets/" + asset,
                asset => new PlannedAsset(GetAssetPath(asset), new FileInfo(GetAssetPath(asset)).Length), StringComparer.Ordinal);
            var manifest = new BackupManifest();
            manifest.Files[DataFile] = new BackupFile { Length = data.LongLength, Sha256 = new string('0', 64) };
            foreach (var draft in drafts.Where(draft => draft.Bytes is not null))
                manifest.Files[draft.State.File] = new BackupFile { Length = draft.Bytes!.LongLength, Sha256 = new string('0', 64) };
            if (profileBytes is not null)
                manifest.Files[ProfileFile] = new BackupFile { Length = profileBytes.LongLength, Sha256 = new string('0', 64) };
            foreach (var asset in assets)
                manifest.Files[asset.Key] = new BackupFile { Length = asset.Value.Length, Sha256 = new string('0', 64) };
            // Hashes are fixed-width ASCII. Placeholder hashes give the exact final JSON size.
            ValidateBackupPlan(manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    AddEntry(archive, manifest, DataFile, data);
                    foreach (var draft in drafts.Where(draft => draft.Bytes is not null))
                        AddEntry(archive, manifest, draft.State.File, draft.Bytes!);
                    if (profileBytes is not null) AddEntry(archive, manifest, ProfileFile, profileBytes);
                    // Include imported and legacy assets, including those retained for previous saves and drafts.
                    foreach (var asset in assets)
                        AddAssetEntry(archive, manifest, asset.Key, asset.Value);
                    var manifestBytes = ValidateBackupPlan(manifest);
                    var entry = archive.CreateEntry(ManifestFile, CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(manifestBytes);
                }
                EnsureUnchanged();
                foreach (var draft in drafts)
                    EnsureDraftUnchanged(draft.State, draft.Snapshot.PrimaryHash, draft.Snapshot.PreviousHash);
                EnsureProfileUnchanged(profileHash);
                File.Move(temporary, target, true);
            }
            finally { TryDeleteFile(temporary); }
        }
    }

    /// <summary>Replaces this entire library after validation; preserves the former folder beside it.</summary>
    public void Restore(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        lock (_gate)
        {
            EnsureNoLinks(RootPath);
            var parent = Path.GetDirectoryName(RootPath)!;
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, "." + Path.GetFileName(RootPath) + ".restore-" + Guid.NewGuid().ToString("N"));
            var rollback = Path.Combine(parent, Path.GetFileName(RootPath) + ".before-restore-" +
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(staging);
            try
            {
                ExtractVerified(zipPath, staging);
                var restored = ReadLibrary(Path.Combine(staging, DataFile), Path.Combine(staging, "assets"));
                if (File.Exists(Path.Combine(staging, ProfileFile)))
                    _ = ReadProfile(Path.Combine(staging, ProfileFile), Path.Combine(staging, "assets"));
                foreach (var collection in Enum.GetValues<NoteCollection>())
                {
                    var state = StateFor(collection);
                    var path = Path.Combine(staging, state.File);
                    if (!File.Exists(path)) continue;
                    _ = ReadDraft(path);
                    File.Copy(path, Path.Combine(staging, state.PreviousFile));
                }
                File.Copy(Path.Combine(staging, DataFile), Path.Combine(staging, PreviousFile));
                var hadOriginal = Directory.Exists(RootPath);
                if (hadOriginal) Directory.Move(RootPath, rollback);
                try { Directory.Move(staging, RootPath); }
                catch
                {
                    if (hadOriginal && !Directory.Exists(RootPath)) Directory.Move(rollback, RootPath);
                    throw;
                }
                _cards = restored;
                _knownHash = HashFile(Path.Combine(RootPath, DataFile));
                _loaded = true;
                _newLibrary = false;
                _draftStates.Clear();
                ResetProfileState();
                RecoveryNotice = hadOriginal ? $"Backup restored. Your previous library is preserved at: {rollback}" : "Backup restored.";
            }
            finally { TryDeleteStaging(staging, parent); }
        }
    }

    private void Commit(List<CardDocument> cards)
    {
        CompletePendingDeletion();
        ValidateCards(cards, AssetDirectory);
        EnsureUnchanged();
        var bytes = Serialize(cards);
        if (bytes.LongLength > MaxDataBytes) throw new InvalidDataException("The library exceeds 64 MB. Your existing files have not been changed.");
        var primary = Path.Combine(RootPath, DataFile);
        var temporary = primary + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(temporary, bytes);
            if (File.Exists(primary)) File.Replace(temporary, primary, Path.Combine(RootPath, PreviousFile));
            else
            {
                // The first successful save also has a recovery copy.
                WritePrimary(bytes, Path.Combine(RootPath, PreviousFile));
                File.Move(temporary, primary);
            }
            _knownHash = HashFile(primary);
            _cards = cards;
            _newLibrary = false;
        }
        finally { TryDeleteFile(temporary); }
    }

    private void EnsureUnchanged()
    {
        EnsureNoLinks(RootPath);
        var path = Path.Combine(RootPath, DataFile);
        var currentHash = File.Exists(path) ? HashFile(path) : null;
        if (currentHash != _knownHash)
            throw new InvalidOperationException("The library was changed or deleted outside this window. Reload it before saving.");
    }

    private void RequireLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("The library has not loaded successfully. Saving is disabled to protect your existing files.");
    }

    private static List<CardDocument> ReadLibrary(string path, string assets, string? deletedId = null)
    {
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxDataBytes) throw new InvalidDataException("The library file is missing, empty or larger than 64 MB.");
        var payload = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllBytes(path), Json);
        if (payload is null || payload.Version is not (1 or 2 or 3 or 4 or 5) || payload.Cards is null)
            throw new InvalidDataException("This library format or version is not supported.");
        // An interrupted deletion is authoritative even when recovering an older snapshot.
        if (deletedId is not null) payload.Cards.RemoveAll(card => card?.Id == deletedId);
        ValidateCards(payload.Cards, assets);
        return payload.Cards;
    }

    private static void ValidateCards(List<CardDocument> cards, string assets)
    {
        if (cards.Count > 100000) throw new InvalidDataException("The library exceeds the limit of 100,000 cards.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (card is null || string.IsNullOrWhiteSpace(card.Id) || card.Id.Length > 128 || !ids.Add(card.Id) ||
                card.Title is null || card.Blocks is null || card.Memory is null || card.Reviews is null ||
                card.CreatedAt == default || card.UpdatedAt == default || card.Blocks.Count > 10000 || !Enum.IsDefined(card.Collection))
                throw new InvalidDataException("A card is incomplete or has a duplicate ID.");
            if (card.Note is not null)
            {
                card.Note = NoteText.Copy(card.Note);
                ValidateNoteImages(card.Note, assets);
            }
            var memory = card.Memory;
            if (!double.IsFinite(memory.Stability) || memory.Stability < 0 || !double.IsFinite(memory.Difficulty) ||
                memory.Difficulty is < 1 or > 10 || memory.Repetitions < 0 || memory.Lapses < 0 || memory.Lapses > memory.Repetitions)
                throw new InvalidDataException("A card contains an invalid memory state.");
            var blocks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in card.Blocks)
            {
                if (block is null || string.IsNullOrWhiteSpace(block.Id) || !blocks.Add(block.Id) ||
                    block.Kind is not ("text" or "image") || block.Text is null ||
                    !Finite(block.X, block.Y, block.Width, block.Height, block.FontSize, block.Radius) ||
                    block.X < 0 || block.Y < 0 || block.X > PageSize.Width || block.Y > PageSize.Height ||
                    block.Width <= 0 || block.Height <= 0 || block.Width > PageSize.Width + 1 ||
                    block.Height > PageSize.Height + 1 || block.FontSize is < 6 or > 120 || block.Radius < 0)
                    throw new InvalidDataException("A note element has an invalid position or style.");
                if (block.Kind == "image")
                {
                    ValidateAssetName(block.AssetName);
                    var path = Path.Combine(assets, block.AssetName);
                    EnsureNoLinks(path);
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length is <= 0 or > MaxAssetBytes)
                        throw new InvalidDataException($"An image is missing or damaged: {block.AssetName}");
                    using var stream = File.OpenRead(path);
                    var header = new byte[Math.Min(32, (int)info.Length)];
                    stream.ReadExactly(header);
                    DetectImageExtension(header);
                }
            }
            foreach (var review in card.Reviews)
                if (review is null || !Enum.IsDefined(review.Rating) || !Finite(review.ElapsedDays, review.RecallBefore, review.StabilityAfter) ||
                    review.ElapsedDays < 0 || review.RecallBefore is < 0 or > 1 || review.StabilityAfter <= 0)
                    throw new InvalidDataException("A review record is invalid.");
        }
    }

    private static void ValidateNoteImages(NoteContent content, string assets)
    {
        foreach (var image in content.Images) ValidateImageFile(image, assets);
    }

    private static string ValidateImageFile(NoteImage image, string assets)
    {
        NoteImages.ValidateReference(image);
        var path = Path.GetFullPath(Path.Combine(assets, image.AssetName));
        if (!IsWithin(path, Path.GetFullPath(assets))) throw new InvalidDataException("The image path is outside the library.");
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"An imported image is missing: {image.AssetName}");
        if (info.Length <= 0) throw new InvalidDataException($"An imported image is empty: {image.AssetName}");
        if (info.Length > NoteImages.MaxImportBytes)
            throw new InvalidDataException($"An imported image exceeds 20 MB: {image.AssetName}");
        var bytes = File.ReadAllBytes(path);
        var extension = NoteImages.ValidateBytes(bytes, image.PixelWidth, image.PixelHeight);
        var expectedName = Convert.ToHexStringLower(SHA256.HashData(bytes)) + "." + extension;
        if (expectedName != image.AssetName) throw new InvalidDataException("An imported image has changed or has an invalid asset name.");
        return path;
    }

    private static void ExtractVerified(string zipPath, string staging)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        ArchiveLimits.Validate(archive.Entries.Select(entry => new ArchiveEntrySize(entry.FullName, entry.Length)));
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                name.Split('/').Any(s => s is "" or "." or "..") || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                !entries.TryAdd(name, entry)) throw new InvalidDataException("The backup contains unsafe or duplicate file paths.");
            if (name != DataFile && name != ManifestFile && name != DraftFile && name != MemoDraftFile && name != ProfileFile && !(name.StartsWith("assets/", StringComparison.Ordinal) && name.Count(c => c == '/') == 1))
                throw new InvalidDataException("The backup contains an unexpected file.");
        }
        if (!entries.TryGetValue(ManifestFile, out var manifestEntry) || !entries.ContainsKey(DataFile))
            throw new InvalidDataException("This is not a complete Folio backup.");
        BackupManifest? manifest;
        using (var input = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<BackupManifest>(input, Json);
        if (manifest?.Version != 1 || manifest.Files is null || manifest.Files.Count != entries.Count - 1 || !manifest.Files.ContainsKey(DataFile))
            throw new InvalidDataException("The backup contents do not match its manifest.");
        var canonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (!canonicalNames.Add(item.Key) || item.Key == ManifestFile || !entries.TryGetValue(item.Key, out var entry) ||
                entry.FullName != item.Key || item.Value is null || item.Value.Length != entry.Length)
                throw new InvalidDataException("A backup file name or size does not match its manifest.");
            var path = Path.GetFullPath(Path.Combine(staging, item.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(path, staging)) throw new InvalidDataException("A backup path is outside the restore folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var input = entry.Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("A backup file exceeds its declared size.");
                    output.Write(buffer, 0, count);
                }
                if (written != entry.Length) throw new InvalidDataException("A backup file is truncated.");
                output.Flush(true);
            }
            if (!string.Equals(HashFile(path), item.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The backup failed its integrity check. Your existing library has not been changed.");
        }
    }

    private static void AddEntry(ZipArchive archive, BackupManifest manifest, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
        manifest.Files[name] = new BackupFile { Length = bytes.LongLength, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    private static byte[] ValidateBackupPlan(BackupManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        ArchiveLimits.Validate(manifest.Files.Select(file => new ArchiveEntrySize(file.Key, file.Value.Length))
            .Append(new ArchiveEntrySize(ManifestFile, bytes.LongLength)));
        return bytes;
    }

    private static void AddAssetEntry(ZipArchive archive, BackupManifest manifest, string name, PlannedAsset asset)
    {
        EnsureNoLinks(asset.Path);
        using var source = File.OpenRead(asset.Path);
        if (source.Length != asset.Length)
            throw new IOException("A legacy asset changed while the backup was being prepared. Try the backup again.");
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            written += count;
            if (written > asset.Length)
                throw new IOException("A legacy asset grew while the backup was being written. Try the backup again.");
            output.Write(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
        }
        if (written != asset.Length)
            throw new IOException("A legacy asset was truncated while the backup was being written. Try the backup again.");
        manifest.Files[name] = new BackupFile { Length = written, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) };
    }

    private static string DetectImageExtension(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        if (b.Length >= 24 && b[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "png";
        if (b.Length >= 4 && b[0] == 255 && b[1] == 216 && b[2] == 255) return "jpg";
        if (b.Length >= 10 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M') return "bmp";
        if (b.Length >= 8 && (b[..4].SequenceEqual(new byte[] { 73, 73, 42, 0 }) || b[..4].SequenceEqual(new byte[] { 77, 77, 0, 42 }))) return "tif";
        throw new InvalidDataException("This image format is not recognized. Supported formats: PNG, JPEG, GIF, BMP and TIFF.");
    }

    private static void ValidateAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name is "." or ".." ||
            name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || Path.GetFileName(name) != name)
            throw new InvalidDataException("The image file name is invalid.");
    }

    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private IEnumerable<string> PreservedAssetNames()
    {
        if (!Directory.Exists(AssetDirectory)) return [];
        EnsureNoLinks(AssetDirectory);
        var files = Directory.GetFiles(AssetDirectory);
        foreach (var file in files)
        {
            ValidateAssetName(Path.GetFileName(file));
            EnsureNoLinks(file);
            if (new FileInfo(file).Length > MaxAssetBytes)
                throw new InvalidDataException("A preserved asset exceeds the 64 MB backup limit.");
        }
        return files.Select(file => Path.GetFileName(file));
    }
    private static byte[] Serialize(List<CardDocument> cards) => JsonSerializer.SerializeToUtf8Bytes(new LibraryFile { Cards = cards }, Json);
    private static List<CardDocument> Clone(List<CardDocument> cards) => JsonSerializer.Deserialize<List<CardDocument>>(
        JsonSerializer.SerializeToUtf8Bytes(cards, Json), Json)!;
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WritePrimary(byte[] bytes, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(temporary, bytes);
            File.Move(temporary, destination, true);
        }
        finally { TryDeleteFile(temporary); }
    }

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static bool IsWithin(string path, string directory) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsDataError(Exception error) => error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;
    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Library paths cannot contain symbolic links or directory junctions.");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteStaging(string staging, string parent)
    {
        var resolved = Path.GetFullPath(staging);
        if (!IsWithin(resolved, Path.GetFullPath(parent)) || !Path.GetFileName(resolved).Contains(".restore-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class LibraryFile
    {
        [JsonRequired] public int Version { get; set; } = 5;
        [JsonRequired] public List<CardDocument>? Cards { get; set; }
    }
    private sealed class BackupManifest
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public Dictionary<string, BackupFile> Files { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class BackupFile
    {
        [JsonRequired] public long Length { get; set; }
        [JsonRequired] public string Sha256 { get; set; } = "";
    }
    private sealed record PlannedAsset(string Path, long Length);
    private sealed record DraftBackupSnapshot(ComposerDraft? Draft, string? PrimaryHash, string? PreviousHash);
}

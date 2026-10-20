using System.Text.Json;

namespace Folio.Core;

public sealed partial class LibraryStore
{
    public bool IsDraftSaveBlocked(NoteCollection collection)
    {
        lock (_gate) return StateFor(collection).WriteBlocked;
    }

    public string? GetDraftRecoveryNotice(NoteCollection collection)
    {
        lock (_gate) return StateFor(collection).RecoveryNotice;
    }

    private DraftState StateFor(NoteCollection collection)
    {
        if (!Enum.IsDefined(collection)) throw new ArgumentOutOfRangeException(nameof(collection));
        if (!_draftStates.TryGetValue(collection, out var state))
            _draftStates[collection] = state = new DraftState(collection);
        return state;
    }

    private sealed class DraftState(NoteCollection collection)
    {
        public NoteCollection Collection { get; } = collection;
        public string File => Collection == NoteCollection.Notes ? DraftFile : MemoDraftFile;
        public string PreviousFile => Collection == NoteCollection.Notes ? PreviousDraftFile : PreviousMemoDraftFile;
        public bool Inspected { get; set; }
        public bool WriteBlocked { get; set; }
        public string? KnownHash { get; set; }
        public string? KnownPreviousHash { get; set; }
        public string? RecoveryNotice { get; set; }
    }

    /// <summary>Returns an independent unsent draft. Damaged files are preserved, never repaired in place.</summary>
    public ComposerDraft? LoadDraft() => LoadDraft(NoteCollection.Notes);

    public ComposerDraft? LoadDraft(NoteCollection collection)
    {
        lock (_gate)
        {
            RequireLoaded();
            var state = StateFor(collection);
            state.Inspected = true;
            state.WriteBlocked = false;
            state.RecoveryNotice = null;
            var primary = Path.Combine(RootPath, state.File);
            var previous = Path.Combine(RootPath, state.PreviousFile);
            state.KnownHash = null;
            state.KnownPreviousHash = null;
            try
            {
                EnsureNoLinks(primary);
                EnsureNoLinks(previous);
                state.KnownHash = File.Exists(primary) ? HashFile(primary) : null;
                state.KnownPreviousHash = File.Exists(previous) ? HashFile(previous) : null;
                if (state.KnownHash is not null) return Unsent(state, ReadDraft(primary));
                if (state.KnownPreviousHash is null) return null;
                var recovered = ReadDraft(previous);
                state.RecoveryNotice = "The unsent draft file is missing. The previous complete draft is available for recovery.";
                return Unsent(state, recovered);
            }
            catch (Exception error) when (IsDataError(error))
            {
                // A caller may still read and save all regular notes. Draft writes remain blocked
                // until the original damaged files are resolved, so autosave cannot erase evidence.
                state.WriteBlocked = true;
                state.RecoveryNotice = "The unsent draft could not be read. Its files were preserved and draft saving is disabled until they are recovered.";
                try
                {
                    EnsureNoLinks(previous);
                    if (!File.Exists(previous)) return null;
                    var recovered = ReadDraft(previous);
                    state.RecoveryNotice += " The previous complete draft is available in the composer.";
                    return Unsent(state, recovered);
                }
                catch (Exception previousError) when (IsDataError(previousError)) { return null; }
            }
        }
    }

    public void SaveDraft(ComposerDraft draft) => SaveDraft(draft, NoteCollection.Notes);

    public void SaveDraft(ComposerDraft draft, NoteCollection collection)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            RequireLoaded();
            var state = StateFor(collection);
            var copy = CopyDraft(draft);
            ValidateNoteImages(copy.Content, AssetDirectory);
            if (_cards.Any(card => card.Id == copy.CardId))
                throw new InvalidOperationException("This draft has already been sent. Start a new draft before saving composer changes.");
            PrepareDraftWrite(state);
            CommitDraft(state, copy);
        }
    }

    private ComposerDraft? Unsent(DraftState state, ComposerDraft? draft)
    {
        if (draft is null || !_cards.Any(card => card.Id == draft.CardId)) return draft;
        if (!state.WriteBlocked)
        {
            try { CommitDraft(state, null); }
            catch (Exception error) when (IsDataError(error) || error is InvalidOperationException)
            {
                state.WriteBlocked = true;
                state.RecoveryNotice = "An already-sent draft was kept out of the composer, but its files could not be cleared. They were preserved; reload the draft after resolving the file access problem.";
            }
        }
        return null;
    }

    /// <summary>Persists a null tombstone atomically; it does not delete unsent content before the write succeeds.</summary>
    public void ClearDraft() => ClearDraft(NoteCollection.Notes);

    public void ClearDraft(NoteCollection collection)
    {
        lock (_gate)
        {
            RequireLoaded();
            var state = StateFor(collection);
            PrepareDraftWrite(state);
            CommitDraft(state, null);
        }
    }

    private void PrepareDraftWrite(DraftState state)
    {
        if (!state.Inspected) LoadDraft(state.Collection);
        if (state.WriteBlocked)
            throw new InvalidDataException(state.RecoveryNotice ?? "The unsent draft needs recovery before it can be changed.");
        EnsureDraftUnchanged(state);
    }

    private void EnsureDraftUnchanged(DraftState state) => EnsureDraftUnchanged(state, state.KnownHash, state.KnownPreviousHash);

    private void EnsureDraftUnchanged(DraftState state, string? expectedPrimary, string? expectedPrevious)
    {
        var primary = Path.Combine(RootPath, state.File);
        var previous = Path.Combine(RootPath, state.PreviousFile);
        EnsureNoLinks(primary);
        EnsureNoLinks(previous);
        var current = File.Exists(primary) ? HashFile(primary) : null;
        var old = File.Exists(previous) ? HashFile(previous) : null;
        if (current != expectedPrimary || old != expectedPrevious)
            throw new InvalidOperationException("The unsent draft changed outside this window. Reload it before saving.");
    }

    private DraftBackupSnapshot ReadDraftForBackup(DraftState state)
    {
        // Exporting is not a composer reload. Keep its original external-change expectations intact.
        if (state.Inspected) EnsureDraftUnchanged(state);
        var primary = Path.Combine(RootPath, state.File);
        var previous = Path.Combine(RootPath, state.PreviousFile);
        EnsureNoLinks(primary);
        EnsureNoLinks(previous);
        var primaryHash = File.Exists(primary) ? HashFile(primary) : null;
        var previousHash = File.Exists(previous) ? HashFile(previous) : null;
        ComposerDraft? draft = null;
        try
        {
            if (primaryHash is not null) draft = ReadDraft(primary);
            else if (previousHash is not null)
            {
                draft = ReadDraft(previous);
                state.RecoveryNotice = "The unsent draft file is missing. The previous complete draft is available for recovery.";
            }
        }
        catch (Exception error) when (IsDataError(error))
        {
            state.RecoveryNotice = "The unsent draft could not be read. Its files were preserved; the backup will include a valid previous draft when available.";
            try { if (previousHash is not null) draft = ReadDraft(previous); }
            catch (Exception previousError) when (IsDataError(previousError)) { draft = null; }
        }
        if (draft is not null && _cards.Any(card => card.Id == draft.CardId)) draft = null;
        return new DraftBackupSnapshot(draft, primaryHash, previousHash);
    }

    private void CommitDraft(DraftState state, ComposerDraft? draft)
    {
        if (draft is not null) ValidateNoteImages(draft.Content, AssetDirectory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, Json);
        if (bytes.LongLength > MaxDataBytes) throw new InvalidDataException("The unsent draft exceeds 64 MB. Existing files were preserved.");
        var primary = Path.Combine(RootPath, state.File);
        var previous = Path.Combine(RootPath, state.PreviousFile);
        var temporary = primary + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurably(temporary, bytes);
            EnsureDraftUnchanged(state);
            if (File.Exists(primary)) File.Replace(temporary, primary, previous);
            else
            {
                WritePrimary(bytes, previous);
                File.Move(temporary, primary);
            }
            state.KnownHash = HashFile(primary);
            state.KnownPreviousHash = HashFile(previous);
            if (draft is null && state.KnownPreviousHash != state.KnownHash)
            {
                // A completed clear leaves no older non-null recovery copy to resurrect.
                WritePrimary(bytes, previous);
                state.KnownPreviousHash = HashFile(previous);
            }
            state.RecoveryNotice = null;
        }
        finally { TryDeleteFile(temporary); }
    }

    private static ComposerDraft? ReadDraft(string path)
    {
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxDataBytes)
            throw new InvalidDataException("The unsent draft is missing, empty or larger than 64 MB.");
        var draft = JsonSerializer.Deserialize<ComposerDraft>(File.ReadAllBytes(path), Json);
        if (draft is null) return null;
        var copy = CopyDraft(draft);
        ValidateNoteImages(copy.Content, Path.Combine(Path.GetDirectoryName(path)!, "assets"));
        return copy;
    }

    private static ComposerDraft CopyDraft(ComposerDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.CardId) || draft.CardId.Length > 128 || draft.Content is null)
            throw new InvalidDataException("The unsent draft has an invalid identity or missing text.");
        return new ComposerDraft { CardId = draft.CardId, Content = NoteText.Copy(draft.Content) };
    }

}

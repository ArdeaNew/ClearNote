using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Folio.Core;

public sealed partial class LibraryStore
{
    private const string ProfileFile = "profile.json";
    private const int MaxProfileBytes = 65536;
    private bool _profileInspected;
    private bool _profileWriteBlocked;
    private string? _knownProfileHash;

    public string? ProfileRecoveryNotice { get; private set; }
    public bool ProfileSaveBlocked { get { lock (_gate) return _profileWriteBlocked; } }

    private void ResetProfileState()
    {
        _profileInspected = false;
        _profileWriteBlocked = false;
        _knownProfileHash = null;
        ProfileRecoveryNotice = null;
    }

    /// <summary>Returns an independent local author identity; damaged profiles never disable the note library.</summary>
    public UserProfile LoadProfile()
    {
        lock (_gate)
        {
            RequireLoaded();
            _profileInspected = true;
            _profileWriteBlocked = false;
            _knownProfileHash = null;
            ProfileRecoveryNotice = null;
            var path = Path.Combine(RootPath, ProfileFile);
            try
            {
                _knownProfileHash = ReadProfileHash();
                if (_knownProfileHash is null) return new UserProfile();
                var profile = ReadProfile(path, AssetDirectory);
                EnsureProfileUnchanged();
                return profile;
            }
            catch (Exception error) when (IsDataError(error))
            {
                _profileWriteBlocked = true;
                ProfileRecoveryNotice = "The local profile could not be read. Its file was preserved and profile saving is disabled until it is recovered.";
                return new UserProfile();
            }
        }
    }

    /// <summary>Atomically saves an independent, validated identity without changing any note or draft.</summary>
    public void SaveProfile(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            RequireLoaded();
            var copy = CopyProfile(profile, AssetDirectory);
            if (!_profileInspected) LoadProfile();
            if (_profileWriteBlocked) throw new InvalidDataException(ProfileRecoveryNotice ?? "The local profile needs recovery before it can be changed.");
            EnsureProfileUnchanged();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(copy, Json);
            if (bytes.Length > MaxProfileBytes) throw new InvalidDataException("The local profile exceeds its size limit.");
            var primary = Path.Combine(RootPath, ProfileFile);
            var temporary = primary + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurably(temporary, bytes);
                EnsureProfileUnchanged();
                if (File.Exists(primary)) File.Replace(temporary, primary, null);
                else File.Move(temporary, primary);
                _knownProfileHash = HashFile(primary);
                ProfileRecoveryNotice = null;
            }
            finally { TryDeleteFile(temporary); }
        }
    }

    private void EnsureProfileUnchanged() => EnsureProfileUnchanged(_knownProfileHash);

    private void EnsureProfileUnchanged(string? expectedHash)
    {
        if (ReadProfileHash() != expectedHash)
            throw new InvalidOperationException("The local profile changed outside this window. Reload it before saving.");
    }

    private string? ReadProfileHash()
    {
        var path = Path.Combine(RootPath, ProfileFile);
        EnsureNoLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("The profile path is occupied by a folder.");
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length is <= 0 or > MaxProfileBytes)
            throw new InvalidDataException("The local profile is empty or exceeds its size limit.");
        return HashFile(path);
    }

    private static UserProfile ReadProfile(string path, string assets)
    {
        EnsureNoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxProfileBytes)
            throw new InvalidDataException("The local profile is missing, empty or exceeds its size limit.");
        UserProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<UserProfile>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("The local profile is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The local profile is damaged or has an unsupported format.", error); }
        return CopyProfile(profile, assets);
    }

    private static UserProfile CopyProfile(UserProfile profile, string assets)
    {
        string name;
        try { name = profile.DisplayName?.Normalize(NormalizationForm.FormC).Trim() ?? ""; }
        catch (ArgumentException error) { throw new InvalidDataException("The display name contains invalid text.", error); }
        if (name.Length == 0 || new StringInfo(name).LengthInTextElements > 80 || name.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new InvalidDataException("The display name must contain between 1 and 80 characters without line breaks or control characters.");
        NoteImage? avatar = null;
        if (profile.Avatar is not null)
        {
            avatar = new NoteImage
            {
                AssetName = profile.Avatar.AssetName,
                PixelWidth = profile.Avatar.PixelWidth,
                PixelHeight = profile.Avatar.PixelHeight
            };
            ValidateImageFile(avatar, assets);
        }
        return new UserProfile { DisplayName = name, Avatar = avatar };
    }

}

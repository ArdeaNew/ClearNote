using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Folio.App;

/// <summary>Presentation preferences live beside Data, outside note identity and portable library archives.</summary>
internal sealed class WindowWidthPreferences(string logDirectory)
{
    internal const double DefaultWidth = 678;
    internal const double MinimumWidth = 560;
    internal const double MaximumWidth = 4096;
    private readonly string _path = Path.Combine(Path.GetFullPath(logDirectory), "window.json");
    private string? _knownHash;
    private bool _inspected, _blocked;

    internal double Load()
    {
        _inspected = true;
        _blocked = false;
        _knownHash = null;
        try
        {
            EnsureNoLinks(_path);
            if (Directory.Exists(_path)) throw new IOException("The window settings path is occupied by a folder.");
            if (!File.Exists(_path)) return DefaultWidth;
            var bytes = ReadSettings();
            var value = JsonSerializer.Deserialize<Settings>(bytes);
            if (value is null || value.Version != 1 || !Valid(value.Width))
                throw new InvalidDataException("Window settings are not supported or contain an invalid width.");
            _knownHash = Convert.ToHexString(SHA256.HashData(bytes));
            return value.Width;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A damaged optional preference must never disable the note library or be silently overwritten.
            _blocked = true;
            return DefaultWidth;
        }
    }

    internal void Save(double width)
    {
        if (!_inspected || _blocked) throw new IOException("The existing window settings need recovery and have been preserved.");
        if (!Valid(width)) throw new ArgumentOutOfRangeException(nameof(width));
        EnsureNoLinks(_path);
        var currentHash = File.Exists(_path) ? Convert.ToHexString(SHA256.HashData(ReadSettings())) : null;
        if (!string.Equals(currentHash, _knownHash, StringComparison.Ordinal))
            throw new IOException("Window settings changed outside this window. Restart ClearNote to load them.");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        EnsureNoLinks(_path);
        var temporary = Path.Combine(directory, ".window-" + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Settings { Version = 1, Width = width },
            new JsonSerializerOptions { WriteIndented = true });
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            EnsureNoLinks(_path);
            var finalHash = File.Exists(_path) ? Convert.ToHexString(SHA256.HashData(ReadSettings())) : null;
            if (!string.Equals(finalHash, _knownHash, StringComparison.Ordinal))
                throw new IOException("Window settings changed while saving. The newer file has been preserved.");
            // Both operations commit a complete file in the same directory; neither leaves partial JSON.
            if (_knownHash is null) File.Move(temporary, _path);
            else File.Replace(temporary, _path, null);
            _knownHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private byte[] ReadSettings()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 4096) throw new InvalidDataException("Window settings exceed their size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool Valid(double width) => double.IsFinite(width) && width >= MinimumWidth && width <= MaximumWidth;

    private static void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Window settings cannot use linked folders or files.");
    }

    private sealed class Settings
    {
        public int Version { get; set; }
        public double Width { get; set; }
    }
}

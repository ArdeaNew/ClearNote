using System.Security.Cryptography;
using System.Text.Json;

namespace Folio.Core;

public sealed record PortableStoragePaths(string LibraryPath, string BackupPath, string? MigrationNotice);

/// <summary>Keeps portable usage data beneath Log and copies older portable data without removing originals.</summary>
public static class PortableStorage
{
    private const string MarkerFile = "portable-storage.json";
    private static readonly byte[] MarkerBytes = "{\"version\":1}"u8.ToArray();

    public static PortableStoragePaths Prepare(string softwareDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareDirectory);
        var software = Path.TrimEndingDirectorySeparator(Path.GetFullPath(softwareDirectory));
        if (Path.GetPathRoot(software) == software) throw new ArgumentException("Use the software folder below the drive root.", nameof(softwareDirectory));
        var log = Path.Combine(software, "Log");
        var data = Path.Combine(log, "Data");
        var backups = Path.Combine(log, "Backups");
        var marker = Path.Combine(log, MarkerFile);
        EnsureNoLinks(marker);
        EnsureNoLinks(data);
        EnsureNoLinks(backups);
        if (File.Exists(marker))
        {
            if (new FileInfo(marker).Length > 1024) throw new InvalidDataException("The portable storage migration marker is invalid. Existing files were preserved.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(marker));
            if (!document.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1)
                throw new InvalidDataException("The portable storage migration marker is invalid. Existing files were preserved.");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(backups);
            return new PortableStoragePaths(data, backups, null);
        }

        var planned = new Dictionary<string, MigrationFile>(StringComparer.OrdinalIgnoreCase);
        AddTree(Path.Combine(software, "Data"), "Data", planned, splitBackups: true);
        AddTree(Path.Combine(software, "Backups"), "Backups", planned, splitBackups: false);
        var libraryNames = new[] { "library.json", "library.previous.json" };
        Directory.CreateDirectory(log);
        var staging = Path.Combine(log, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in planned.Values)
            {
                var destination = SafePath(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyVerified(file.Source, destination, file.Hash);
            }
            var stagedData = Path.Combine(staging, "Data");
            if (File.Exists(Path.Combine(stagedData, "library.json")) || File.Exists(Path.Combine(stagedData, "library.previous.json")))
            {
                // Recovery, if necessary, touches only the staged copy; the source remains unchanged.
                var verification = new LibraryStore(stagedData);
                verification.Load();
                var recoveredPrimary = Path.Combine(stagedData, "library.json");
                if (!planned.ContainsKey(Path.Combine("Data", "library.json")))
                    planned[Path.Combine("Data", "library.json")] = new MigrationFile(recoveredPrimary, Path.Combine("Data", "library.json"), Hash(recoveredPrimary));
            }
            // Validate conflicts against the complete, verified staged result, including a recovered primary.
            var stagedLibraryHashes = libraryNames.Where(name => File.Exists(Path.Combine(stagedData, name)))
                .Select(name => Hash(Path.Combine(stagedData, name))).ToHashSet(StringComparer.Ordinal);
            if (stagedLibraryHashes.Count != 0)
                foreach (var name in libraryNames)
                    if (!planned.ContainsKey(Path.Combine("Data", name)) && File.Exists(Path.Combine(data, name)) && !stagedLibraryHashes.Contains(Hash(Path.Combine(data, name))))
                        throw new InvalidDataException("Both old Data and Log/Data contain different library save sets. They were preserved without merging.");
            foreach (var file in planned.Values)
            {
                var destination = SafePath(log, file.RelativePath);
                EnsureNoLinks(destination);
                if (Directory.Exists(destination) || File.Exists(destination) && Hash(destination) != Hash(SafePath(staging, file.RelativePath)))
                    throw new InvalidDataException($"Portable migration found conflicting files at {destination}. Both libraries were preserved; choose which library to keep before restarting.");
            }
            var publishedData = false;
            if (Directory.Exists(stagedData) && !Directory.Exists(data))
            {
                EnsureNoLinks(data);
                Directory.Move(stagedData, data); // Publish a complete new library as one directory operation.
                publishedData = true;
            }
            foreach (var file in planned.Values)
            {
                if (publishedData && file.RelativePath.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var source = SafePath(staging, file.RelativePath);
                var destination = SafePath(log, file.RelativePath);
                EnsureNoLinks(destination);
                var stagedHash = Hash(source);
                if (File.Exists(destination))
                {
                    if (Hash(destination) != stagedHash)
                        throw new InvalidDataException("The destination changed during portable migration. All original files were preserved.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination); // No overwrite: concurrent changes cannot be lost.
            }
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(backups);
            var stagedMarker = Path.Combine(staging, MarkerFile);
            using (var output = new FileStream(stagedMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(MarkerBytes); output.Flush(true); }
            EnsureNoLinks(marker);
            File.Move(stagedMarker, marker);
            return new PortableStoragePaths(data, backups, planned.Count == 0 ? null :
                "Portable data was copied into Log/Data and Log/Backups and verified. The original Data and Backups folders were preserved.");
        }
        finally
        {
            var resolved = Path.GetFullPath(staging);
            if (IsWithin(resolved, log) && Path.GetFileName(resolved).StartsWith(".migration-", StringComparison.Ordinal))
            {
                try { if (Directory.Exists(resolved)) Directory.Delete(resolved, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void AddTree(string root, string destinationRoot, Dictionary<string, MigrationFile> planned, bool splitBackups)
    {
        EnsureNoLinks(root);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            EnsureNoLinks(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNoLinks(child);
                if (Directory.Exists(child)) { pending.Push(child); continue; }
                var relative = Path.GetRelativePath(root, child);
                var firstSeparator = relative.IndexOf(Path.DirectorySeparatorChar);
                var nestedBackup = splitBackups && firstSeparator >= 0 && string.Equals(relative[..firstSeparator], "Backups", StringComparison.OrdinalIgnoreCase);
                var target = nestedBackup ? Path.Combine("Backups", relative[(firstSeparator + 1)..]) : Path.Combine(destinationRoot, relative);
                var hash = Hash(child);
                if (planned.TryGetValue(target, out var existing) && existing.Hash != hash)
                    throw new InvalidDataException("The old Data and Backups folders contain conflicting files. The originals were preserved.");
                planned[target] = new MigrationFile(child, target, hash);
            }
        }
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        EnsureNoLinks(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { input.CopyTo(output); output.Flush(true); }
        if (Hash(destination) != expectedHash || Hash(source) != expectedHash)
            throw new IOException("A source file changed during portable migration. The originals were preserved; retry after other writers are closed.");
    }

    private static string SafePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root)) throw new InvalidDataException("A portable data path is outside Log.");
        return path;
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Portable data paths cannot contain symbolic links or directory junctions.");
    }

    private sealed record MigrationFile(string Source, string RelativePath, string Hash);
}

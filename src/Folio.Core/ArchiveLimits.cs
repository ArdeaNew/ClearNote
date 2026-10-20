namespace Folio.Core;

/// <summary>Shared expanded-size limits for backup planning and archive restoration.</summary>
internal static class ArchiveLimits
{
    internal const int MaxEntries = 100002;
    internal const long MaxFileBytes = 64L * 1024 * 1024;
    internal const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaxManifestBytes = 8L * 1024 * 1024;

    internal static void Validate(IEnumerable<ArchiveEntrySize> entries)
    {
        var count = 0;
        long total = 0;
        foreach (var entry in entries)
        {
            if (++count > MaxEntries) throw new InvalidDataException("The backup exceeds the limit of 100,002 files.");
            if (entry.Length < 0 || entry.Length > MaxFileBytes)
                throw new InvalidDataException("A backup file exceeds the 64 MB expanded-size limit.");
            if (entry.Name == "backup-manifest.json" && entry.Length > MaxManifestBytes)
                throw new InvalidDataException("The backup manifest exceeds the 8 MB limit.");
            total += entry.Length;
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("The backup exceeds the 2 GB expanded-size limit.");
        }
    }
}

internal readonly record struct ArchiveEntrySize(string Name, long Length);

using System.IO;
using System.Text;
using System.Text.Json;
using Folio.Core;

namespace Folio.App;

/// <summary>Records failures beside the library; logging never interrupts an editor or writes note content.</summary>
internal static class OperationDiagnostics
{
    private const long MaxDailyBytes = 1024 * 1024;

    public static string? TryWrite(LibraryStore store, string operation, Exception error)
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "Diagnostics");
            var path = Path.Combine(folder, $"errors-{DateTime.Now:yyyy-MM-dd}.jsonl");
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            Directory.CreateDirectory(folder);
            var detail = error.ToString();
            if (detail.Length > 16384) detail = detail[..16384];
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now, operation, type = error.GetType().FullName,
                code = $"0x{error.HResult:X8}", detail
            }) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (output.Length + bytes.Length > MaxDailyBytes) return null;
            output.Seek(0, SeekOrigin.End);
            output.Write(bytes);
            output.Flush(true);
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }
}

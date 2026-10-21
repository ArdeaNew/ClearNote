#Requires -Version 5.1
<#
.SYNOPSIS
Checks the Windows executable and its embedded runtime without starting the UI.
.EXAMPLE
./src/tools/Verify-Portable.ps1 -Path ./app/ClearNote.exe -ExpectedVersion 1.9.5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Path,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')] [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$executable = Get-Item -LiteralPath $Path
if ($executable.PSIsContainer -or $executable.VersionInfo.ProductName -ne 'ClearNote') {
    throw 'Expected a ClearNote executable.'
}
if ($ExpectedVersion -and ([version]$executable.VersionInfo.FileVersion).ToString(3) -ne $ExpectedVersion) {
    throw 'The executable version does not match the requested release.'
}
$looseFiles = @(Get-ChildItem -LiteralPath $executable.DirectoryName -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json'
})
if ($looseFiles.Count) { throw 'The portable folder contains loose runtime dependencies.' }

# Bundle v6 layout and entry ordering follow the .NET runtime's manifest writer:
# https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs
# https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs
# HostFxr and HostPolicy are linked into the native apphost, not manifest entries:
# https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/TargetInfo.cs
# .NET 10's Windows host also links CoreCLR/JIT; inspect its PE exports:
# https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/static/singlefilehost.def
# https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/apphost/static/CMakeLists.txt
if (-not ('ClearNote.Release.BundleCheck' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClearNote.Release
{
    public sealed class BundleResult
    {
        public int Files;
        public int CompressedFiles;
        public string RuntimeConfig;
        public string NativeRuntime;
    }

    public static class BundleCheck
    {
        private sealed class Entry
        {
            public long Offset, Size, Compressed;
            public byte Type;
            public string Name;
        }

        private static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidDataException(message);
        }

        private static string ReadText(BinaryReader reader, int maximum)
        {
            uint count = 0;
            for (int shift = 0; shift <= 28; shift += 7)
            {
                byte next = reader.ReadByte();
                if (shift == 28) Require((next & 0xf8) == 0, "Invalid string length.");
                count |= (uint)(next & 0x7f) << shift;
                if ((next & 0x80) == 0)
                {
                    Require(count > 0 && count <= maximum && count <= reader.BaseStream.Length - reader.BaseStream.Position,
                        "Bundle string exceeds its bounds.");
                    return new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)count));
                }
            }
            throw new InvalidDataException("Invalid bundle string prefix.");
        }

        private static void CheckLocation(long offset, long size, long limit)
        {
            Require(offset > 0 && size >= 0 && offset <= limit && size <= limit - offset,
                "Embedded payload exceeds the executable's bounds.");
        }

        private static int MapRva(byte[] data, int pe, uint rva, long bytes)
        {
            int count = BitConverter.ToUInt16(data, pe + 6);
            int optionalSize = BitConverter.ToUInt16(data, pe + 20);
            Require(count > 0 && count <= 96 && optionalSize >= 120, "Invalid PE section table.");
            long table = (long)pe + 24 + optionalSize;
            Require(table <= data.Length && count * 40L <= data.Length - table, "Truncated PE section table.");
            for (int i = 0; i < count; i++)
            {
                int section = (int)table + i * 40;
                uint address = BitConverter.ToUInt32(data, section + 12);
                uint rawSize = BitConverter.ToUInt32(data, section + 16);
                uint raw = BitConverter.ToUInt32(data, section + 20);
                long delta = (long)rva - address;
                if (delta < 0 || delta >= rawSize) continue;
                long location = raw + delta;
                Require(bytes >= 0 && bytes <= rawSize - delta && location <= data.Length && bytes <= data.Length - location,
                    "PE export data exceeds its section.");
                return (int)location;
            }
            throw new InvalidDataException("PE export address is outside file-backed sections.");
        }

        private static HashSet<string> ReadExports(byte[] data, int pe)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Require(BitConverter.ToUInt16(data, pe + 20) >= 120, "Missing PE data directories.");
            uint exportRva = BitConverter.ToUInt32(data, pe + 24 + 112);
            if (exportRva == 0) return names;
            int directory = MapRva(data, pe, exportRva, 40);
            uint count = BitConverter.ToUInt32(data, directory + 24);
            Require(count <= 65536, "PE export count exceeds verification bounds.");
            if (count == 0) return names;
            int pointers = MapRva(data, pe, BitConverter.ToUInt32(data, directory + 32), count * 4L);
            for (int i = 0; i < count; i++)
            {
                int name = MapRva(data, pe, BitConverter.ToUInt32(data, pointers + i * 4), 1);
                int end = name;
                while (end < data.Length && end - name < 256 && data[end] != 0) end++;
                Require(end < data.Length && end - name < 256, "Unterminated PE export name.");
                names.Add(Encoding.ASCII.GetString(data, name, end - name));
            }
            return names;
        }

        public static BundleResult Inspect(string path)
        {
            byte[] data;
            using (var input = File.OpenRead(path))
            {
                Require(input.Length >= 4096 && input.Length <= 512L * 1024 * 1024,
                    "Executable length is outside the supported verification range.");
                using (var reader = new BinaryReader(input))
                {
                    data = reader.ReadBytes((int)input.Length);
                    Require(data.Length == input.Length, "Executable was truncated while reading.");
                }
            }
            Require(data[0] == 'M' && data[1] == 'Z', "Missing Windows executable header.");
            int pe = BitConverter.ToInt32(data, 0x3c);
            Require(pe >= 0x40 && pe <= data.Length - 144, "Invalid Windows executable header offset.");
            Require(BitConverter.ToUInt32(data, pe) == 0x00004550 &&
                BitConverter.ToUInt16(data, pe + 4) == 0x8664 &&
                BitConverter.ToUInt16(data, pe + 24) == 0x020b &&
                BitConverter.ToUInt16(data, pe + 24 + 68) == 2, "Expected a Windows x64 desktop executable.");

            byte[] marker = { 0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,
                0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,
                0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae };
            int signature = -1;
            for (int i = 8; i <= data.Length - marker.Length; i++)
            {
                if (data[i] != marker[0]) continue;
                int j = 1;
                while (j < marker.Length && data[i + j] == marker[j]) j++;
                if (j == marker.Length) { signature = i; break; }
            }
            Require(signature >= 8, "Missing .NET single-file bundle marker.");
            long header = BitConverter.ToInt64(data, signature - 8);
            Require(header > signature + marker.Length && header <= data.Length - 53,
                "The executable does not contain a valid bundle manifest.");
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            long depsOffset, depsSize, configOffset, configSize;
            int compressed = 0;
            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = header;
                Require(reader.ReadUInt32() == 6 && reader.ReadUInt32() == 0, "Unsupported .NET bundle format.");
                int count = reader.ReadInt32();
                Require(count > 0 && count <= 10000, "Invalid embedded file count.");
                ReadText(reader, 256);
                depsOffset = reader.ReadInt64(); depsSize = reader.ReadInt64();
                configOffset = reader.ReadInt64(); configSize = reader.ReadInt64();
                Require(reader.ReadUInt64() <= 1, "Unsupported bundle flags.");
                for (int i = 0; i < count; i++)
                {
                    var entry = new Entry { Offset = reader.ReadInt64(), Size = reader.ReadInt64(),
                        Compressed = reader.ReadInt64(), Type = reader.ReadByte(), Name = ReadText(reader, 4096) };
                    Require(entry.Type <= 5 && entry.Size >= 0 && entry.Compressed >= 0 && entry.Compressed <= entry.Size,
                        "Invalid embedded file metadata.");
                    Require(entry.Name.IndexOfAny(new char[] { '\\', ':' }) < 0 && !entry.Name.StartsWith("/"),
                        "Invalid embedded file path.");
                    foreach (string part in entry.Name.Split('/'))
                        Require(part.Length > 0 && part != "." && part != "..", "Unsafe embedded file path.");
                    CheckLocation(entry.Offset, entry.Compressed > 0 ? entry.Compressed : entry.Size, header);
                    Require(!entries.ContainsKey(entry.Name), "Duplicate embedded file path.");
                    entries.Add(entry.Name, entry);
                    if (entry.Compressed > 0) compressed++;
                }
            }
            string[] managed = { "ClearNote.dll", "Folio.Core.dll", "System.Private.CoreLib.dll",
                "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll" };
            foreach (string name in managed)
                Require(entries.ContainsKey(name) && entries[name].Type == 1 && entries[name].Size > 0,
                    "Required application or framework assembly is not bundled: " + name);
            var exports = ReadExports(data, pe);
            bool staticRuntime = exports.Contains("DotNetRuntimeInfo") && exports.Contains("CLRJitAttachState") &&
                exports.Contains("g_CLREngineMetrics");
            bool bundledRuntime = entries.ContainsKey("coreclr.dll") && entries["coreclr.dll"].Type == 2 &&
                entries["coreclr.dll"].Size > 0 && entries.ContainsKey("clrjit.dll") &&
                entries["clrjit.dll"].Type == 2 && entries["clrjit.dll"].Size > 0;
            Require(staticRuntime || bundledRuntime, "CoreCLR/JIT is neither linked into the executable nor bundled.");
            foreach (string name in new string[] { "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "PenImc_cor3.dll" })
                Require(entries.ContainsKey(name) && entries[name].Type == 2 && entries[name].Size > 0,
                    "Required native WPF library is not bundled: " + name);
            Require(entries.ContainsKey("ClearNote.runtimeconfig.json") && entries.ContainsKey("ClearNote.deps.json"),
                "Application runtime configuration is not bundled.");
            var config = entries["ClearNote.runtimeconfig.json"];
            var deps = entries["ClearNote.deps.json"];
            Require(config.Type == 4 && config.Compressed == 0 && config.Offset == configOffset && config.Size == configSize &&
                config.Size > 0 && config.Size <= 1024 * 1024, "Invalid bundled runtime configuration.");
            Require(deps.Type == 3 && deps.Compressed == 0 && deps.Offset == depsOffset && deps.Size == depsSize,
                "Invalid bundled dependency configuration.");
            Require(compressed > 0, "Expected a compressed standalone release.");
            return new BundleResult { Files = entries.Count, CompressedFiles = compressed,
                NativeRuntime = staticRuntime ? "Statically linked CoreCLR/JIT" : "Bundled CoreCLR/JIT",
                RuntimeConfig = new UTF8Encoding(false, true).GetString(data, (int)config.Offset, (int)config.Size) };
        }
    }
}
'@
}
$bundle = [ClearNote.Release.BundleCheck]::Inspect($executable.FullName)
$configuration = $bundle.RuntimeConfig | ConvertFrom-Json
$options = $configuration.runtimeOptions
if (-not $options -or $options.PSObject.Properties.Name -contains 'framework' -or
    $options.PSObject.Properties.Name -contains 'frameworks') {
    throw 'The executable still requires an external .NET framework.'
}
$frameworks = @($options.includedFrameworks)
foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $matches = @($frameworks | Where-Object { $_.name -eq $name -and $_.version -match '^10\.0\.[0-9]+$' })
    if ($matches.Count -ne 1) { throw "The embedded runtime configuration is missing $name." }
}
[pscustomobject]@{
    Executable = $executable.FullName
    Version = $executable.VersionInfo.FileVersion
    Architecture = 'Windows x64'
    BundledFiles = $bundle.Files
    CompressedFiles = $bundle.CompressedFiles
    Runtime = 'Included (.NET and Windows Desktop)'
    NativeRuntime = $bundle.NativeRuntime
    SHA256 = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash
}

// Packs the published Money Tweaks for Linux folder into a .tar.gz with Unix file modes set explicitly (the release is built
// on Windows, where archive tools can't mark the program executable).
// Usage: dotnet run --file scripts/pack-linux.cs -- <publish folder> <output .tar.gz> <version>
using System.Formats.Tar;
using System.IO.Compression;

if (args.Length != 3) { Console.Error.WriteLine("usage: pack-linux <publish folder> <output .tar.gz> <version>"); return 2; }
string src = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]), top = $"money-tweaks-{args[2]}";
const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

using var fs = File.Create(output);
using var gz = new GZipStream(fs, CompressionLevel.Optimal);
using var tar = new TarWriter(gz, TarEntryFormat.Pax);
tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = Exec });
int files = 0;
foreach (var path in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
{
    string rel = Path.GetRelativePath(src, path).Replace('\\', '/');
    using var data = File.OpenRead(path);
    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{top}/{rel}")
    {
        Mode = rel is "money-tweaks" or "money-tweaks.sh" ? Exec : Plain, DataStream = data, ModificationTime = File.GetLastWriteTimeUtc(path),
    });
    files++;
}
Console.WriteLine($"Packed {files} files into {output}");
return 0;

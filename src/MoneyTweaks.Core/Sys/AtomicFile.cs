using System.Text;

namespace MoneyTweaks.Core.Sys;

/// <summary>
/// Crash-safe file writes for files that hold rollback data (tweak backups, NVIDIA backups, service backups) and for
/// game settings files: the text goes to a temp file next to the target, is flushed to disk, then replaces the target
/// in one rename. A crash or power cut leaves either the old file or the new one, never a half-written one.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text, Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, encoding ?? new UTF8Encoding(false)))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            // Keep the target's attributes (e.g. a game's read-only settings file is handled by the caller).
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}

using MoneyTweaks.Core.Cleaner;

namespace MoneyTweaks.Tests;

/// <summary>Read-only checks against this PC's real Windows data (nothing is changed).</summary>
public class CleanerWindowsTests
{
    [Fact]
    public void Recycle_bin_size_is_read_from_windows()
    {
        // Regression: a 4-byte packed SHQUERYRBINFO is rejected on 64-bit Windows, which hid the Recycle Bin size.
        var rb = StorageCleaner.RecycleBin();
        Assert.NotNull(rb);
        Assert.True(rb!.Value.bytes >= 0 && rb.Value.items >= 0);
    }

    [Fact]
    public void Nothing_in_the_real_temp_folder_resolves_into_a_protected_location()
    {
        int checkedFiles = 0;
        foreach (var f in new DirectoryInfo(Path.GetTempPath()).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(5000))
        {
            var full = StorageCleaner.Normalize(f.FullName);
            Assert.True(StorageCleaner.Under(full, StorageCleaner.Normalize(Path.GetTempPath())));
            checkedFiles++;
        }
        Assert.True(checkedFiles >= 0);
    }
}

using Microsoft.Win32;
using MoneyTweaks.Core.Features;

namespace MoneyTweaks.Tests;

/// <summary>Uses a scratch key instead of the real Run key.</summary>
[Collection("registry")]
public class AutoStartTests : IDisposable
{
    private const string Scratch = @"Software\MoneyTweaksTests\Run";
    public AutoStartTests() => AutoStart.KeyPath = Scratch;
    public void Dispose()
    {
        AutoStart.KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MoneyTweaksTests", false); } catch { }
    }

    [Fact]
    public void Enable_disable_and_follow_moved_exe()
    {
        Assert.False(AutoStart.IsEnabled);
        AutoStart.EnsurePath(@"C:\New\MoneyTweaks.exe");   // disabled: must not create anything
        Assert.False(AutoStart.IsEnabled);

        AutoStart.Enable(@"C:\Apps\Money Tweaks\MoneyTweaks.exe");
        Assert.Equal("\"C:\\Apps\\Money Tweaks\\MoneyTweaks.exe\" --background", AutoStart.Current());

        AutoStart.EnsurePath(@"D:\Portable\MoneyTweaks.exe"); // moved copy: entry follows it
        Assert.Equal("\"D:\\Portable\\MoneyTweaks.exe\" --background", AutoStart.Current());

        AutoStart.EnsurePath(@"C:\Temp\render\testhost.exe");  // some other host running this code: never takes over
        Assert.Equal("\"D:\\Portable\\MoneyTweaks.exe\" --background", AutoStart.Current());

        AutoStart.Disable();
        Assert.False(AutoStart.IsEnabled);
        AutoStart.Disable();                                  // idempotent
    }
}

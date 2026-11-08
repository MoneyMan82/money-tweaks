using MoneyTweaks.AgentCenter.Core;

namespace MoneyTweaks.AgentCenter.Tests;

public class WorkspaceTests
{
    private static readonly Workspace Ws = new(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks");

    [Fact]
    public void Garbled_hook_paths_resolve_to_the_real_workspace_file()
    {
        // What Claude Code's hook JSON contained on this PC: each non-ASCII character of the root replaced by U+FFFD.
        Assert.Equal(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks\README.md", Ws.Resolve("C:\\Users\\antti\\OneDrive\\Ty\uFFFDp\uFFFDyt\uFFFD\\MoneyTweaks\\README.md"));
        Assert.Equal(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks\src\a.cs", Ws.Resolve("src/a.cs", "C:\\Users\\antti\\OneDrive\\Ty\uFFFDp\uFFFDyt\uFFFD\\MoneyTweaks"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks\..\secret.txt")]
    [InlineData(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaksEvil\a.cs")]
    [InlineData("C:\\Users\\antti\\OneDrive\\Ty\uFFFDp\uFFFDyt\uFFFD\\MoneyTweaks\\sec\uFFFDret")]
    [InlineData("C:\\X\uFFFD\uFFFD")]
    public void Paths_outside_the_workspace_are_refused(string path) => Assert.Null(Ws.Resolve(path));

    [Fact]
    public void Worktree_names_and_checkouts()
    {
        var f = @"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks\.claude\worktrees\agent-a69\src\x.cs";
        Assert.Equal("agent-a69", Ws.WorktreeName(f));
        Assert.Equal("agent-a69", Ws.WorktreeName("C:\\Users\\antti\\OneDrive\\Ty\uFFFDp\uFFFDyt\uFFFD\\MoneyTweaks\\.claude\\worktrees\\agent-a69"));
        Assert.Equal(@"C:\Users\antti\OneDrive\Työpöytä\MoneyTweaks\.claude\worktrees\agent-a69", Ws.CheckoutOf(f));
        Assert.Equal(Ws.Root, Ws.CheckoutOf(Ws.Root + @"\src\a.cs"));
        Assert.Null(Ws.WorktreeName(Ws.Root + @"\src\a.cs"));
    }

    [Theory]
    [InlineData(@"src\MoneyTweaks\bin\Release\x.dll", true)]
    [InlineData(@".git\index", true)]
    [InlineData(@".claude\worktrees\a\obj\x.json", true)]
    [InlineData(@"src\MoneyTweaks\App.xaml.cs", false)]
    [InlineData(@"team\TASKS.md", false)]
    public void File_monitor_ignores_build_output_and_git(string rel, bool ignored) => Assert.Equal(ignored, FileMonitor.IsIgnored(rel));

    [Fact]
    public void Registry_reads_the_real_agent_definitions()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "MoneyTweaks.slnx"))) d = Path.GetDirectoryName(d);
        var reg = AgentRegistry.Load(new Workspace(d!));
        Assert.True(reg.Count >= 20, reg.Count.ToString());
        Assert.Contains(reg, a => a.Name == "developer" && a.Worktree && !a.ReadOnly && a.Group == "Building");
        Assert.Contains(reg, a => a.Name == "security-reviewer" && a.ReadOnly && a.Group == "Verification");
        Assert.Contains(reg, a => a.Name == "chief-coordinator" && a.Group == "Lead");
    }

    [Fact]
    public void Store_deduplicates_and_searches()
    {
        var path = Path.Combine(Path.GetTempPath(), "mt-store-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var s = new EventStore(path);
            var e = new AgentEvent { Ts = 10, Session = "s", Agent = "a", Type = "progress", Summary = "Found 50% faster_path" };
            Assert.True(s.Add(e));
            Assert.False(s.Add(e));
            Assert.Single(s.Search("50%"));
            Assert.Empty(s.Search("60%"));
            Assert.Single(s.Search("faster_path"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }
}

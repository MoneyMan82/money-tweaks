namespace MoneyTweaks.Core.Macros;

/// <summary>Friendly names for Windows virtual-key codes. Macros store names, never raw scripts.</summary>
public static class VirtualKeys
{
    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> ByCode = [];

    static VirtualKeys()
    {
        void Add(string name, int vk) { ByName[name] = vk; ByCode.TryAdd(vk, name); }
        for (char c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (int i = 0; i <= 9; i++) Add(i.ToString(), 0x30 + i);
        for (int i = 1; i <= 24; i++) Add("F" + i, 0x6F + i);
        for (int i = 0; i <= 9; i++) Add("Num" + i, 0x60 + i);
        (string, int)[] named =
        [
            ("LShift", 0xA0), ("RShift", 0xA1), ("LCtrl", 0xA2), ("RCtrl", 0xA3), ("LAlt", 0xA4), ("RAlt", 0xA5),
            ("Shift", 0x10), ("Ctrl", 0x11), ("Alt", 0x12), ("LWin", 0x5B), ("RWin", 0x5C),
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08), ("CapsLock", 0x14),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("PrintScreen", 0x2C), ("Pause", 0x13), ("ScrollLock", 0x91), ("NumLock", 0x90),
            ("NumMultiply", 0x6A), ("NumAdd", 0x6B), ("NumSubtract", 0x6D), ("NumDecimal", 0x6E), ("NumDivide", 0x6F),
            ("Semicolon", 0xBA), ("Plus", 0xBB), ("Comma", 0xBC), ("Minus", 0xBD), ("Period", 0xBE), ("Slash", 0xBF),
            ("Tilde", 0xC0), ("LBracket", 0xDB), ("Backslash", 0xDC), ("RBracket", 0xDD), ("Quote", 0xDE),
        ];
        foreach (var (n, vk) in named) Add(n, vk);
    }

    public static bool TryParse(string? name, out int vk) => ByName.TryGetValue(name?.Trim() ?? "", out vk);
    public static string? Name(int vk) => ByCode.TryGetValue(vk, out var n) ? n : null;
    public static IEnumerable<string> AllNames => ByName.Keys;

    /// <summary>Extended keys need KEYEVENTF_EXTENDEDKEY so games see the right key (arrows, nav block, right modifiers).</summary>
    internal static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E
        or 0xA3 or 0xA5 or 0x5B or 0x5C or 0x6F or 0x90 or 0x2C;
}

namespace QuickPeek;

/// <summary>
/// 虚拟键与组合键解析："Ctrl+Shift+C" / "Alt+Q" / "Win+F12"。
/// AHK 版靠 Send 字符串，这里直接映射到 VK，走 SendInput。
/// </summary>
internal static class Input
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Tab"] = 0x09, ["Backspace"] = 0x08, ["BS"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E,
        ["Insert"] = 0x2D, ["Ins"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PgUp"] = 0x21, ["PageUp"] = 0x21, ["PgDn"] = 0x22, ["PageDown"] = 0x22,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["PrintScreen"] = 0x2C, ["Pause"] = 0x13, ["CapsLock"] = 0x14,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91, ["Apps"] = 0x5D,
        ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
        [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0
    };

    public static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        name = name.Trim();
        if (name.Length == 0) return false;

        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + f); // F1 = 0x70
            return true;
        }

        if (Named.TryGetValue(name, out vk)) return true;
        if (name.Length == 1 && Named.TryGetValue(name, out vk)) return true;
        return false;
    }

    /// <summary>解析 "Ctrl+Alt+L" 形式；返回 (修饰符, VK)。</summary>
    public static bool TryParseCombo(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyName = null;

        foreach (var raw in parts)
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= Native.MOD_CONTROL;
                    break;
                case "alt":
                    mods |= Native.MOD_ALT;
                    break;
                case "shift":
                    mods |= Native.MOD_SHIFT;
                    break;
                case "win":
                case "lwin":
                    mods |= Native.MOD_WIN;
                    break;
                default:
                    keyName = p;
                    break;
            }
        }

        if (keyName == null) return false;
        if (!TryParseKey(keyName, out vk)) return false;

        // 只有修饰符没有主键的组合（例如只按 "Ctrl+"）无效
        if (mods == 0 && keyName.Length > 1 && !Named.ContainsKey(keyName))
            return false;

        return true;
    }

    /// <summary>把 Keys（WinForms）转成 "Ctrl+Alt+L" 文本，用于快捷键录制。</summary>
    public static string FromWinFormsKeys(Keys keyData)
    {
        var mods = new List<string>();
        if ((keyData & Keys.Control) == Keys.Control) mods.Add("Ctrl");
        if ((keyData & Keys.Alt) == Keys.Alt) mods.Add("Alt");
        if ((keyData & Keys.Shift) == Keys.Shift) mods.Add("Shift");

        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.None)
            return "";

        var name = key.ToString();
        if (key is >= Keys.D0 and <= Keys.D9) name = ((char)('0' + (key - Keys.D0))).ToString();
        else if (name.StartsWith("Oem", StringComparison.Ordinal))
        {
            name = key switch
            {
                Keys.OemMinus => "-",
                Keys.Oemplus => "=",
                Keys.OemOpenBrackets => "[",
                Keys.OemCloseBrackets => "]",
                Keys.OemPipe => "\\",
                Keys.OemSemicolon => ";",
                Keys.OemQuotes => "'",
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.OemQuestion => "/",
                Keys.Oemtilde => "`",
                _ => name
            };
        }

        mods.Add(name);
        return string.Join("+", mods);
    }

    // ---------------- 输入模拟 ----------------

    public static void SendCombo(string spec)
    {
        if (!TryParseCombo(spec, out uint mods, out uint vk))
        {
            Log.Write($"⚠️ 无法解析按键组合: '{spec}'");
            return;
        }

        var seq = new List<Native.INPUT>();

        void Add(uint code, bool up)
        {
            seq.Add(new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wVk = (ushort)code,
                        dwFlags = up ? Native.KEYEVENTF_KEYUP : 0
                    }
                }
            });
        }

        if ((mods & Native.MOD_CONTROL) != 0) Add(0x11, false);
        if ((mods & Native.MOD_SHIFT) != 0) Add(0x10, false);
        if ((mods & Native.MOD_ALT) != 0) Add(0x12, false);
        if ((mods & Native.MOD_WIN) != 0) Add(0x5B, false);

        Add(vk, false);
        Add(vk, true);

        if ((mods & Native.MOD_WIN) != 0) Add(0x5B, true);
        if ((mods & Native.MOD_ALT) != 0) Add(0x12, true);
        if ((mods & Native.MOD_SHIFT) != 0) Add(0x10, true);
        if ((mods & Native.MOD_CONTROL) != 0) Add(0x11, true);

        var arr = seq.ToArray();
        uint sent = Native.SendInput((uint)arr.Length, arr,
            System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        if (sent != arr.Length)
            Log.Write($"⚠️ SendInput 只注入了 {sent}/{arr.Length} 个事件 (错误 " +
                      $"{System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// 释放可能"卡住"的修饰键：热键触发时用户往往还按着 Alt/Ctrl，
    /// 这时直接发 Ctrl+C 会变成 Ctrl+Alt+C，目标程序复制不了。
    /// </summary>
    public static void ReleaseModifiers()
    {
        uint[] vks = { 0x11, 0x12, 0x10, 0x5B, 0x5C }; // Ctrl Alt Shift LWin RWin
        var arr = vks.Select(v => new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT { wVk = (ushort)v, dwFlags = Native.KEYEVENTF_KEYUP }
            }
        }).ToArray();
        Native.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>逐字符 Unicode 输入，等价 AHK 的 SendText（中文也能打）。</summary>
    public static void SendText(string text)
    {
        foreach (var ch in text)
        {
            var down = new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wScan = ch,
                        dwFlags = Native.KEYEVENTF_UNICODE
                    }
                }
            };
            var up = down;
            up.U.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;

            var arr = new[] { down, up };
            Native.SendInput(2, arr, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        }
    }
}

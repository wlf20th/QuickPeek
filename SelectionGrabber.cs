using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QuickPeek;

public readonly record struct Selection(string Text, string ClipboardBackup);

/// <summary>
/// 划词抓取：备份剪贴板 → 清空 → 发送复制快捷键 → 等待 → 还原。
/// 和 AHK 版逻辑一致，但按键走 SendInput、等待有明确超时。
/// </summary>
internal static class SelectionGrabber
{
    public const int MaxSel = 8000;
    public const int CopyTimeoutMs = 2000;

    /// <summary>
    /// 终端类进程。在终端里盲发 Ctrl+Shift+C 是危险的：没有选区时，终端把
    /// "Ctrl+Shift+字母" 按 "Ctrl+字母" 编码（Shift 不参与终端输入编码），
    /// 于是变成 0x03 —— shell 收到就是中断信号，正在跑的 llama-server、
    /// 构建、批处理会直接被 Ctrl+C 打断。
    /// </summary>
    private static readonly string[] TerminalHosts =
    {
        "windowsterminal", "wt", "conhost", "openconsole", "cmd", "powershell", "pwsh",
        "mintty", "alacritty", "wezterm", "wezterm-gui", "conemu", "conemu64",
        "tabby", "hyper", "fluentterminal", "terminus", "cmder"
    };

    public static bool IsTerminalApp(string proc)
    {
        if (string.IsNullOrEmpty(proc)) return false;
        string p = proc.ToLowerInvariant();
        if (p.EndsWith(".exe")) p = p[..^4];
        foreach (var t in TerminalHosts) if (p == t) return true;
        return p.Contains("terminal") || p.Contains("conhost") || p.Contains("openconsole");
    }

    public static bool IsTerminalForeground() => IsTerminalApp(ForegroundProcessName());

    /// <summary>
    /// 第三方 SSH / 串口客户端：键盘输入会被原样转发到远端，盲发按键同样有打断命令的风险。
    /// 它们通常"选中即复制"，所以优先走自动复制快路径；真要发键也只发一次、不补发。
    /// 注意不能加进 TerminalHosts —— 那会让鼠标触发被整体跳过，反而没有操作条了。
    /// </summary>
    private static readonly string[] PassthroughHosts =
    {
        "nyaterm", "finalshell", "xshell", "xshell6", "xftp", "securecrt", "securecrt64",
        "mobaxterm", "putty", "puttytel", "winterm", "termius", "electerm", "royalts",
        "juicessh", "solarputty", "bitvise"
    };

    public static bool IsKeyboardPassthrough(string proc)
    {
        if (string.IsNullOrEmpty(proc)) return false;
        string p = proc.ToLowerInvariant();
        if (p.EndsWith(".exe")) p = p[..^4];
        foreach (var t in PassthroughHosts) if (p == t) return true;
        return p.Contains("ssh") || p.Contains("term");
    }

    /// <summary>
    /// 抓取放到独立的 STA 线程执行。
    /// 关键：全局鼠标钩子装在 UI 线程上，若在 UI 线程里同步等剪贴板（最多 2.7s），
    /// 钩子回调会被拖住，整个系统的鼠标输入都会一顿一顿。
    /// </summary>
    public static Task<Selection?> CaptureAsync(AppConfig cfg, bool fromHotkey, uint seqAtMouseDown = 0)
    {
        var tcs = new TaskCompletionSource<Selection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(CaptureCore(cfg, fromHotkey, seqAtMouseDown));
            }
            catch (Exception ex)
            {
                Log.Write($"⚠️ 抓取线程异常: {ex}");
                tcs.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "QuickPeek.Capture"
        };
        th.SetApartmentState(ApartmentState.STA); // 剪贴板需要 STA
        th.Start();
        return tcs.Task;
    }

    public static Selection? CaptureCore(AppConfig cfg, bool fromHotkey, uint seqAtMouseDown = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trail = new List<string>();
        void Phase(string name) => trail.Add($"{name}@{sw.ElapsedMilliseconds}ms");

        try
        {
            return CaptureBody(cfg, fromHotkey, seqAtMouseDown, sw, Phase);
        }
        finally
        {
            // 只有慢到不正常时才打阶段耗时，正常抓取（100~300ms）不刷日志
            if (sw.ElapsedMilliseconds > 1200)
                Log.Write($"抓取慢({sw.ElapsedMilliseconds}ms): " + string.Join(" → ", trail));
        }
    }

    private static Selection? CaptureBody(AppConfig cfg, bool fromHotkey, uint seqAtMouseDown,
        System.Diagnostics.Stopwatch sw, Action<string> Phase)
    {
        // ---- 快路径：应用"选中即复制" ----
        // 很多终端/SSH 客户端（FinalShell 系、PuTTY、MobaXterm…）拖选松手就自己写剪贴板了。
        // 这种情况我们一个键都不该发：既拿得到文本，也没有按键透传出去打断命令的风险。
        // 必须在清空剪贴板之前判断，否则会把人家刚复制的内容清掉。
        if (seqAtMouseDown != 0 && Native.GetClipboardSequenceNumber() != seqAtMouseDown)
        {
            string auto = ClipboardHelper.GetText().Trim(' ', '\t', '\r', '\n');
            Phase("读取自动复制");
            if (auto.Length > 0)
            {
                Log.Write($"抓取: 应用已自动复制({auto.Length} 字)，未发送任何按键");
                return new Selection(Clamp(auto), auto);
            }
        }

        string proc = ForegroundProcessName();
        bool terminal = IsTerminalApp(proc);

        // ---- 终端：先问"有没有选区"，再决定要不要发键 ----
        // 终端里盲发 Ctrl+Shift+C 是有代价的：没有选区时终端会把它按 Ctrl+C 编码成 0x03，
        // 正在跑的命令（llama-server、构建、批处理）当场被打断。反过来，有选区时它只复制。
        // 所以先用 UIA 问一句，效果就和 cmd 自己的 Ctrl+C 一样：
        //   有选中 → 直接拿走文字（一个键都不发，连剪贴板都不碰）
        //   没选中 → 什么都不做
        // 这段刻意放在"清空剪贴板"之前：能直接拿到文字时，用户的剪贴板一动都不该动。
        if (terminal)
        {
            var (state, uiaText) = SelectionProbe.Query(Native.GetForegroundWindow());

            if (state == SelState.Has && uiaText.Length > 0)
            {
                Log.Write($"抓取: 终端({proc})选区由 UIA 直接读取 {uiaText.Length} 字，未发送任何按键");
                return new Selection(Clamp(uiaText), "");
            }

            if (state == SelState.None)
            {
                Log.Write($"抓取: 终端({proc})当前没有选中文字，不做任何动作");
                return null;
            }

            // 探不出来（该终端没有 UIA 文本接口）：鼠标触发一律不冒险，
            // 热键是用户明确要求"复制当前选区"，才按老路子发一次键
            if (!fromHotkey)
            {
                Log.Write($"抓取: 终端({proc})选区状态未知，鼠标触发不发键（会把 Ctrl+Shift+C 打成 Ctrl+C）");
                return null;
            }
            Log.Write($"抓取: 终端({proc})选区状态未知，热键触发，按老路子发一次复制键");
        }

        var backup = ClipboardHelper.GetText();
        Phase("读取备份");
        if (!ClipboardHelper.SetText(""))
            return null;
        Phase("清空剪贴板");

        // 记住"已清空"这个状态对应的序列号，后面用它判断复制有没有落地
        uint seqCleared = Native.GetClipboardSequenceNumber();

        // 关键：热键触发时用户手还没松开。若此刻 Alt 仍按着，
        // 注入的 Ctrl+C 会被目标程序当作 Ctrl+Alt+C，复制直接失效。
        // 所以先等修饰键真正松开，再清一遍残留状态。
        if (!WaitModifiersReleased(450))
            Log.Write("抓取: 等待修饰键松开超时，继续尝试");
        Input.ReleaseModifiers();
        Thread.Sleep(15);
        Phase("等修饰键");

        var keys = MatchCopyKeys(cfg);
        // SSH/串口客户端和终端一样：按键会被转发到远端，补发一次等于再捅一刀
        bool passthrough = terminal || IsKeyboardPassthrough(proc);
        Log.Write($"抓取: 前台进程={proc}, 使用按键={keys}" +
                  (passthrough ? "（按键会转发到远端：只发一次，不补发）" : ""));

        Input.SendCombo(keys);
        Phase("发送复制键");

        bool got;
        string text;

        if (passthrough)
        {
            // 绝不补发：第二次同样可能被透传成 Ctrl+C
            got = ClipboardHelper.WaitForNewText(CopyTimeoutMs, seqCleared, out text);
            Phase("等待剪贴板(只有一次机会)");
        }
        else
        {
            // 部分应用（WinUI 记事本等）第一次会吞掉按键——刚结束鼠标拖选时尤其明显，
            // 复制没落地就补发一次，比单纯延长超时更可靠。
            got = ClipboardHelper.WaitForNewText(700, seqCleared, out text);
            Phase("等待剪贴板(第一次)");
            if (!got)
            {
                Log.Write("抓取: 首次复制未落地，补发一次");
                uint seqBeforeRetry = Native.GetClipboardSequenceNumber();
                Input.SendCombo(keys);
                got = ClipboardHelper.WaitForNewText(CopyTimeoutMs, seqBeforeRetry, out text);
                Phase("等待剪贴板(补发)");
            }
        }

        if (!got)
        {
            // 放弃之前再看一眼：Java 写的终端/SSH 客户端可能刚好在超时之后才渲染完，
            // 这时剪贴板里其实已经有内容了。以前只把长度写进日志就把内容丢了，
            // 表现就是"明明复制到了却不弹操作条"。
            string late = ClipboardHelper.GetText().Trim(' ', '\t', '\r', '\n');
            Log.Write($"抓取: 等待剪贴板超时（前台进程={ForegroundProcessName()}，" +
                      $"剪贴板长度={late.Length}，序列号 {seqCleared}→{Native.GetClipboardSequenceNumber()}，" +
                      $"耗时 {sw.ElapsedMilliseconds}ms）");

            if (late.Length == 0)
            {
                if (backup != "") ClipboardHelper.SetText(backup);
                return null;
            }

            Log.Write($"抓取: 超时后拿到 {late.Length} 字，直接采用");
            text = late;
            got = true;
        }

        if (backup != "") ClipboardHelper.SetText(backup);
        Phase("恢复剪贴板");

        text = text.Trim(' ', '\t', '\r', '\n');
        if (text.Length == 0)
        {
            Log.Write("抓取: 选中的只有空白字符，忽略");
            return null;
        }

        text = Clamp(text);

        Log.Write($"抓取成功: {text.Length} 字, 耗时 {sw.ElapsedMilliseconds}ms");
        return new Selection(text, backup);
    }

    /// <summary>
    /// 超长选区截断：保留头尾各 4000 字，中间用一行标记代替。
    /// 头尾都留是因为日志的"结论"常常在末尾（异常栈、最后的报错）。
    /// </summary>
    private static string Clamp(string text)
    {
        if (text.Length <= MaxSel) return text;
        return text[..4000] + "\n\n……[中间内容已截断]……\n\n" + text[^4000..];
    }

    /// <summary>等 Ctrl/Alt/Shift/Win 真正松手，最多 timeoutMs。</summary>
    private static bool WaitModifiersReleased(int timeoutMs)
    {
        int[] mods = { 0x11, 0x12, 0x10, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool any = false;
            foreach (var vk in mods)
                if (Native.IsDown(vk)) { any = true; break; }

            if (!any) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>按分软件规则挑复制按键：先匹配进程名（正则），再匹配 title: 前缀。</summary>
    public static string MatchCopyKeys(AppConfig cfg)
    {
        var (proc, title) = ForegroundInfo();
        foreach (var rule in cfg.AppCopyKeys)
        {
            if (string.IsNullOrWhiteSpace(rule.Match)) continue;

            if (rule.Match.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = rule.Match[6..].Trim();
                if (pattern.Length == 0) continue;
                try
                {
                    if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase))
                    {
                        Log.Write($"命中规则(标题): {pattern} -> {rule.Keys}");
                        return rule.Keys;
                    }
                }
                catch (ArgumentException ex)
                {
                    Log.Write($"⚠️ 标题规则无效: {pattern} ({ex.Message})");
                }
            }
            else
            {
                try
                {
                    if (Regex.IsMatch(proc, rule.Match, RegexOptions.IgnoreCase))
                    {
                        Log.Write($"命中规则(进程): {rule.Match} -> {rule.Keys}");
                        return rule.Keys;
                    }
                }
                catch (ArgumentException ex)
                {
                    Log.Write($"⚠️ 进程规则无效: {rule.Match} ({ex.Message})");
                }
            }
        }

        return string.IsNullOrWhiteSpace(cfg.CopyKeys) ? "Ctrl+C" : cfg.CopyKeys;
    }

    public static string ForegroundProcessName()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            return Process.GetProcessById((int)pid).ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static (string proc, string title) ForegroundInfo()
    {
        string proc = "", title = "";
        try
        {
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            proc = Process.GetProcessById((int)pid).ProcessName + ".exe";
            title = NativeTitle(hwnd);
        }
        catch
        {
            // 权限不足等，返回空
        }
        return (proc, title);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private static string NativeTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

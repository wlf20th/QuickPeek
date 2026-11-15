using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace QuickPeek;

public readonly record struct Selection(string Text);

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
                return new Selection(Clamp(auto));
            }
        }

        string proc = ForegroundProcessName();
        bool terminal = IsTerminalApp(proc);

        // ---- 先问一句"到底有没有选中文字"，再决定要不要发键 ----
        // 为什么要问：
        //  · 终端里没有选区时 Ctrl+Shift+C 会被编码成 0x03，直接打断前台命令；
        //  · 普通程序里没有选区时 Ctrl+C 也不是"什么都没发生"——VS Code 复制整行、
        //    某些编辑器复制当前段落，于是弹出一条内容无关的操作条；
        //  · 两种情况都要白等 2.7 秒（等两轮剪贴板）才放弃。
        // 这段刻意放在"清空剪贴板"之前：能直接拿到文字时，用户的剪贴板一动都不该动。
        var probe = SelectionProbe.Query(Native.GetForegroundWindow(), terminal);
        bool noSelection = probe.State == SelState.None && probe.Authoritative;
        Log.Write($"抓取: 选区探针 {probe.State}(可信={probe.Authoritative}) 进程={proc}");

        if (noSelection)
        {
            Log.Write(terminal
                ? $"抓取: 终端({proc})当前没有选中文字，不做任何动作"
                : $"抓取: {proc} 当前没有选中内容（UIA 判定），不发键、不弹条");
            return null;
        }

        if (terminal)
        {
            if (probe.State == SelState.Has && probe.Text.Length > 0)
            {
                Log.Write($"抓取: 终端({proc})选区由 UIA 直接读取 {probe.Text.Length} 字，未发送任何按键");
                return new Selection(Clamp(probe.Text));
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

        // ---- 备份剪贴板（全格式），并且**不清空** ----
        // 老做法是"备份文本 → Clear() → 发复制键 → 只把文本写回去"：Clear() 会把图片、
        // 正在复制的文件、富文本一起抹掉，而还原那一步因为备份是空串直接跳过，
        // 于是"复制了一个文件/一张截图，再随手划个词"就永久丢了（README 缺陷 1）。
        // 现在改两点：
        //   1) 快照覆盖全部格式，图片/文件/HTML/RTF 都能原样写回；
        //   2) 不再清空剪贴板，"复制有没有落地"改看序列号 —— 只有应用真的写过剪贴板，
        //      序列号才会变。好处是超时放弃时用户剪贴板一动都没动过，连还原都不需要。
        var backup = ClipboardSnapshot.Capture();
        if (backup == null)
        {
            // 剪贴板被死占（重试多次仍打不开）：既没法备份、也没法可靠判断复制结果，
            // 这时候发键只会白白改掉用户的剪贴板，直接放弃。
            Log.Write("抓取: 剪贴板无法打开（被占用），本次放弃");
            return null;
        }

        try
        {
            if (backup.Skipped.Count > 0)
                Log.Write($"剪贴板快照: {backup.Summary}；跳过 {string.Join(",", backup.Skipped)}");

            return CopyViaKeys(cfg, fromHotkey, proc, terminal, probe, backup, sw, Phase);
        }
        finally
        {
            // 已还原的项句柄归系统了（置 0），剩下的是没机会还原的，在这里释放
            backup.Dispose();
        }
    }

    /// <summary>
    /// 发复制键把选区送进剪贴板，读完文本后把快照还原回去。
    /// 不清空剪贴板，靠序列号变化判断复制是否落地。
    /// </summary>
    private static Selection? CopyViaKeys(AppConfig cfg, bool fromHotkey, string proc, bool terminal,
        ProbeResult probe, ClipboardSnapshot backup, System.Diagnostics.Stopwatch sw, Action<string> Phase)
    {
        // 记住发键前的序列号：只有它变了，才说明应用真的写过剪贴板
        uint seqBefore = Native.GetClipboardSequenceNumber();
        Phase("备份剪贴板");

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
            got = ClipboardHelper.WaitForNewText(CopyTimeoutMs, seqBefore, out text);
            Phase("等待剪贴板(只有一次机会)");
        }
        else
        {
            // 部分应用（WinUI 记事本等）第一次会吞掉按键——刚结束鼠标拖选时尤其明显，
            // 复制没落地就补发一次，比单纯延长超时更可靠。
            // 探针已经确认"确实有选区"时收紧超时：这种情况下复制要么很快落地，
            // 要么就是被应用吞了，没必要一轮等 2 秒。
            int first = probe.State == SelState.Has ? 900 : 700;
            int second = probe.State == SelState.Has ? 900 : CopyTimeoutMs;

            got = ClipboardHelper.WaitForNewText(first, seqBefore, out text);
            Phase("等待剪贴板(第一次)");
            if (!got)
            {
                Log.Write("抓取: 首次复制未落地，补发一次");
                uint seqBeforeRetry = Native.GetClipboardSequenceNumber();
                Input.SendCombo(keys);
                got = ClipboardHelper.WaitForNewText(second, seqBeforeRetry, out text);
                Phase("等待剪贴板(补发)");
            }
        }

        if (!got)
        {
            // 放弃之前再看一眼：Java 写的终端/SSH 客户端可能刚好在超时之后才渲染完。
            // 但这次剪贴板没有被我们清空，所以必须先确认序列号确实变过 —— 否则读到的
            // 是用户本来就有的内容（上一段文本、图片的伴生文本），会被当成选区弹出去。
            uint seqNow = Native.GetClipboardSequenceNumber();
            bool changed = seqNow != seqBefore;
            string late = changed ? ClipboardHelper.GetText().Trim(' ', '\t', '\r', '\n') : "";

            Log.Write($"抓取: 等待剪贴板超时（前台进程={ForegroundProcessName()}，" +
                      $"剪贴板{(changed ? "已变化" : "未变化")}，文本长度={late.Length}，" +
                      $"序列号 {seqBefore}→{seqNow}，耗时 {sw.ElapsedMilliseconds}ms）");

            if (late.Length == 0)
            {
                // 序列号没变 = 应用压根没动剪贴板，用户的东西原样在那，不用还原；
                // 变了却读不出文本 = 应用复制的是非文本（图片等），得把快照写回去。
                if (changed) RestoreClipboard(backup);
                return null;
            }

            Log.Write($"抓取: 超时后拿到 {late.Length} 字，直接采用");
            text = late;
        }

        RestoreClipboard(backup);
        Phase("恢复剪贴板");

        text = text.Trim(' ', '\t', '\r', '\n');
        if (text.Length == 0)
        {
            Log.Write("抓取: 选中的只有空白字符，忽略");
            return null;
        }

        text = Clamp(text);

        Log.Write($"抓取成功: {text.Length} 字, 耗时 {sw.ElapsedMilliseconds}ms");
        return new Selection(text);
    }

    /// <summary>把快照写回剪贴板。还原失败不能静默 —— 那是用户真的丢东西。</summary>
    private static void RestoreClipboard(ClipboardSnapshot backup)
    {
        if (!backup.Restore())
            Log.Write("⚠️ 剪贴板还原失败，原内容（图片/文件/富文本）可能已丢失");
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

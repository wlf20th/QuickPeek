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
    /// 抓取放到独立的 STA 线程执行。
    /// 关键：全局鼠标钩子装在 UI 线程上，若在 UI 线程里同步等剪贴板（最多 2.7s），
    /// 钩子回调会被拖住，整个系统的鼠标输入都会一顿一顿。
    /// </summary>
    public static Task<Selection?> CaptureAsync(AppConfig cfg, bool fromHotkey)
    {
        var tcs = new TaskCompletionSource<Selection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(CaptureCore(cfg, fromHotkey));
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

    public static Selection? CaptureCore(AppConfig cfg, bool fromHotkey)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trail = new List<string>();
        void Phase(string name) => trail.Add($"{name}@{sw.ElapsedMilliseconds}ms");

        try
        {
            return CaptureBody(cfg, fromHotkey, sw, Phase);
        }
        finally
        {
            // 只有慢到不正常时才打阶段耗时，正常抓取（100~300ms）不刷日志
            if (sw.ElapsedMilliseconds > 1200)
                Log.Write($"抓取慢({sw.ElapsedMilliseconds}ms): " + string.Join(" → ", trail));
        }
    }

    private static Selection? CaptureBody(AppConfig cfg, bool fromHotkey,
        System.Diagnostics.Stopwatch sw, Action<string> Phase)
    {
        var backup = ClipboardHelper.GetText();
        Phase("读取备份");
        if (!ClipboardHelper.SetText(""))
            return null;
        Phase("清空剪贴板");

        // 关键：热键触发时用户手还没松开。若此刻 Alt 仍按着，
        // 注入的 Ctrl+C 会被目标程序当作 Ctrl+Alt+C，复制直接失效。
        // 所以先等修饰键真正松开，再清一遍残留状态。
        if (!WaitModifiersReleased(450))
            Log.Write("抓取: 等待修饰键松开超时，继续尝试");
        Input.ReleaseModifiers();
        Thread.Sleep(15);
        Phase("等修饰键");

        string proc = ForegroundProcessName();
        bool terminal = IsTerminalApp(proc);

        // 鼠标触发 + 终端：直接放弃。终端在"没有选区"时收到 Ctrl+Shift+C 会把它
        // 编码成 Ctrl+C，打断前台命令；而鼠标误触发（拖窗口、点一下、拖标题栏）
        // 恰恰就是没有选区的情况，风险远大于收益。终端里划词请用 Alt+Q。
        if (terminal && !fromHotkey)
        {
            Log.Write($"抓取: 前台是终端({proc})，跳过鼠标触发 —— " +
                      "没选区时 Ctrl+Shift+C 会被当成 Ctrl+C 打断正在跑的命令");
            if (backup != "") ClipboardHelper.SetText(backup);
            return null;
        }

        var keys = MatchCopyKeys(cfg);
        Log.Write($"抓取: 前台进程={proc}, 使用按键={keys}" +
                  (terminal ? "（终端：只发一次，不补发）" : ""));

        Input.SendCombo(keys);
        Phase("发送复制键");

        bool got;
        string text;

        if (terminal)
        {
            // 终端里绝不补发：第二次同样会在没选区时透传成 Ctrl+C
            got = ClipboardHelper.WaitForText(CopyTimeoutMs, out text);
            Phase("等待剪贴板(终端)");
        }
        else
        {
            // 部分应用（WinUI 记事本等）第一次会吞掉按键——刚结束鼠标拖选时尤其明显，
            // 复制没落地就补发一次，比单纯延长超时更可靠。
            got = ClipboardHelper.WaitForText(700, out text);
            Phase("等待剪贴板(第一次)");
            if (!got)
            {
                Log.Write("抓取: 首次复制未落地，补发一次");
                Input.SendCombo(keys);
                got = ClipboardHelper.WaitForText(CopyTimeoutMs, out text);
                Phase("等待剪贴板(补发)");
            }
        }

        if (!got)
        {
            Log.Write($"抓取: 等待剪贴板超时（前台进程={ForegroundProcessName()}，" +
                      $"剪贴板长度={ClipboardHelper.GetText().Length}，耗时 {sw.ElapsedMilliseconds}ms）");
            if (backup != "") ClipboardHelper.SetText(backup);
            return null;
        }

        if (backup != "") ClipboardHelper.SetText(backup);
        Phase("恢复剪贴板");

        text = text.Trim(' ', '\t', '\r', '\n');
        if (text.Length == 0)
        {
            Log.Write("抓取: 选中的只有空白字符，忽略");
            return null;
        }

        if (text.Length > MaxSel)
        {
            string head = text[..4000];
            string tail = text[^4000..];
            text = head + "\n\n……[中间内容已截断]……\n\n" + tail;
        }

        Log.Write($"抓取成功: {text.Length} 字, 耗时 {sw.ElapsedMilliseconds}ms");
        return new Selection(text, backup);
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

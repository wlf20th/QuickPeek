using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace QuickPeek;

internal enum SelState
{
    /// <summary>确定没有选区。</summary>
    None,
    /// <summary>确定有选区（Text 里可能带着文字）。</summary>
    Has,
    /// <summary>问不出来 —— 该程序不提供可用的文本接口。</summary>
    Unknown
}

/// <summary>探针结果。Authoritative=true 表示"有/没有"这个结论可信，可以据此决定发不发键。</summary>
internal readonly record struct ProbeResult(SelState State, string Text, bool Authoritative);

/// <summary>
/// 选区探针：目标是在**一个按键都不发**的前提下回答"现在到底有没有选中文字"。
///
/// 为什么需要它：
///  · 终端（WT / conhost）里没有选区时 Ctrl+C 会被编码成 0x03，直接打断前台命令；
///  · 普通程序里没有选区时 Ctrl+C 也不是"什么都没发生" —— VS Code 会复制整个当前行，
///    结果就是拿着一行无关代码弹操作条；
///  · 两种情况都要白等 2.7 秒才放弃。
///
/// 做法：从**当前焦点元素**往上找，挑第一个支持 TextPattern 的控件问 GetSelection()。
/// 只走焦点链路（而不是遍历整窗）是刻意的：开销从"几百 ms 起"降到 60–75ms，
/// 并且能避免把侧栏里上次选中的文件/列表项误当成用户的选区。
/// </summary>
internal static class SelectionProbe
{
    public static ProbeResult Query(IntPtr hwnd, bool terminal)
    {
        if (hwnd == IntPtr.Zero) return new(SelState.Unknown, "", false);

        // 统一甩到常驻的 UIA 线程上问（临时线程里问不到子元素，见 UiaHost 的注释）
        if (!UiaHost.TryInvoke(() => QueryCore(hwnd, terminal), 2500, out ProbeResult result))
            return new(SelState.Unknown, "", false);

        return result;
    }

    private static ProbeResult QueryCore(IntPtr hwnd, bool terminal)
    {
        var sw = Stopwatch.StartNew();
        var result = QueryFocused(hwnd);

        // 终端再兜一层：焦点偶尔落在别处（比如刚点过标题栏/滚动条），
        // 这时退回"扫一遍这个窗口的 TextPattern 元素"，终端窗口元素少，代价可接受
        if (result.State == SelState.Unknown && terminal)
            result = QueryScan(hwnd);

        if (sw.ElapsedMilliseconds > 250)
            Log.Write($"UIA 探针: 状态={result.State} 可信={result.Authoritative} 耗时={sw.ElapsedMilliseconds}ms");
        return result;
    }

    // ---------------- 快路径：焦点元素往上找 ----------------

    private static ProbeResult QueryFocused(IntPtr hwnd)
    {
        AutomationElement? focused;
        try { focused = AutomationElement.FocusedElement; }
        catch (Exception ex)
        {
            Log.Write($"UIA 探针: 取焦点元素失败 {ex}");
            return new(SelState.Unknown, "", false);
        }
        if (focused == null)
        {
            Log.Write("UIA 探针: 没有焦点元素");
            return new(SelState.Unknown, "", false);
        }

        // 焦点可能落在别的东西上（右键菜单、输入法候选框、甚至别的程序），
        // 那就不再代表"这个窗口的选区"，必须当探不出来处理
        if (!BelongsToWindow(focused, hwnd))
        {
            Log.Write($"UIA 探针: 焦点元素不属于目标窗口（{focused.Current.ClassName}）");
            return new(SelState.Unknown, "", false);
        }

        var walker = TreeWalker.ControlViewWalker;
        var el = focused;
        var trail = new List<string>();
        for (int depth = 0; depth < 8 && el != null; depth++)
        {
            var r = Inspect(el);
            if (r.State != SelState.Unknown) return r;
            try
            {
                trail.Add($"{el.Current.ClassName}/{el.Current.ControlType.ProgrammaticName}");
                el = walker.GetParent(el);
            }
            catch { break; }
        }

        Log.Write("UIA 探针: 焦点链上没找到可用文本控件 " + string.Join(" > ", trail));
        return new(SelState.Unknown, "", false);
    }

    /// <summary>
    /// 焦点元素是不是属于这个窗口的进程。
    /// 不能用"祖先里有没有这个 hwnd"来判断：WinUI 应用（记事本、设置）的顶层元素
    /// NativeWindowHandle 常常是 0，一路往上只会走到桌面（#32769）。
    /// 进程号够用 —— 同进程的弹出菜单之类会被 Inspect 里的 ControlType 过滤掉。
    /// </summary>
    private static bool BelongsToWindow(AutomationElement el, IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;
            return el.Current.ProcessId == (int)pid;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>问一个元素"你有选区吗"。不是文本面板类的控件就返回 Unknown。</summary>
    private static ProbeResult Inspect(AutomationElement el)
    {
        try
        {
            var ct = el.Current.ControlType;
            // 只信文本面板的回答：Document（记事本/浏览器正文）、Text（WT 的 TermControl）、Edit
            if (ct != ControlType.Document && ct != ControlType.Text && ct != ControlType.Edit)
                return new(SelState.Unknown, "", false);

            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out object obj))
                return new(SelState.Unknown, "", false);

            var tp = (TextPattern)obj;
            if (tp.SupportedTextSelection == SupportedTextSelection.None)
                return new(SelState.Unknown, "", false);   // 这个控件没有"选区"概念

            var ranges = tp.GetSelection();
            if (ranges.Length == 0) return new(SelState.None, "", true);

            string text = Clean(ranges[0].GetText(-1));
            return text.Length > 0
                ? new(SelState.Has, text, true)
                : new(SelState.None, "", true);            // 有选区但全是空白，等同没选
        }
        catch (Exception ex)
        {
            Log.Write($"UIA 探针: 读 {el.Current.ClassName} 失败 {ex.GetType().Name} {ex.Message}");
            return new(SelState.Unknown, "", false);
        }
    }

    // ---------------- 兜底：扫这个窗口 ----------------

    private static ProbeResult QueryScan(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return new(SelState.Unknown, "", false);

            var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
            var all = root.FindAll(TreeScope.Descendants, cond);

            bool supported = false;
            for (int i = 0; i < all.Count; i++)
            {
                AutomationElement el;
                try { el = all[i]; }
                catch { continue; }

                var r = Inspect(el);
                if (r.State == SelState.Has) return r;
                if (r.Authoritative) supported = true;
            }
            return supported ? new(SelState.None, "", true) : new(SelState.Unknown, "", false);
        }
        catch (Exception ex)
        {
            Log.Write($"UIA 探针(扫描)失败: {ex.GetType().Name} {ex.Message}");
            return new(SelState.Unknown, "", false);
        }
    }

    /// <summary>
    /// 终端会把每行右侧补齐到整屏宽度（多行选区时尤其明显），必须先按行去掉尾部空白；
    /// 行首缩进是日志的一部分，不能动。
    /// </summary>
    private static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd(' ', '\t');
        return string.Join("\n", lines).TrimEnd('\n');
    }
}

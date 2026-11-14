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

/// <summary>
/// 终端选区探针：目标是在**一个按键都不发**的前提下回答"现在到底有没有选中文字"。
///
/// 为什么需要它：终端（WT / conhost）在没有选区时收到 Ctrl+C 会被编码成 0x03，
/// 直接把前台命令打断；而 Ctrl+C 在有选区时又只是复制。所以只要先知道"有没有选区"，
/// 就能完全复刻 cmd 自己的语义：有选中才抓取并弹条，没选中什么都不做。
///
/// 实现走 UI Automation 的 TextPattern：WT 的 TermControl、conhost 的屏幕缓冲区
/// 都实现了它，GetSelection() 能直接拿到选区文字，比发键 + 读剪贴板更准也更安全。
/// </summary>
internal static class SelectionProbe
{
    public static (SelState State, string Text) Query(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return (SelState.Unknown, "");

        var sw = Stopwatch.StartNew();
        var result = QueryUia(hwnd);
        if (sw.ElapsedMilliseconds > 250)
            Log.Write($"UIA 探针: 状态={result.State} 耗时={sw.ElapsedMilliseconds}ms（偏慢，但不影响正确性）");
        return result;
    }

    private static (SelState State, string Text) QueryUia(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return (SelState.Unknown, "");

            var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
            var all = root.FindAll(TreeScope.Descendants, cond);

            bool selectionSupported = false;
            for (int i = 0; i < all.Count; i++)
            {
                AutomationElement el;
                try { el = all[i]; }
                catch { continue; }

                // WT 只有一个 TermControl 提供文本；conhost 是整个缓冲区。
                // 其它元素（标签页标题之类）即使支持 TextPattern 也不会有选区文字。
                if (!el.TryGetCurrentPattern(TextPattern.Pattern, out object obj)) continue;
                var tp = (TextPattern)obj;
                if (tp.SupportedTextSelection == SupportedTextSelection.None) continue;
                selectionSupported = true;

                var ranges = tp.GetSelection();
                if (ranges.Length == 0) continue;

                string text = Clean(ranges[0].GetText(-1));
                if (text.Length > 0) return (SelState.Has, text);
            }

            // 支持"选区"概念但当前为空 → 就是没选中，可以放心不下发任何按键
            return selectionSupported ? (SelState.None, "") : (SelState.Unknown, "");
        }
        catch (Exception ex)
        {
            Log.Write($"UIA 探针失败: {ex.GetType().Name} {ex.Message}");
            return (SelState.Unknown, "");
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

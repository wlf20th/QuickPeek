// 探针：用 UI Automation 读某个窗口里的「选中文字」，全程不发按键、不碰剪贴板。
// 用法：uiaprobe.exe [窗口句柄十六进制]
using System.Text;
using System.Windows.Automation;

namespace uiaprobe;

internal static class Program
{
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    private static IntPtr FindByClass(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (sb.ToString() == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int Main(string[] args)
    {
        // --watch <ms>：轮询选区长度，用来测 WT 的 UIA 选区有多大的更新延迟
        if (args.Length >= 2 && args[0] == "--watch")
        {
            return Watch(args[1], args.Length > 2 ? args[2] : "4000");
        }

        var arg = args.Length > 0 ? args[0] : "";
        IntPtr hwnd = arg.Length > 0
            ? (IntPtr)Convert.ToInt64(arg.Replace("0x", ""), 16)
            : FindByClass("CASCADIA_HOSTING_WINDOW_CLASS");

        Console.WriteLine($"窗口 = 0x{hwnd:X}");
        if (hwnd == IntPtr.Zero) return 1;

        var root = AutomationElement.FromHandle(hwnd);
        Console.WriteLine($"根元素: {root.Current.ControlType.ProgrammaticName} name={root.Current.Name}");

        var textCond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        var docs = root.FindAll(TreeScope.Descendants, textCond);
        Console.WriteLine($"支持 TextPattern 的元素数 = {docs.Count}");

        for (int i = 0; i < docs.Count; i++)
        {
            var el = docs[i];
            Console.WriteLine($"  [{i}] class={el.Current.ClassName} type={el.Current.ControlType.ProgrammaticName} name={el.Current.Name}");
            try
            {
                var tp = (TextPattern)el.GetCurrentPattern(TextPattern.Pattern);
                var all = tp.DocumentRange.GetText(120);
                Console.WriteLine("      可见文本(前120): " + Show(all));
                var sel = tp.GetSelection();
                Console.WriteLine($"      选区段数 = {sel.Length}");
                foreach (var r in sel)
                {
                    var t = r.GetText(-1);
                    Console.WriteLine($"      选区文字({t.Length}字): " + Show(t));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      TextPattern 读取失败: {ex.GetType().Name} {ex.Message}");
            }
        }
        return 0;
    }

    private static int Watch(string hex, string msArg)
    {
        IntPtr hwnd = (IntPtr)Convert.ToInt64(hex.Replace("0x", ""), 16);
        int ms = int.Parse(msArg);
        var root = AutomationElement.FromHandle(hwnd);
        var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        var all = root.FindAll(TreeScope.Descendants, cond);
        AutomationElement? el = null;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Current.ClassName == "TermControl") { el = all[i]; break; }
        el ??= all.Count > 0 ? all[all.Count - 1] : null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -1;
        while (sw.ElapsedMilliseconds < ms)
        {
            string t = "";
            try
            {
                var tp = (TextPattern)el!.GetCurrentPattern(TextPattern.Pattern);
                var sel = tp.GetSelection();
                t = sel.Length > 0 ? sel[0].GetText(-1) : "";
            }
            catch (Exception ex) { t = "<err " + ex.GetType().Name + ">"; }

            if (t.Length != last)
            {
                Console.WriteLine($"{sw.ElapsedMilliseconds,5}ms  len={t.Length,3}  {Show(t)}");
                last = t.Length;
            }
            Thread.Sleep(60);
        }
        return 0;
    }

    private static string Show(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n");
}

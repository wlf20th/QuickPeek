using System.Windows.Forms;

namespace QuickPeek;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 诊断用：单独验证按键注入是否生效（不落托盘、不占单实例）
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (argv.Length >= 2 && argv[0] is "--sendkeys" or "-sendkeys")
        {
            Log.Write($"诊断: 注入按键 '{argv[1]}'");
            Input.SendCombo(argv[1]);
            return;
        }

        // 诊断用：问一次"当前焦点那个窗口有没有选中文字"，结论写日志
        // 用法：QuickPeek.exe --probe [窗口句柄十六进制]
        if (argv.Length >= 1 && argv[0] is "--probe" or "-probe")
        {
            var hwnd = argv.Length >= 2
                ? (IntPtr)Convert.ToInt64(argv[1].Replace("0x", ""), 16)
                : Native.GetForegroundWindow();
            var proc = SelectionGrabber.ForegroundProcessName();
            var r = SelectionProbe.Query(hwnd, SelectionGrabber.IsTerminalApp(proc));
            Log.Write($"诊断探针: 窗口=0x{hwnd:X} 进程={proc} 结论={r.State} 可信={r.Authoritative} 字数={r.Text.Length}" +
                      (r.Text.Length > 0 ? $" 首行={FirstLine(r.Text)}" : ""));
            return;
        }

        // 单实例：已有进程就让它把托盘菜单弹出来，比甩一个“已在运行”的对话框顺手
        using var mutex = new Mutex(true, @"Local\QuickPeek_DotNet_SingleInstance", out bool created);
        if (!created)
        {
            if (ActivateExisting()) return;

            // 事件还没建好（对方刚启动）或信号失败，退回一句话提示
            MessageBox.Show("QuickPeek 已在托盘运行。\r\n\r\n划词：选中文字后按热键或直接拖选\r\n设置：在托盘图标上右键 → 设置…", "QuickPeek",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            Log.Write($"❌ UI 线程未处理异常: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"❌ 后台未处理异常: {e.ExceptionObject}");

        try
        {
            Application.Run(new TrayContext());
        }
        catch (Exception ex)
        {
            Log.Write($"❌ 启动失败: {ex}");
            MessageBox.Show("启动失败：" + ex.Message, "QuickPeek",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>诊断输出用：只取第一行，避免把整段日志刷爆。</summary>
    private static string FirstLine(string s)
    {
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        var line = i < 0 ? s : s[..i];
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    /// <summary>通知已在运行的实例弹托盘菜单。</summary>
    private static bool ActivateExisting()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(@"Local\QuickPeek_Activate", out var h))
                return false;
            using (h) h.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

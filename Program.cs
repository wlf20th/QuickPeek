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

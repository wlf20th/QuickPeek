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

        // 单实例：第二次启动直接把窗口交给已有进程（这里简单忽略）
        using var mutex = new Mutex(true, @"Local\QuickPeek_DotNet_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("QuickPeek 已在运行，请查看托盘图标。", "QuickPeek",
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
}

namespace QuickPeek;

public static class Log
{
    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(ConfigStore.Dir, "qp.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}

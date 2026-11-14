namespace QuickPeek;

/// <summary>
/// 剪贴板读写带重试：别的程序（输入法、浏览器）会短暂占用剪贴板，
/// 单次失败很常见，这里按 AHK 版的 10×30ms 重试。
/// </summary>
internal static class ClipboardHelper
{
    public static string GetText()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    return Clipboard.GetText(TextDataFormat.UnicodeText);
                return "";
            }
            catch
            {
                Thread.Sleep(30);
            }
        }
        return "";
    }

    public static bool SetText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                // 注意：Clipboard.SetText("") 会抛 ArgumentException，置空必须走 Clear()
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return true;
            }
            catch (Exception ex)
            {
                if (i == 9) Log.Write("⚠️ 写剪贴板失败（被占用）: " + ex.Message);
                Thread.Sleep(30);
            }
        }
        return false;
    }

    /// <summary>等待剪贴板出现文本，返回是否等到。</summary>
    public static bool WaitForText(int timeoutMs, out string text)
    {
        text = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    var t = Clipboard.GetText(TextDataFormat.UnicodeText);
                    if (!string.IsNullOrEmpty(t))
                    {
                        text = t;
                        return true;
                    }
                }
            }
            catch
            {
                // 剪贴板被占用，继续等
            }
            Thread.Sleep(25);
        }
        return false;
    }
}

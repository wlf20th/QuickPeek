using System.Reflection;

namespace QuickPeek;

/// <summary>
/// 应用图标：多尺寸 ico 嵌在程序集里，按目标尺寸挑最合适的一帧。
/// 老版本借用系统 imageres.dll 的图标，在托盘里很不显眼。
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "QuickPeek.app.ico";

    private static readonly Dictionary<int, Icon> Cache = new();

    /// <summary>按边长取图标（会自动挑 ico 里最接近的尺寸帧）。</summary>
    public static Icon Get(int size)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(size, out var cached)) return cached;

            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (s != null)
                {
                    var icon = new Icon(s, size, size);
                    Cache[size] = icon;
                    return icon;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"⚠️ 读取内置图标失败: {ex.Message}");
            }

            // 兜底：exe 自身的内嵌图标
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                if (ico != null)
                {
                    Cache[size] = ico;
                    return ico;
                }
            }
            catch
            {
                // 忽略
            }

            return SystemIcons.Application;
        }
    }

    /// <summary>托盘图标尺寸要跟随 DPI（150% 下应取 24px，取 16px 会被拉伸糊掉）。</summary>
    public static Icon Tray => Get(SystemInformation.SmallIconSize.Width);

    /// <summary>窗口标题栏图标。</summary>
    public static Icon Window => Get(32);
}

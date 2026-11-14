using System.Drawing.Drawing2D;

namespace QuickPeek;

/// <summary>深色主题：统一配色，避免在 3 个窗体里各写一份。</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x1E, 0x1E, 0x1E);
    public static readonly Color BgPanel = Color.FromArgb(0x25, 0x25, 0x26);
    public static readonly Color BgRail = Color.FromArgb(0x20, 0x20, 0x23);
    public static readonly Color BgInput = Color.FromArgb(0x2A, 0x2A, 0x2A);
    public static readonly Color BgButton = Color.FromArgb(0x2E, 0x2E, 0x2E);
    public static readonly Color BgButtonHover = Color.FromArgb(0x3A, 0x3A, 0x3E);
    public static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x3A);
    public static readonly Color Text = Color.FromArgb(0xE8, 0xE8, 0xE8);
    public static readonly Color TextDim = Color.FromArgb(0x8A, 0x8A, 0x92);
    public static readonly Color TextFaint = Color.FromArgb(0xAA, 0xAA, 0xAA);
    public static readonly Color Accent = Color.FromArgb(0x5B, 0x9B, 0xFF);
    public static readonly Color Ok = Color.FromArgb(0x3D, 0xDC, 0x97);
    public static readonly Color Err = Color.FromArgb(0xE0, 0x8A, 0x8A);

    public const string FontName = "Microsoft YaHei UI";

    /// <summary>手工布局用：把 96 DPI 下的设计像素换算成当前 DPI 的物理像素。</summary>
    public static int S(int px, float scale) => (int)Math.Round(px * scale);

    public static float ScaleOf(Control c) => c.DeviceDpi / 96f;

    public static Font Ui(float size = 9f, FontStyle style = FontStyle.Regular) =>
        new(FontName, size, style, GraphicsUnit.Point);

    public static Button Button(string text, int w = 0, int h = 0, bool accent = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Height = h > 0 ? h : 30,
            BackColor = accent ? Accent : BgButton,
            ForeColor = accent ? Color.White : Text,
            FlatStyle = FlatStyle.Flat,
            Font = Ui(9f),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = accent ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = accent
            ? Color.FromArgb(0x4A, 0x8A, 0xEE)
            : BgButtonHover;
        b.FlatAppearance.MouseDownBackColor = accent
            ? Color.FromArgb(0x3A, 0x7A, 0xDE)
            : Border;
        if (w > 0) b.Width = w;
        else b.AutoSize = false;
        return b;
    }

    public static TextBox Input(int w = 200, bool multiline = false)
    {
        var t = new TextBox
        {
            Width = w,
            BackColor = BgInput,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Ui(9.5f),
            Multiline = multiline,
            Height = multiline ? 60 : 24
        };
        // 多行输入框的滚动条默认是亮色，单独染色
        t.HandleCreated += (_, _) => DarkNativeTheme(t);
        return t;
    }

    public static Label Label(string text, Color? color = null, float size = 9f)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? Text,
            Font = Ui(size),
            BackColor = Color.Transparent
        };
    }

    /// <summary>圆角区域，用于无边框浮窗。</summary>
    public static void RoundRegion(Control c, int radius)
    {
        if (c.Width <= 0 || c.Height <= 0) return;
        using var path = RoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius);
        var old = c.Region;
        c.Region = new Region(path);
        old?.Dispose();
    }

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || d > Math.Min(r.Width, r.Height))
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>把 Windows 原生控件（滚动条、下拉框等）渲染成暗色。</summary>
    public static void DarkNativeTheme(Control c)
    {
        try
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", IntPtr.Zero);
        }
        catch
        {
            // 忽略
        }
    }

    public static void DarkTitleBar(IntPtr hwnd) => Native.UseDarkTitleBar(hwnd);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, IntPtr pszSubIdList);
}

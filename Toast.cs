using System.Windows.Forms;

namespace QuickPeek;

/// <summary>轻量提示气泡，等价 AHK 的 ToolTip()。</summary>
internal sealed class Toast : Form
{
    private static Toast? _current;
    private readonly System.Windows.Forms.Timer _timer;

    private Toast(string text, bool nearTray, int durationMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.BgPanel;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        _ = Handle;
        float s = Theme.ScaleOf(this);
        int S(int v) => Theme.S(v, s);

        var lbl = new Label
        {
            Text = text,
            AutoSize = false,
            ForeColor = Theme.Text,
            Font = Theme.Ui(9.5f),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };
        Controls.Add(lbl);

        var size = TextRenderer.MeasureText(text, lbl.Font);
        int w = Math.Min(S(560), size.Width + S(28));
        int h = Math.Max(S(34), size.Height + S(16));
        lbl.SetBounds(S(8), S(4), w - S(16), h - S(8));

        int x, y;
        if (nearTray)
        {
            // 启动提示贴着托盘区（右下角），不要盖在鼠标上
            var scr = Screen.PrimaryScreen!;
            var wa = scr.WorkingArea;
            int bottom = wa.Bottom;
            // 任务栏设成自动隐藏时，工作区就是整屏，气泡会贴到屏幕最下沿、
            // 而且任务栏一弹出来就把它盖住。这里往上让开一段。
            if (wa.Bottom >= scr.Bounds.Bottom) bottom -= S(64);
            x = wa.Right - w - S(12);
            y = bottom - h - S(14);
        }
        else
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            x = Cursor.Position.X + S(14);
            y = Cursor.Position.Y + S(22);
            if (x + w > wa.Right) x = wa.Right - w - S(8);
            if (y + h > wa.Bottom) y = Cursor.Position.Y - h - S(12);
        }
        SetBounds(x, y, w, h);
        Theme.RoundRegion(this, S(6));

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(800, durationMs) };
        _timer.Tick += (_, _) => Close();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x08000000;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public static void Show(string text, int durationMs = 1600) => ShowCore(text, false, durationMs);

    /// <summary>贴着右下角托盘区显示，用于启动提示（不要跟着鼠标、别挡住光标）。</summary>
    public static void ShowNearTray(string text, int durationMs = 2600) => ShowCore(text, true, durationMs);

    private static void ShowCore(string text, bool nearTray, int durationMs)
    {
        try
        {
            _current?.Close();
            _current = new Toast(text, nearTray, durationMs);
            _current.Show();
            _current._timer.Start();
        }
        catch
        {
            // 提示失败无所谓
        }
    }
}

using System.Windows.Forms;

namespace QuickPeek;

/// <summary>轻量提示气泡，等价 AHK 的 ToolTip()。</summary>
internal sealed class Toast : Form
{
    private static Toast? _current;
    private readonly System.Windows.Forms.Timer _timer;

    private Toast(string text)
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
        int w = Math.Min(S(420), size.Width + S(28));
        int h = Math.Max(S(34), size.Height + S(16));
        lbl.SetBounds(S(8), S(4), w - S(16), h - S(8));

        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Cursor.Position.X + S(14);
        int y = Cursor.Position.Y + S(22);
        if (x + w > wa.Right) x = wa.Right - w - S(8);
        if (y + h > wa.Bottom) y = Cursor.Position.Y - h - S(12);
        SetBounds(x, y, w, h);
        Theme.RoundRegion(this, S(6));

        _timer = new System.Windows.Forms.Timer { Interval = 1600 };
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

    public static void Show(string text)
    {
        try
        {
            _current?.Close();
            _current = new Toast(text);
            _current.Show();
            _current._timer.Start();
        }
        catch
        {
            // 提示失败无所谓
        }
    }
}

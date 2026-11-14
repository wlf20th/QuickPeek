using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickPeek;

/// <summary>
/// 选中文字后贴着鼠标弹出的操作条。
/// 无边框 + 置顶 + 不抢焦点，按钮由配置里的 actions 动态生成。
/// </summary>
internal sealed class ActionBarForm : Form
{
    private const int BtnW = 82;
    private const int Gap = 6;
    private const int BarH = 66;

    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly Label _preview;
    private readonly List<Control> _buttons = new();
    private float _s = 1f;

    public event Action<ActionConfig>? ActionChosen;

    public ActionBarForm(int autoHideMs)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Font = Theme.Ui(9f);
        AutoScaleMode = AutoScaleMode.None;

        _preview = new Label
        {
            AutoSize = false,
            ForeColor = Theme.TextFaint,
            Font = Theme.Ui(9f),
            BackColor = Color.Transparent,
            UseMnemonic = false,
            AutoEllipsis = true
        };
        Controls.Add(_preview);

        _hideTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1500, autoHideMs) };
        _hideTimer.Tick += OnHideTick;

        MouseDown += StartDrag;
        _preview.MouseDown += StartDrag;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>用户是否手动拖动过操作条（只有拖过才谈得上“记住位置”）。</summary>
    public bool UserDragged { get; private set; }

    private void StartDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        UserDragged = true;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
        KeepAlive();
    }

    private void KeepAlive()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;

        if ((Control.MouseButtons & MouseButtons.Left) != 0)
        {
            KeepAlive();
            return;
        }

        if (Bounds.Contains(Cursor.Position))
        {
            KeepAlive();
            return;
        }

        Hide();
    }

    /// <summary>按选中文本和动作列表重建界面并显示在鼠标旁。</summary>
    public void ShowFor(string text, IReadOnlyList<ActionConfig> actions, Point? fixedPos, int autoHideMs)
    {
        _ = Handle;                       // 先建句柄，拿到真实 DPI
        _s = Theme.ScaleOf(this);
        int S(int v) => Theme.S(v, _s);

        foreach (var b in _buttons) b.Dispose();
        _buttons.Clear();

        int n = Math.Max(1, actions.Count);
        int btnW = S(BtnW), gap = S(Gap), barH = S(BarH);
        int width = S(8) + n * (btnW + gap) + S(30) + S(8);

        _hideTimer.Interval = Math.Max(1500, autoHideMs);

        _preview.Text = text.Replace("\r", " ").Replace("\n", " ");
        _preview.SetBounds(S(8), S(8), width - S(16), S(20));

        int x = S(8);
        foreach (var act in actions)
        {
            var b = Theme.Button(act.Name, btnW, S(30));
            b.Location = new Point(x, S(30));
            b.Click += (_, _) =>
            {
                Hide();
                ActionChosen?.Invoke(act);
            };
            b.MouseEnter += (_, _) => KeepAlive();
            Controls.Add(b);
            _buttons.Add(b);
            x += btnW + gap;
        }

        var close = Theme.Button("✕", S(30), S(30));
        close.Location = new Point(x, S(30));
        close.ForeColor = Theme.TextDim;
        close.Click += (_, _) => Hide();
        Controls.Add(close);
        _buttons.Add(close);

        // 位置：默认贴在鼠标正下方（水平居中于鼠标），下方放不下才翻到上方
        var cur = Cursor.Position;
        var anchor = fixedPos ?? cur;
        var wa = Screen.FromPoint(anchor).WorkingArea;

        int px, py;
        if (fixedPos is Point fp)
        {
            px = fp.X;
            py = fp.Y;
        }
        else
        {
            px = cur.X - width / 2;
            py = cur.Y + S(22);                                     // 让开光标箭头本身的高度
            if (py + barH > wa.Bottom) py = cur.Y - S(12) - barH;   // 下方不够 → 翻到上方
        }

        // 夹紧到工作区内，别让条被屏幕边缘截掉
        if (px + width > wa.Right) px = wa.Right - width - S(4);
        if (px < wa.Left) px = wa.Left + S(4);
        if (py + barH > wa.Bottom) py = wa.Bottom - barH - S(4);
        if (py < wa.Top) py = wa.Top + S(4);

        SetBounds(px, py, width, barH);
        Theme.RoundRegion(this, S(8));
        Log.Write($"操作条定位: 鼠标=({cur.X},{cur.Y}) 窗口=({px},{py}) 尺寸={width}x{barH} 固定={fixedPos != null}");

        if (!Visible) Show();
        BringToFront();
        KeepAlive();
    }

    public Point CurrentPosition => Location;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Border);
        using var path = Theme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 8);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

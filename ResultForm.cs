using System.Windows.Forms;

namespace QuickPeek;

/// <summary>结果窗：先显示“生成中”，拿到结果后原地替换，不重开窗口。</summary>
internal sealed class ResultForm : Form
{
    private readonly RichTextBox _box;
    private readonly Label _status;
    private readonly Button _copyBtn;

    /// <summary>是否把结果按 Markdown 渲染（设置里可关）。</summary>
    public bool RenderMarkdown { get; set; } = true;

    public ResultForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        _ = Handle;
        var s = Theme.ScaleOf(this);
        int S(int v) => Theme.S(v, s);

        Text = "QuickPeek";
        Icon = AppIcon.Window;
        Width = S(680);
        Height = S(560);
        MinimumSize = new Size(S(460), S(320));
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Theme.Bg;
        Font = Theme.Ui(9f);
        KeyPreview = true;

        _status = new Label
        {
            Dock = DockStyle.Top,
            Height = S(28),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(S(12), 0, 0, 0),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Theme.BgPanel,
            Text = "就绪"
        };

        _box = new RichTextBox
        {
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.BgInput,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.Ui(10.5f),
            WordWrap = true,
            DetectUrls = false,
            HideSelection = false,
            ShortcutsEnabled = true
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(46), BackColor = Theme.Bg };
        _copyBtn = Theme.Button("复制结果", S(104), S(32));
        _copyBtn.Location = new Point(S(10), S(7));
        _copyBtn.Click += (_, _) =>
        {
            if (ClipboardHelper.SetText(_box.Text)) Toast.Show("✓ 已复制");
        };

        var closeBtn = Theme.Button("关闭", S(92), S(32));
        closeBtn.Location = new Point(S(124), S(7));
        closeBtn.Click += (_, _) => Hide();

        var tip = Theme.Label("Esc 关闭 · Ctrl+C 复制全部", Theme.TextDim, 8.5f);
        tip.AutoSize = true;
        tip.Location = new Point(S(236), S(15));

        bottom.Controls.AddRange(new Control[] { _copyBtn, closeBtn, tip });

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(10), S(8), S(10), S(4)) };
        host.Controls.Add(_box);

        Controls.Add(host);
        Controls.Add(bottom);
        Controls.Add(_status);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.C && !_box.Focused)
            {
                if (ClipboardHelper.SetText(_box.Text)) Toast.Show("✓ 已复制");
            }
        };

        // 文本框里没选中内容时，Ctrl+C 也复制全文（默认行为是没反应）
        _box.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C && _box.SelectionLength == 0)
            {
                if (ClipboardHelper.SetText(_box.Text)) Toast.Show("✓ 已复制");
                e.SuppressKeyPress = true;
            }
        };

        // 关闭 = 隐藏，保留窗口复用
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        Load += (_, _) =>
        {
            Theme.DarkTitleBar(Handle);
            Theme.DarkNativeTheme(_box);
        };
    }

    public string Text2 => _box.Text;

    public void ShowLoading(string label)
    {
        MarkdownRenderer.RenderPlain(_box, $"⏳ {label}\r\n\r\n请稍候，模型正在生成…");
        _status.Text = "生成中…";
        _copyBtn.Enabled = false;
        _box.Select(0, 0);
        ShowNearCursor();
    }

    public void SetResult(string content, string status)
    {
        string text = Normalize(content);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (RenderMarkdown) MarkdownRenderer.Render(_box, text);
        else MarkdownRenderer.RenderPlain(_box, text);
        sw.Stop();

        Log.Write($"结果渲染: {(RenderMarkdown ? "markdown" : "纯文本")} {text.Length} 字 {sw.ElapsedMilliseconds}ms");

        _status.Text = status;
        _copyBtn.Enabled = true;
        if (!Visible) ShowNearCursor();

        _box.Select(0, 0);
        _box.ScrollToCaret();
    }

    /// <summary>
    /// llama.cpp / vLLM 返回的换行是 \n，控件只认 \r\n，
    /// 不转换的话整段回答会挤成一行。
    /// </summary>
    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private void ShowNearCursor()
    {
        if (!Visible)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = Math.Max(wa.Left + 8, Math.Min(Cursor.Position.X + 16, wa.Right - Width - 8));
            int y = Math.Max(wa.Top + 8, Math.Min(Cursor.Position.Y + 20, wa.Bottom - Height - 8));
            Location = new Point(x, y);
            Show();
        }
        BringToFront();
    }
}

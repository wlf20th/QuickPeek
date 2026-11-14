using System.Diagnostics;
using System.Windows.Forms;

namespace QuickPeek;

/// <summary>深色开关按钮，替代 WinForms 原生 CheckBox（原生在暗色下很难看）。</summary>
internal sealed class ToggleButton : Button
{
    private bool _on;

    public event Action<bool>? Toggled;

    public ToggleButton(bool on, int w = 96, int h = 26)
    {
        Width = w;
        Height = h;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Theme.Border;
        FlatAppearance.BorderSize = 1;
        FlatAppearance.MouseOverBackColor = Theme.BgButtonHover;
        Font = Theme.Ui(9f);
        Cursor = Cursors.Hand;
        TabStop = false;
        IsOn = on;
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            Text = _on ? "已开启" : "已关闭";
            BackColor = _on ? Color.FromArgb(0x27, 0x3A, 0x30) : Theme.BgButton;
            ForeColor = _on ? Theme.Ok : Color.FromArgb(0x6E, 0x6E, 0x76);
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        IsOn = !IsOn;
        base.OnClick(e);
        Toggled?.Invoke(IsOn);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
    }
}

internal sealed class SettingsForm : Form
{
    private const int LabelW = 250;
    private const int CtrlX = 272;

    private readonly AppConfig _work;
    private readonly Action<AppConfig> _onSave;
    private readonly float _s;
    private int S(int v) => Theme.S(v, _s);

    private readonly Panel _body = new();
    private readonly List<Button> _navs = new();
    private readonly List<Panel> _pages = new();

    private TextBox _apiBase = null!, _apiKey = null!, _model = null!;
    private TextBox _timeout = null!, _temperature = null!, _maxTokens = null!;
    private TextBox _hkPeek = null!, _hkClip = null!, _copyKeys = null!;
    private TextBox _autoHide = null!;
    private ToggleButton _mouseToggle = null!, _stripReasoning = null!, _remember = null!, _startup = null!, _renderMd = null!;
    private Label _testStatus = null!;

    private readonly Panel _actionHost = new();
    private readonly Panel _ruleHost = new();
    private readonly List<ActionRow> _actionRows = new();
    private readonly List<RuleRow> _ruleRows = new();
    private TextBox? _recording;
    private int _currentPage;

    private sealed class ActionRow
    {
        public Panel Root = null!;
        public TextBox Name = null!;
        public ComboBox Mode = null!;
        public TextBox Sys = null!;
    }

    private sealed class RuleRow
    {
        public Panel Root = null!;
        public TextBox Match = null!;
        public TextBox Keys = null!;
    }

    public SettingsForm(AppConfig cfg, Action<AppConfig> onSave, bool startupEnabled, Action<bool> onStartupToggle)
    {
        _work = cfg.Clone();
        _onSave = onSave;

        AutoScaleMode = AutoScaleMode.None;
        _ = Handle;                       // 建句柄拿真实 DPI
        _s = Theme.ScaleOf(this);

        Text = "QuickPeek 设置";
        Icon = AppIcon.Window;
        ClientSize = new Size(S(940), S(636));
        MinimumSize = new Size(S(860), S(600));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Ui(9f);
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // ---- 左侧导航 ----
        var rail = new Panel { Dock = DockStyle.Left, Width = S(180), BackColor = Theme.BgRail };
        rail.Controls.Add(new Label
        {
            Text = "QuickPeek",
            AutoSize = false,
            Bounds = new Rectangle(S(18), S(20), S(150), S(26)),
            ForeColor = Color.White,
            Font = Theme.Ui(13f, FontStyle.Bold),
            BackColor = Color.Transparent
        });

        string[] navNames = { "常规", "AI 服务", "动作管理", "分软件", "关于" };
        for (int i = 0; i < navNames.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = navNames[i],
                Bounds = new Rectangle(S(10), S(62 + i * 44), S(160), S(36)),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(S(12), 0, 0, 0),
                BackColor = Theme.BgRail,
                ForeColor = Color.FromArgb(0xA0, 0xA0, 0xA8),
                Font = Theme.Ui(9.5f),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x2A, 0x2A, 0x2E);
            b.Click += (_, _) => SwitchPage(idx);
            rail.Controls.Add(b);
            _navs.Add(b);
        }

        // ---- 底部按钮 ----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(58), BackColor = Theme.Bg };

        var saveBtn = Theme.Button("保存并应用", S(124), S(34), accent: true);
        saveBtn.Click += (_, _) => Save();

        var cancelBtn = Theme.Button("取消", S(92), S(34));
        cancelBtn.Click += (_, _) => Hide();

        var rightBox = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = S(250),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, S(12), S(16), 0),
            BackColor = Theme.Bg
        };
        saveBtn.Margin = new Padding(S(8), 0, 0, 0);
        cancelBtn.Margin = new Padding(S(8), 0, 0, 0);
        rightBox.Controls.Add(saveBtn);   // RightToLeft：先加的在最右
        rightBox.Controls.Add(cancelBtn);

        var logBtn = Theme.Button("打开日志", S(104), S(34));
        logBtn.Location = new Point(S(20), S(12));
        logBtn.Click += (_, _) => OpenWith("notepad.exe", Log.Path);

        bottom.Controls.Add(logBtn);
        bottom.Controls.Add(rightBox);

        _body.Dock = DockStyle.Fill;
        _body.Padding = new Padding(S(24), S(16), S(16), S(8));
        _body.BackColor = Theme.Bg;

        Controls.Add(_body);
        Controls.Add(bottom);
        Controls.Add(rail);

        BuildPageGeneral(startupEnabled, onStartupToggle);
        BuildPageService();
        BuildPageActions();
        BuildPageRules();
        BuildPageAbout();

        SwitchPage(0);

        KeyDown += OnFormKeyDown;
        Load += (_, _) => Theme.DarkTitleBar(Handle);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    // ---------------- 页面骨架 ----------------

    private Panel NewPage(string title)
    {
        var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
        _pages.Add(p);
        _body.Controls.Add(p);

        p.Controls.Add(new Label
        {
            Text = title,
            AutoSize = false,
            Bounds = new Rectangle(0, S(8), S(500), S(32)),
            ForeColor = Color.White,
            Font = Theme.Ui(15f, FontStyle.Bold),
            BackColor = Color.Transparent
        });
        return p;
    }

    private Label Section(Panel p, string text, int y)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(0, S(y), S(640), S(20)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Color.Transparent
        };
        p.Controls.Add(l);
        return l;
    }

    private Label FieldLabel(Panel p, string text, int y)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(0, S(y) + S(3), S(LabelW), S(22)),
            ForeColor = Theme.Text,
            Font = Theme.Ui(9f),
            BackColor = Color.Transparent
        };
        p.Controls.Add(l);
        return l;
    }

    private Label Hint(Panel p, string text, int y)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(S(CtrlX), S(y)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Color.Transparent
        };
        p.Controls.Add(l);
        return l;
    }

    private TextBox Field(Panel p, string value, int y, int w, bool readOnly = false)
    {
        var t = Theme.Input(S(w));
        t.Text = value;
        t.ReadOnly = readOnly;
        t.Height = S(24);
        t.Location = new Point(S(CtrlX), S(y));
        if (readOnly) t.BackColor = Color.FromArgb(0x33, 0x33, 0x36);
        p.Controls.Add(t);
        return t;
    }

    /// <summary>可录制的快捷键输入框 + 录制按钮。</summary>
    private TextBox HotkeyField(Panel p, string value, int y)
    {
        var t = Field(p, value, y, 150, readOnly: true);
        var btn = Theme.Button("录制", S(60), S(26));
        btn.Location = new Point(S(CtrlX + 158), S(y) - S(1));
        btn.Click += (_, _) =>
        {
            if (_recording == t)
            {
                EndRecording();
                return;
            }
            EndRecording();
            _recording = t;
            btn.Text = "按键…";
            t.Text = "请按下组合键";
        };
        p.Controls.Add(btn);
        return t;
    }

    private void EndRecording()
    {
        if (_recording == null) return;
        _recording = null;
        foreach (var c in _pages[_currentPage].Controls.OfType<Button>())
            if (c.Text == "按键…") c.Text = "录制";
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (_recording == null) return;

        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Escape)
        {
            EndRecording();
            return;
        }

        var spec = Input.FromWinFormsKeys(e.KeyData);
        if (spec.Length == 0) return;

        _recording.Text = spec;
        EndRecording();
    }

    // ---------------- 常规 ----------------

    private void BuildPageGeneral(bool startupEnabled, Action<bool> onStartupToggle)
    {
        var p = NewPage("常规");

        Section(p, "划词与触发", 54);

        FieldLabel(p, "鼠标划词弹条（拖选或双击）", 78);
        _mouseToggle = new ToggleButton(_work.MouseTrigger, S(96), S(26)) { Location = new Point(S(CtrlX), S(78)) };
        p.Controls.Add(_mouseToggle);

        FieldLabel(p, "开机自启", 110);
        _startup = new ToggleButton(startupEnabled, S(96), S(26)) { Location = new Point(S(CtrlX), S(110)) };
        _startup.Toggled += on => onStartupToggle(on);
        p.Controls.Add(_startup);

        Section(p, "快捷键", 152);

        FieldLabel(p, "划词操作条", 176);
        _hkPeek = HotkeyField(p, _work.HkPeek, 176);

        FieldLabel(p, "分析剪贴板日志", 210);
        _hkClip = HotkeyField(p, _work.HkClip, 210);

        Section(p, "划词抓取", 252);

        FieldLabel(p, "抓取复制按键", 276);
        _copyKeys = HotkeyField(p, _work.CopyKeys, 276);

        Hint(p, "终端里建议 Ctrl+Shift+C，避免与中断冲突。", 306);

        Section(p, "操作条", 340);

        FieldLabel(p, "自动隐藏（秒）", 364);
        _autoHide = Field(p, _work.AutoHideSec.ToString(), 364, 80);

        FieldLabel(p, "记住拖动后的位置", 400);
        _remember = new ToggleButton(_work.RememberPos, S(96), S(26)) { Location = new Point(S(CtrlX), S(400)) };
        p.Controls.Add(_remember);

        Section(p, "结果窗", 444);

        FieldLabel(p, "渲染 Markdown", 468);
        _renderMd = new ToggleButton(_work.RenderMarkdown, S(96), S(26)) { Location = new Point(S(CtrlX), S(468)) };
        p.Controls.Add(_renderMd);

        Hint(p, "按 Markdown 排版显示；关掉则输出纯文本。", 500);
    }

    // ---------------- AI 服务 ----------------

    private void BuildPageService()
    {
        var p = NewPage("AI 服务");

        Section(p, "服务连接（OpenAI 兼容，本地 llama-server 默认 127.0.0.1:8080）", 54);

        FieldLabel(p, "服务地址", 78);
        _apiBase = Field(p, _work.ApiBase, 78, 400);

        FieldLabel(p, "API Key", 112);
        _apiKey = Field(p, _work.ApiKey, 112, 400);

        FieldLabel(p, "模型名", 146);
        _model = Field(p, _work.Model, 146, 400);

        FieldLabel(p, "生成超时（秒）", 180);
        _timeout = Field(p, _work.TimeoutSec.ToString(), 180, 90);

        FieldLabel(p, "温度 temperature", 214);
        _temperature = Field(p, _work.Temperature.ToString("0.00"), 214, 90);

        FieldLabel(p, "最大生成 max_tokens", 248);
        _maxTokens = Field(p, _work.MaxTokens.ToString(), 248, 90);

        FieldLabel(p, "仅输出最终答案（隐藏思考过程）", 282);
        _stripReasoning = new ToggleButton(_work.StripReasoning, S(130), S(26)) { Location = new Point(S(CtrlX), S(282)) };
        p.Controls.Add(_stripReasoning);

        var testBtn = Theme.Button("测试连接", S(104), S(32));
        testBtn.Location = new Point(0, S(330));
        testBtn.Click += async (_, _) => await TestConnectionAsync();
        p.Controls.Add(testBtn);

        _testStatus = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(S(116), S(336), S(460), S(24)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(9f),
            BackColor = Color.Transparent,
            Text = "点击测试服务是否可达"
        };
        p.Controls.Add(_testStatus);

        p.Controls.Add(new Label
        {
            Text = "提示：服务地址要带上 /v1（实际请求 <地址>/chat/completions）。\n" +
                   "本机直连会绕过系统代理，等价 AHK 版的 curl --noproxy \"*\"。",
            AutoSize = true,
            Location = new Point(0, S(378)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Color.Transparent
        });
    }

    private async Task TestConnectionAsync()
    {
        _testStatus.ForeColor = Theme.TextDim;
        _testStatus.Text = "测试中…";
        var probe = _work.Clone();
        probe.ApiBase = _apiBase.Text.Trim();
        probe.ApiKey = _apiKey.Text;
        probe.Model = _model.Text.Trim();
        probe.TimeoutSec = 30;
        probe.MaxTokens = 16;

        try
        {
            using var client = new LlmClient(probe);
            await client.CompleteAsync("你是连通性测试助手。", "只回复两个字：可用", CancellationToken.None);
            _testStatus.ForeColor = Theme.Ok;
            _testStatus.Text = $"✓ 连接正常（{probe.Model}）";
            Log.Write("测试连接: 成功");
        }
        catch (Exception ex)
        {
            _testStatus.ForeColor = Theme.Err;
            _testStatus.Text = "✗ " + FirstLine(ex.Message);
            Log.Write($"测试连接失败: {ex.Message}");
        }
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i > 0 ? s[..i] : s).Trim();
    }

    // ---------------- 动作管理 ----------------

    private void BuildPageActions()
    {
        var p = NewPage("动作管理");

        p.Controls.Add(new Label
        {
            Text = "每个动作 = 一条 system 提示词 + 一种结果落地方式。",
            AutoSize = true,
            Location = new Point(0, S(46)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Color.Transparent
        });

        var add = Theme.Button("＋ 添加动作", S(112), S(30));
        add.Location = new Point(S(480), S(40));
        add.Click += (_, _) =>
        {
            CollectActions();
            _work.Actions.Add(new ActionConfig { Name = "新动作", Sys = "", Mode = "popup" });
            RebuildActionRows();
        };
        p.Controls.Add(add);

        _actionHost.Location = new Point(0, S(78));
        _actionHost.Size = new Size(S(610), S(400));
        _actionHost.AutoScroll = true;
        _actionHost.BackColor = Theme.Bg;
        p.Controls.Add(_actionHost);

        RebuildActionRows();
    }

    private static readonly (string key, string label)[] ModeOptions =
    {
        ("popup", "弹窗显示"), ("copy", "复制结果"), ("replace", "替换选中文字"), ("type", "模拟按键输入")
    };

    private void RebuildActionRows()
    {
        _actionHost.SuspendLayout();
        foreach (var r in _actionRows) r.Root.Dispose();
        _actionRows.Clear();

        int y = 0;
        foreach (var act in _work.Actions)
        {
            var root = new Panel
            {
                Bounds = new Rectangle(0, S(y), S(590), S(86)),
                BackColor = Theme.Bg
            };

            var name = Theme.Input(S(160));
            name.Text = act.Name;
            name.Height = S(24);
            name.Location = new Point(0, S(2));

            var mode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.BgInput,
                ForeColor = Theme.Text,
                Font = Theme.Ui(9f),
                Bounds = new Rectangle(S(170), S(2), S(150), S(26))
            };
            mode.Items.AddRange(ModeOptions.Select(m => (object)m.label).ToArray());
            int idx = Array.FindIndex(ModeOptions, m => m.key == act.Mode);
            mode.SelectedIndex = idx < 0 ? 0 : idx;

            var sys = Theme.Input(S(500), multiline: true);
            sys.Text = act.Sys;
            sys.Location = new Point(0, S(32));
            sys.Height = S(46);
            sys.ScrollBars = ScrollBars.Vertical;

            var del = Theme.Button("删除", S(64), S(26));
            del.Location = new Point(S(510), S(1));
            del.ForeColor = Theme.Err;
            del.Click += (_, _) =>
            {
                CollectActions();
                _work.Actions.Remove(act);
                RebuildActionRows();
            };

            root.Controls.AddRange(new Control[] { name, mode, sys, del });
            _actionHost.Controls.Add(root);
            _actionRows.Add(new ActionRow { Root = root, Name = name, Mode = mode, Sys = sys });
            y += 94;
        }

        _actionHost.ResumeLayout();
    }

    private void CollectActions()
    {
        for (int i = 0; i < _actionRows.Count && i < _work.Actions.Count; i++)
        {
            var row = _actionRows[i];
            _work.Actions[i].Name = row.Name.Text.Trim();
            _work.Actions[i].Sys = row.Sys.Text;
            int mi = row.Mode.SelectedIndex;
            _work.Actions[i].Mode = mi >= 0 && mi < ModeOptions.Length ? ModeOptions[mi].key : "popup";
        }
    }

    // ---------------- 分软件 ----------------

    private void BuildPageRules()
    {
        var p = NewPage("分软件");

        p.Controls.Add(new Label
        {
            Text = "默认匹配前台进程名（正则，忽略大小写）；以 title: 开头则匹配窗口标题。\n" +
                   "示例：WindowsTerminal|wt\\.exe|cmd\\.exe|pwsh    title:SSH 会话",
            AutoSize = true,
            Location = new Point(0, S(46)),
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            BackColor = Color.Transparent
        });

        var add = Theme.Button("＋ 添加规则", S(112), S(30));
        add.Location = new Point(S(480), S(40));
        add.Click += (_, _) =>
        {
            CollectRules();
            _work.AppCopyKeys.Add(new AppRule { Match = "", Keys = "Ctrl+C" });
            RebuildRuleRows();
        };
        p.Controls.Add(add);

        _ruleHost.Location = new Point(0, S(92));
        _ruleHost.Size = new Size(S(610), S(380));
        _ruleHost.AutoScroll = true;
        _ruleHost.BackColor = Theme.Bg;
        p.Controls.Add(_ruleHost);

        RebuildRuleRows();
    }

    private void RebuildRuleRows()
    {
        _ruleHost.SuspendLayout();
        foreach (var r in _ruleRows) r.Root.Dispose();
        _ruleRows.Clear();

        int y = 0;
        foreach (var rule in _work.AppCopyKeys)
        {
            var root = new Panel
            {
                Bounds = new Rectangle(0, S(y), S(590), S(34)),
                BackColor = Theme.Bg
            };

            var match = Theme.Input(S(260));
            match.Text = rule.Match;
            match.Height = S(24);
            match.Location = new Point(0, S(3));

            var keys = Theme.Input(S(130));
            keys.Text = rule.Keys;
            keys.ReadOnly = true;
            keys.Height = S(24);
            keys.BackColor = Color.FromArgb(0x33, 0x33, 0x36);
            keys.Location = new Point(S(268), S(3));

            var rec = Theme.Button("录制", S(60), S(26));
            rec.Location = new Point(S(406), S(2));
            rec.Click += (_, _) =>
            {
                if (_recording == keys)
                {
                    EndRecording();
                    return;
                }
                EndRecording();
                _recording = keys;
                rec.Text = "按键…";
                keys.Text = "请按下组合键";
            };

            var del = Theme.Button("删除", S(60), S(26));
            del.Location = new Point(S(472), S(2));
            del.ForeColor = Theme.Err;
            del.Click += (_, _) =>
            {
                CollectRules();
                _work.AppCopyKeys.Remove(rule);
                RebuildRuleRows();
            };

            root.Controls.AddRange(new Control[] { match, keys, rec, del });
            _ruleHost.Controls.Add(root);
            _ruleRows.Add(new RuleRow { Root = root, Match = match, Keys = keys });
            y += 40;
        }

        _ruleHost.ResumeLayout();
    }

    private void CollectRules()
    {
        for (int i = 0; i < _ruleRows.Count && i < _work.AppCopyKeys.Count; i++)
        {
            _work.AppCopyKeys[i].Match = _ruleRows[i].Match.Text.Trim();
            _work.AppCopyKeys[i].Keys = _ruleRows[i].Keys.Text.Trim();
        }
    }

    // ---------------- 关于 ----------------

    private void BuildPageAbout()
    {
        var p = NewPage("关于");

        var text =
            "QuickPeek v0.4 · 划词 AI 助手（C# / .NET 8 重构版）\n\n" +
            "用法：选中文字后弹出操作条；Alt+Q 手动划词；Ctrl+Alt+L 分析剪贴板。\n" +
            "依赖：.NET 8 桌面运行时 · 直连 OpenAI 兼容接口（默认本机 llama-server）。\n\n" +
            $"配置文件：{ConfigStore.FilePath}\n" +
            $"运行日志：{Log.Path}\n\n" +
            "与 AHK 版相比：请求走原生 HttpClient（无 curl 子进程、无临时文件、无轮询），\n" +
            "JSON 用 System.Text.Json 解析（不再靠字符串扫描），界面适配高 DPI 缩放。";

        p.Controls.Add(new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(0, S(56), S(680), S(260)),
            ForeColor = Color.FromArgb(0xC8, 0xC8, 0xCC),
            Font = Theme.Ui(9.5f),
            BackColor = Color.Transparent
        });

        var openCfg = Theme.Button("打开配置目录", S(124), S(32));
        openCfg.Location = new Point(0, S(320));
        openCfg.Click += (_, _) => OpenWith("explorer.exe", ConfigStore.Dir);
        p.Controls.Add(openCfg);

        var openLog = Theme.Button("打开日志", S(104), S(32));
        openLog.Location = new Point(S(136), S(320));
        openLog.Click += (_, _) => OpenWith("notepad.exe", Log.Path);
        p.Controls.Add(openLog);
    }

    private static void OpenWith(string exe, string arg)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{arg}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 打开 {arg} 失败: {ex.Message}");
        }
    }

    // ---------------- 导航与保存 ----------------

    private void SwitchPage(int index)
    {
        _currentPage = index;
        EndRecording();
        for (int i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == index;
        for (int i = 0; i < _navs.Count; i++)
        {
            _navs[i].BackColor = i == index ? Color.FromArgb(0x2C, 0x2C, 0x33) : Theme.BgRail;
            _navs[i].ForeColor = i == index ? Color.White : Color.FromArgb(0xA0, 0xA0, 0xA8);
        }
    }

    private void Save()
    {
        CollectActions();
        CollectRules();
        EndRecording();

        _work.ApiBase = _apiBase.Text.Trim();
        _work.ApiKey = _apiKey.Text.Trim();
        _work.Model = _model.Text.Trim();
        _work.TimeoutSec = ParseInt(_timeout.Text, _work.TimeoutSec, 10, 3600);
        _work.Temperature = ParseDouble(_temperature.Text, _work.Temperature, 0, 2);
        _work.MaxTokens = ParseInt(_maxTokens.Text, _work.MaxTokens, 64, 200000);
        _work.AutoHideSec = ParseInt(_autoHide.Text, _work.AutoHideSec, 2, 600);

        _work.HkPeek = string.IsNullOrWhiteSpace(_hkPeek.Text) ? "Alt+Q" : _hkPeek.Text.Trim();
        _work.HkClip = string.IsNullOrWhiteSpace(_hkClip.Text) ? "Ctrl+Alt+L" : _hkClip.Text.Trim();
        _work.CopyKeys = string.IsNullOrWhiteSpace(_copyKeys.Text) ? "Ctrl+C" : _copyKeys.Text.Trim();

        _work.MouseTrigger = _mouseToggle.IsOn;
        _work.StripReasoning = _stripReasoning.IsOn;
        _work.RememberPos = _remember.IsOn;
        _work.RenderMarkdown = _renderMd.IsOn;

        _work.Actions = _work.Actions.Where(a => a.Name.Length > 0).ToList();
        _work.AppCopyKeys = _work.AppCopyKeys.Where(r => r.Match.Length > 0).ToList();

        try
        {
            _onSave(_work.Clone());
            Hide();
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 保存配置失败: {ex.Message}");
            MessageBox.Show(this, "保存失败：" + ex.Message, "QuickPeek",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int ParseInt(string s, int fallback, int min, int max) =>
        int.TryParse(s.Trim(), out int v) ? Math.Clamp(v, min, max) : fallback;

    private static double ParseDouble(string s, double fallback, double min, double max) =>
        double.TryParse(s.Trim(), out double v) ? Math.Clamp(v, min, max) : fallback;

    public void SetStartupState(bool on) => _startup.IsOn = on;

    /// <summary>直接切到某一页（0 起），供命令行 --settings N 使用。</summary>
    public void ShowPage(int index)
    {
        SwitchPage(Math.Clamp(index, 0, _pages.Count - 1));
    }

    public void ReloadFrom(AppConfig cfg)
    {
        _work.MouseTrigger = cfg.MouseTrigger;
        _mouseToggle.IsOn = cfg.MouseTrigger;
        _remember.IsOn = cfg.RememberPos;
        _renderMd.IsOn = cfg.RenderMarkdown;
        _stripReasoning.IsOn = cfg.StripReasoning;
    }
}

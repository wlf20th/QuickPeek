using System.Diagnostics;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickPeek;

/// <summary>
/// 托盘常驻主控：热键、鼠标触发、操作条、结果窗、设置窗的编排都在这里。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "QuickPeek";

    private AppConfig _cfg;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly MouseWatcher _mouse = new();
    private readonly LlmClient _llm;
    private readonly System.Windows.Forms.Timer _defer = new() { Interval = 150 };
    private readonly Icon _icon;

    private ActionBarForm? _bar;
    private ResultForm? _result;
    private SettingsForm? _settings;

    private bool _busy;
    private bool _capturing;
    private bool _captureAgain;
    private int _barHiddenTick;
    private string _pendingText = "";
    private Point? _lastBarPos;
    private uint _seqAtMouseDown;
    private bool _exiting;

    /// <summary>只为拿到一个归属 UI 线程的窗口句柄，好把"外部激活"的请求编组回来。</summary>
    private readonly Control _invoker = new();
    private EventWaitHandle? _activate;

    public TrayContext()
    {
        _cfg = ConfigStore.Load();
        _llm = new LlmClient(_cfg);
        _icon = LoadIcon();

        Log.Write("启动: 配置加载完成");

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "QuickPeek · 划词 AI 助手"
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _menu.Show(Cursor.Position);
        };

        RefreshMenu();
        RegisterHotkeys();

        _mouse.DragDetected += () =>
        {
            // 按下时的剪贴板序列号要在这里就存下来：延时器一跑，
            // _mouse 里那个字段可能已经被下一次点击覆盖了
            _seqAtMouseDown = _mouse.ClipSeqAtDown;
            _defer.Stop();
            _defer.Start();
        };
        // 抓取/请求进行中、操作条可见、刚关掉操作条后的一小段时间——都不触发新的抓取
        // 注意：这里**不能**把"操作条可见"作为抑制条件。
        // 点在操作条上本身会被 Suppressed() 里的"同进程窗口"判断挡掉；
        // 一旦按可见状态整体抑制，用户不关操作条再划新词就永远抓不到，
        // 操作条会一直显示旧文本。
        _mouse.AlsoSuppress = () => _busy || _capturing
                                    || unchecked(Environment.TickCount - _barHiddenTick) < 600;
        _defer.Tick += (_, _) =>
        {
            _defer.Stop();
            _ = DoCaptureAndShow(fromHotkey: false);
        };
        _mouse.Start();
        _mouse.Enabled = _cfg.MouseTrigger;

        Log.Write($"启动: 托盘就绪 · 鼠标触发={( _cfg.MouseTrigger ? "开" : "关")}");

        // 启动提示用**自绘气泡**，不用托盘 ShowBalloonTip：
        // Win11 会把 balloon 转成系统通知，标题被截断成"私密"、还显示不了应用图标。
        // 开机自启（注册表里带 --tray）和 --silent 不弹，免得每次开机都打扰。
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!argv.Any(a => a is "--tray" or "--silent"))
        {
            // 延后 500ms：等消息循环真正跑起来，否则气泡画不出来也关不掉
            var hint = new System.Windows.Forms.Timer { Interval = 500 };
            hint.Tick += (_, _) =>
            {
                hint.Stop();
                hint.Dispose();
                Toast.ShowNearTray(
                    $"QuickPeek 已启动 · {_cfg.HkPeek} 划词 · {_cfg.HkClip} 分析剪贴板 · Ctrl+Alt+S 设置",
                    3000);
            };
            hint.Start();
        }

        SetupActivateListener();
        DispatchStartupArgs();
    }

    /// <summary>
    /// 监听"再双击一次 exe"的请求：第二次启动的进程只发一个信号就退出，
    /// 这里收到后把托盘菜单就地弹出来（比弹一句"已在运行"有用得多）。
    /// </summary>
    private void SetupActivateListener()
    {
        try
        {
            _invoker.CreateControl();   // 必须有句柄，BeginInvoke 才能编组到 UI 线程
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QuickPeek_Activate");

            var th = new Thread(() =>
            {
                while (true)
                {
                    bool got;
                    try
                    {
                        // 退出时事件会被 Dispose，这里必须接住，否则后台线程异常会带崩进程
                        got = _activate.WaitOne();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (!got) break;
                    try
                    {
                        _invoker.BeginInvoke(new Action(() =>
                        {
                            if (_exiting) return;
                            RefreshMenu();
                            _menu.Show(Cursor.Position);
                            Log.Write("收到二次启动请求，已弹出托盘菜单");
                        }));
                    }
                    catch
                    {
                        // 窗口已销毁，忽略
                    }
                }
            })
            {
                IsBackground = true,
                Name = "QuickPeek.Activate"
            };
            th.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 激活监听创建失败: {ex.Message}");
        }
    }

    // ---------------- 托盘菜单 ----------------

    private void RefreshMenu()
    {
        _menu.Items.Clear();
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
        _menu.BackColor = Theme.BgPanel;
        _menu.ForeColor = Theme.Text;
        _menu.Font = Theme.Ui(9f);
        _menu.ShowImageMargin = false;

        _menu.Items.Add(MenuItem($"设置…  (Ctrl+Alt+S)", (_, _) => OpenSettings()));
        _menu.Items.Add(MenuItem($"划词测试  ({_cfg.HkPeek})", (_, _) => _ = DoCaptureAndShow(fromHotkey: true)));
        _menu.Items.Add(MenuItem($"分析剪贴板  ({_cfg.HkClip})", (_, _) => AnalyzeClipboard()));
        _menu.Items.Add(new ToolStripSeparator());

        var mouseItem = MenuItem("鼠标划词触发", (_, _) => ToggleMouseTrigger());
        mouseItem.Checked = _cfg.MouseTrigger;
        mouseItem.CheckOnClick = false;
        _menu.Items.Add(mouseItem);

        var startupItem = MenuItem("开机自启", (_, _) => SetStartup(!StartupEnabled()));
        startupItem.Checked = StartupEnabled();
        _menu.Items.Add(startupItem);

        _menu.Items.Add(MenuItem("打开日志", (_, _) => OpenWith("notepad.exe", Log.Path)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(MenuItem("退出", (_, _) => ExitApp()));
    }

    private ToolStripMenuItem MenuItem(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Theme.Text, BackColor = Theme.BgPanel };
        item.Click += onClick;
        return item;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.BgPanel;
        public override Color ImageMarginGradientBegin => Theme.BgPanel;
        public override Color ImageMarginGradientMiddle => Theme.BgPanel;
        public override Color ImageMarginGradientEnd => Theme.BgPanel;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuItemSelected => Theme.BgButtonHover;
        public override Color MenuItemSelectedGradientBegin => Theme.BgButtonHover;
        public override Color MenuItemSelectedGradientEnd => Theme.BgButtonHover;
        public override Color MenuItemPressedGradientBegin => Theme.BgButton;
        public override Color MenuItemPressedGradientEnd => Theme.BgButton;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.Accent;
        public override Color CheckSelectedBackground => Theme.Accent;
        public override Color CheckPressedBackground => Theme.Accent;
    }

    // ---------------- 热键 ----------------

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Pressed += OnHotkeyPressed;

        _hotkeys.Register("peek", _cfg.HkPeek);
        _hotkeys.Register("clip", _cfg.HkClip);
        _hotkeys.Register("settings", "Ctrl+Alt+S");
    }

    private void OnHotkeyPressed(string name)
    {
        switch (name)
        {
            case "peek":
                _ = DoCaptureAndShow(fromHotkey: true);
                break;
            case "clip":
                AnalyzeClipboard();
                break;
            case "settings":
                OpenSettings();
                break;
        }
    }

    // ---------------- 抓取与操作条 ----------------

    /// <summary>抓取总上限。剪贴板被别的程序占住时，单次系统调用能阻塞好几秒。
    /// 不放任它，超时就放弃这次，别把后续划词全堵死。</summary>
    private const int CaptureDeadlineMs = 4000;

    private async Task DoCaptureAndShow(bool fromHotkey)
    {
        if (_capturing)
        {
            // 上一笔还在抓（可能卡在剪贴板上）：记下来，等它结束后补抓一次，
            // 这样用户连着划词时最新的那次不会丢
            if (!fromHotkey) _captureAgain = true;
            return;
        }

        if (_busy)
        {
            if (fromHotkey) Toast.Show("正在处理上一条，稍等…");
            return;
        }

        _capturing = true;
        try
        {
            // 抓取在独立 STA 线程跑，UI 线程保持响应（否则全局鼠标钩子会把整个系统拖卡）
            // 鼠标触发时带上"按下瞬间的剪贴板序列号"，用来识别应用是否已经自动复制了选区；
            // 热键触发没有这个前状态，传 0 跳过该快路径。
            // 终端里不再在这里一刀切地挡掉：SelectionGrabber 会先用 UIA 问"有没有选区"，
            // 有就抓、没有就什么都不做 —— 和 cmd 自己 Ctrl+C 的语义一致。
            var task = SelectionGrabber.CaptureAsync(_cfg, fromHotkey, fromHotkey ? 0u : _seqAtMouseDown);
            var finished = await Task.WhenAny(task, Task.Delay(CaptureDeadlineMs));
            if (finished != task)
            {
                Log.Write($"抓取: 超过 {CaptureDeadlineMs}ms 未返回，放弃本次（剪贴板可能被占用）");
                if (fromHotkey) Toast.Show("抓取超时，剪贴板可能被占用");
                return;
            }

            var sel = await task;
            if (sel == null || sel.Value.Text.Length == 0)
            {
                // 鼠标拖了个空选择：旧操作条上的文本已经过期，直接收掉免得用户点错
                if (!fromHotkey && (_bar?.Visible ?? false)) _bar!.Hide();

                // 鼠标误触发很常见，失败时只记日志，不打扰用户
                if (fromHotkey) Toast.Show("未捕获到选中文本");
                return;
            }

            ShowBar(sel.Value.Text);
            Log.Write($"操作条已显示，动作数={_cfg.Actions.Count}");
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 抓取流程异常: {ex.Message}");
            if (fromHotkey) Toast.Show("抓取失败：" + ex.Message);
        }
        finally
        {
            _capturing = false;

            if (_captureAgain)
            {
                _captureAgain = false;
                _ = DoCaptureAndShow(fromHotkey: false);
            }
        }
    }

    private void AnalyzeClipboard()
    {
        if (_busy)
        {
            Toast.Show("正在处理上一条，稍等…");
            return;
        }

        var text = ClipboardHelper.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast.Show("剪贴板是空的或被占用");
            return;
        }

        var act = _cfg.Actions.FirstOrDefault(a => a.Name == "分析日志") ?? _cfg.Actions.FirstOrDefault();
        if (act == null)
        {
            Toast.Show("没有可用的动作，请先在设置里添加");
            return;
        }

        RunAction(text, act);
    }

    private void ShowBar(string text)
    {
        _pendingText = text;

        if (_bar == null || _bar.IsDisposed)
        {
            _bar = new ActionBarForm(_cfg.AutoHideSec * 1000);
            _bar.ActionChosen += act =>
            {
                if (_cfg.RememberPos) _lastBarPos = _bar!.CurrentPosition;
                RunAction(_pendingText, act);
            };
            _bar.VisibleChanged += (_, _) =>
            {
                if (_bar == null || _bar.Visible) return;
                _barHiddenTick = Environment.TickCount;
                if (_cfg.RememberPos) _lastBarPos = _bar.CurrentPosition;
            };
        }

        // 只有在用户手动拖过操作条之后，才沿用上次位置；否则每次都跟着鼠标走
        Point? fixedPos = (_cfg.RememberPos && _bar.UserDragged) ? _lastBarPos : null;
        _bar.ShowFor(text, _cfg.Actions, fixedPos, _cfg.AutoHideSec * 1000);
        _tray.Visible = true;
    }

    // ---------------- 请求 ----------------

    private async void RunAction(string text, ActionConfig act)
    {
        if (_busy)
        {
            Toast.Show("正在处理上一条，稍等…");
            return;
        }

        _busy = true;
        var mode = string.IsNullOrWhiteSpace(act.Mode) ? "popup" : act.Mode;

        try
        {
            if (mode == "popup")
            {
                _result ??= new ResultForm { RenderMarkdown = _cfg.RenderMarkdown };
                _result.ShowLoading(act.Name);
            }
            else
            {
                Toast.Show("⏳ 模型生成中…");
            }

            Log.Write($"发起请求: 动作={act.Name} 模式={mode} 输入长度={text.Length}");
            var res = await _llm.CompleteAsync(act.Sys, text, CancellationToken.None);
            Log.Write($"请求完成: 用时={res.ElapsedMs}ms 输出长度={res.Content.Length}");

            switch (mode)
            {
                case "copy":
                    ClipboardHelper.SetText(res.Content);
                    Toast.Show("✓ 结果已复制到剪贴板");
                    break;

                case "replace":
                    await ReplaceSelectionAsync(res.Content, text);
                    break;

                case "type":
                    Input.SendText(res.Content);
                    Toast.Show("✓ 已模拟按键输出结果");
                    break;

                default:
                    _result ??= new ResultForm { RenderMarkdown = _cfg.RenderMarkdown };
                    _result.SetResult(
                        res.Content + $"\n\n—— {res.ElapsedMs / 1000.0:0.0}s · {_cfg.Model}",
                        $"完成 · {res.ElapsedMs / 1000.0:0.0}s · {res.Content.Length} 字",
                        act.Name);
                    break;
            }
        }
        catch (TaskCanceledException)
        {
            Fail("❌ 请求超时（超过 " + _cfg.TimeoutSec + "s）\n服务可能正忙，稍后再试");
        }
        catch (HttpRequestException ex)
        {
            Fail($"❌ 无法连接 {_cfg.ApiBase}\n{ex.Message}\n\n请确认 llama-server 已启动。");
        }
        catch (Exception ex)
        {
            Fail("❌ " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Fail(string message)
    {
        Log.Write(message);
        _result ??= new ResultForm { RenderMarkdown = _cfg.RenderMarkdown };
        _result.SetResult(message, "失败", "错误");
    }

    private async Task ReplaceSelectionAsync(string content, string original)
    {
        // "替换选区"要借剪贴板做中转，但用户原本的剪贴板可能是图片/文件，
        // 不能因为一次替换就把它变成文本。先存全格式快照，完事原样写回。
        var backup = ClipboardSnapshot.Capture();
        try
        {
            ClipboardHelper.SetText(content);
            await Task.Delay(120);
            Input.SendCombo("Ctrl+V");
            await Task.Delay(400);

            if (backup == null || !backup.Restore())
            {
                // 快照拿不到（剪贴板被占用）就退回老做法：至少别把选区文本丢了
                if (!string.IsNullOrEmpty(original)) ClipboardHelper.SetText(original);
            }
        }
        finally
        {
            backup?.Dispose();
        }
        Toast.Show("✓ 已替换选中文本");
    }

    // ---------------- 设置与开关 ----------------

    private void OpenSettings()
    {
        if (_settings == null || _settings.IsDisposed)
        {
            _settings = new SettingsForm(_cfg, ApplyConfig, StartupEnabled(), SetStartup);
        }

        if (!_settings.Visible) _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized)
            _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
        _settings.BringToFront();
    }

    private void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;
        ConfigStore.Save(cfg);
        _llm.Reconfigure(cfg);
        _mouse.Enabled = cfg.MouseTrigger;
        if (_result != null) _result.RenderMarkdown = cfg.RenderMarkdown;
        RegisterHotkeys();
        RefreshMenu();
        Log.Write("配置已保存并应用");
        Toast.Show("✓ 配置已应用");
    }

    private void ToggleMouseTrigger()
    {
        _cfg.MouseTrigger = !_cfg.MouseTrigger;
        _mouse.Enabled = _cfg.MouseTrigger;
        ConfigStore.Save(_cfg);
        RefreshMenu();
        Log.Write("鼠标划词触发: " + (_cfg.MouseTrigger ? "开" : "关"));
        Toast.Show(_cfg.MouseTrigger ? "鼠标划词已开启：拖选/双击文字自动弹条" : "鼠标划词已关闭，仅快捷键可用");
    }

    private static bool StartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void SetStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (on)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\" --tray");
            else
                key.DeleteValue(RunValueName, false);

            Log.Write("开机自启: " + (on ? "开" : "关"));
            Toast.Show(on ? "✓ 已开启开机自启" : "已关闭开机自启");
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 设置开机自启失败: {ex.Message}");
            Toast.Show("设置失败：" + ex.Message);
        }
        finally
        {
            RefreshMenu();
            _settings?.SetStartupState(on);
        }
    }

    // ---------------- 启动参数 ----------------

    private void DispatchStartupArgs()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length == 0) return;

        // 等消息循环起来再执行（否则弹窗会立刻退出）
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();

            string cmd = args[0].TrimStart('-').ToLowerInvariant();
            string rest = string.Join(" ", args.Skip(1)).Trim();

            switch (cmd)
            {
                case "settings":
                    OpenSettings();
                    if (int.TryParse(rest, out int page)) _settings?.ShowPage(page - 1);
                    break;
                case "demo":
                case "bar":
                    ShowBar(rest.Length > 0 ? rest : "这是一段示例文字，用于预览操作条。");
                    break;
                case "ask":
                    if (rest.Length > 0)
                    {
                        var act = _cfg.Actions.FirstOrDefault() ?? AppConfig.Default().Actions[0];
                        RunAction(rest, act);
                    }
                    break;
                case "tray":
                default:
                    break;
            }
        };
        t.Start();
    }

    // ---------------- 其他 ----------------

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

    private static Icon LoadIcon() => AppIcon.Tray;

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Write("程序退出");
        _tray.Visible = false;
        _mouse.Dispose();
        _hotkeys.Dispose();
        _llm.Dispose();
        try { _activate?.Dispose(); } catch { }
        try { _invoker.Dispose(); } catch { }
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        if (!_exiting) ExitApp();
        _tray.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}

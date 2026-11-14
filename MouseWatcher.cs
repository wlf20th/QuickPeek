using System.Windows.Forms;

namespace QuickPeek;

/// <summary>
/// 全局鼠标钩子：拖选（位移 > 15px）或双击时触发。
/// 比 AHK 的 ~LButton 更克制——会过滤掉点到自家窗口和全屏应用的点击。
/// </summary>
internal sealed class MouseWatcher : IDisposable
{
    private readonly Native.LowLevelMouseProc _proc; // 必须保存，否则被 GC 回收导致崩溃
    private IntPtr _hook = IntPtr.Zero;
    private Native.POINT _downPos;
    private Native.POINT _lastUpPos;
    private int _lastUpTick;
    private bool _downOnSelf;
    private bool _downOnTitleBar;
    private uint _clipSeqAtDown;

    public MouseWatcher()
    {
        _proc = Callback;
    }

    public bool Enabled { get; set; }

    /// <summary>
    /// 鼠标按下瞬间的剪贴板序列号。
    /// 有些程序（终端、SSH 客户端）是"选中即复制"：拖选松手时自己就把内容写进剪贴板了，
    /// 这时候我们一个按键都不该发 —— 既拿到了文本，也不会有按键透传打断命令的风险。
    /// </summary>
    public uint ClipSeqAtDown => _clipSeqAtDown;

    /// <summary>外部可以加额外抑制条件（例如正在请求中）。</summary>
    public Func<bool>? AlsoSuppress { get; set; }

    public event Action? DragDetected;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Write($"⚠️ 安装鼠标钩子失败 (错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_LBUTTONDOWN)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    _downPos = data.pt;
                    // 按下点就在自家窗口（操作条）上：可能是拖动操作条，
                    // 松手时可能已经拖到窗口外，所以必须在按下时就记住
                    _downOnSelf = IsOwnWindowAt(data.pt);

                    // 拖标题栏不是划词。之前只看位移，拖窗口也会触发抓取；
                    // 在终端里那一下会发 Ctrl+Shift+C，没有选区时被当成 Ctrl+C，
                    // 直接把正在跑的命令（比如 llama-server）打断。
                    _downOnTitleBar = IsTitleBarAt(data.pt);

                    // 记下按下瞬间的剪贴板序列号：松手后如果这个数字变了，
                    // 说明应用自己把选中内容复制进去了（选中即复制），不需要我们发键
                    _clipSeqAtDown = Native.GetClipboardSequenceNumber();
                }
                else if (msg == Native.WM_LBUTTONUP)
                {
                    var up = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

                    // 真双击必须"同一位置快速点两下"；只比时间的话，
                    // 连着点两个不同按钮也会被当成双击，白白触发抓取。
                    bool dbl = unchecked(Environment.TickCount - _lastUpTick) < 400
                               && Math.Abs(up.pt.X - _lastUpPos.X) <= 4
                               && Math.Abs(up.pt.Y - _lastUpPos.Y) <= 4;
                    _lastUpTick = Environment.TickCount;
                    _lastUpPos = up.pt;

                    if (Enabled && !_downOnSelf && !_downOnTitleBar && !Suppressed())
                    {
                        int dist = Math.Abs(up.pt.X - _downPos.X) + Math.Abs(up.pt.Y - _downPos.Y);
                        if (dist >= 15 || dbl)
                            DragDetected?.Invoke();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"⚠️ 鼠标钩子异常: {ex.Message}");
            }
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>该屏幕坐标上的窗口是否属于本进程（操作条/结果窗/设置窗）。</summary>
    private static bool IsOwnWindowAt(Native.POINT pt)
    {
        try
        {
            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(root, out uint pid);
            return (int)pid == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 该点是否落在窗口标题栏上。用 GetTitleBarInfo 取真实标题栏矩形（屏幕坐标），
    /// 只挡标题栏、不挡客户区——比 WM_NCHITTEST 保守，不会误伤自绘标题栏的应用。
    /// </summary>
    private static bool IsTitleBarAt(Native.POINT pt)
    {
        try
        {
            var hwnd = Native.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root == IntPtr.Zero) return false;

            var info = new Native.TITLEBARINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.TITLEBARINFO>(),
                rgstate = new int[6]
            };
            if (!Native.GetTitleBarInfo(root, ref info)) return false;

            var r = info.rcTitleBar;
            int h = r.Height;
            if (h <= 0 || h > 120) return false;   // 没有标题栏（全屏层/桌面）或数值不合理

            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private bool Suppressed()
    {
        try
        {
            if (AlsoSuppress?.Invoke() == true) return true;

            Native.GetCursorPos(out var pt);

            // 松手时点在自家窗口上 → 不触发
            if (IsOwnWindowAt(pt)) return true;

            // 前台全屏应用（游戏/视频）不打扰
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && Native.GetWindowRect(fg, out var r))
            {
                var scr = Screen.FromPoint(new Point(pt.X, pt.Y)).Bounds;
                if (r.Left <= scr.Left && r.Top <= scr.Top && r.Width >= scr.Width && r.Height >= scr.Height)
                    return true;
            }
        }
        catch
        {
            // 判定失败就当不抑制
        }
        return false;
    }

    public void Dispose() => Stop();
}

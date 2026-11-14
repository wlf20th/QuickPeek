using System.Windows.Forms;

namespace QuickPeek;

/// <summary>
/// 全局热键。AHK 一句话搞定的事，这里要一个隐藏窗口 + RegisterHotKey，
/// 好处是能拿到系统级失败原因（比如组合被别的软件占了）。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private sealed class Sink : NativeWindow
    {
        public Action<int>? Hit;

        public Sink()
        {
            CreateHandle(new CreateParams { Caption = "QuickPeekHotkeySink" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Hit?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }
    }

    private readonly Sink _sink = new();
    private readonly Dictionary<int, string> _registered = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _sink.Hit = id =>
        {
            if (_registered.TryGetValue(id, out var name))
                Pressed?.Invoke(name);
        };
    }

    public event Action<string>? Pressed;

    /// <summary>注册一个热键；失败返回 false 并写日志（组合可能被占用）。</summary>
    public bool Register(string name, string spec)
    {
        if (!Input.TryParseCombo(spec, out uint mods, out uint vk))
        {
            Log.Write($"⚠️ 热键格式无法解析: {name} = '{spec}'");
            return false;
        }

        int id = _nextId++;
        if (!Native.RegisterHotKey(_sink.Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Write($"⚠️ 注册热键失败: {name} = {spec} (Win32 错误 {err}，可能已被其他程序占用)");
            return false;
        }

        _registered[id] = name;
        Log.Write($"热键已注册: {name} = {spec} (id={id})");
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
        {
            Native.UnregisterHotKey(_sink.Handle, id);
        }
        _registered.Clear();
        _nextId = 1;
    }

    public void Dispose()
    {
        UnregisterAll();
        _sink.DestroyHandle();
    }
}

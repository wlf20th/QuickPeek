using System.Windows.Forms;

namespace QuickPeek;

/// <summary>
/// UIA 专用线程。
///
/// 为什么不能直接在抓取线程里调：抓取线程是每次新建、用完就扔的（为了不拖住 UI 上的鼠标钩子），
/// 而 UI Automation 客户端是 COM 组件，需要**长驻 STA 线程 + 消息泵**才稳定。
/// 实测在"临时 STA 线程"里查焦点元素时，发布版会拿不到窗口里的子元素
/// （一路问上去只会问到窗口/桌面），表现成"探针永远 Unknown"，白白退回老路径。
///
/// 所以这里起一个常驻线程专门跑 UIA：调用方把活 Post 过去、带超时地等结果，
/// 抓取线程和 UI 线程都不用管 COM 的事。
/// </summary>
internal static class UiaHost
{
    private static readonly object SpawnGate = new();
    private static Control? _marshal;

    /// <summary>在 UIA 线程上执行；超时或线程起不来返回 false。</summary>
    public static bool TryInvoke<T>(Func<T> work, int timeoutMs, out T result)
    {
        result = default!;
        var m = Marshal();
        if (m == null) return false;

        T local = default!;
        using var done = new ManualResetEventSlim(false);
        try
        {
            m.BeginInvoke(new Action(() =>
            {
                try { local = work(); }
                catch (Exception ex) { Log.Write($"UIA 线程异常: {ex.GetType().Name} {ex.Message}"); }
                finally { done.Set(); }
            }));
        }
        catch (Exception ex)
        {
            Log.Write($"UIA 线程调度失败: {ex.GetType().Name}");
            return false;
        }

        // 超时就放弃：UIA 碰上无响应界面可能卡住，不能让一次抓取一直等下去
        if (!done.Wait(timeoutMs)) return false;

        result = local;
        return true;
    }

    private static Control? Marshal()
    {
        var cur = _marshal;
        if (cur is { IsHandleCreated: true }) return cur;

        lock (SpawnGate)
        {
            cur = _marshal;
            if (cur is { IsHandleCreated: true }) return cur;

            var ready = new ManualResetEventSlim(false);
            Control? created = null;

            var th = new Thread(() =>
            {
                var c = new Control();
                c.CreateControl();          // 先有句柄，BeginInvoke 才能用
                _marshal = c;
                created = c;
                ready.Set();
                Application.Run(new ApplicationContext());   // 消息泵：COM 回调靠它
            })
            {
                IsBackground = true,
                Name = "QuickPeek.UIA"
            };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();

            ready.Wait(3000);
            return created;
        }
    }
}

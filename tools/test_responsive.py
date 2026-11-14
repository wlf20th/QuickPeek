"""量化验证：抓取进行中，UI 线程是否还响应消息（这直接决定全局鼠标卡不卡）。
做法：触发一次必定超时的抓取（约 2.7s），期间用 SendMessageTimeout(WM_NULL) 打点。"""
import ctypes, subprocess, sys, time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_NULL = 0
SMTO_ABORTIFHUNG = 0x0002


def find_app_hwnd(pid):
    """找该进程的任意顶层窗口（含隐藏的），用它的消息队列代表 UI 线程。"""
    res = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        p = ctypes.c_ulong()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            b = ctypes.create_unicode_buffer(256)
            u32.GetClassNameW(h, b, 256)
            res.append((h, b.value))
        return True

    u32.EnumWindows(CB(cb), 0)
    for h, cls in res:
        if "WindowsForms" in cls:
            return h, cls
    return (res[0] if res else (0, ""))


def ping(hwnd, timeout_ms=120):
    res = ctypes.c_size_t(0)
    r = u32.SendMessageTimeoutW(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, timeout_ms, ctypes.byref(res))
    return r != 0


def find_notepad():
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        b = ctypes.create_unicode_buffer(128)
        u32.GetClassNameW(h, b, 128)
        if b.value == "Notepad":
            found.append(h)
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else 0


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)


pid = int(sys.argv[1])
hwnd, cls = find_app_hwnd(pid)
print("app hwnd", hwnd, "class", cls)
if not hwnd:
    sys.exit("找不到窗口")

# 记事本里不放选中文本 -> 抓取必然走到超时分支（最坏情况）
subprocess.Popen(["notepad.exe"])
time.sleep(2.0)
np_ = find_notepad()
if np_:
    u32.ShowWindow(np_, 9)
    u32.SetForegroundWindow(np_)
    time.sleep(0.8)

# 触发 Alt+Q
key(0x12); key(0x51); time.sleep(0.05); key(0x51, True); key(0x12, True)

fails, worst, n = 0, 0, 0
start = time.time()
while time.time() - start < 4.0:
    t0 = time.time()
    ok = ping(hwnd, 120)
    dt = (time.time() - t0) * 1000
    n += 1
    if not ok:
        fails += 1
        worst = max(worst, 120)
        print(f"  ✗ 第{n}次探测超时（UI 线程被阻塞）")
    else:
        worst = max(worst, dt)
    time.sleep(0.05)

print(f"探测 {n} 次，超时 {fails} 次，最大响应 {worst:.0f}ms")
subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)

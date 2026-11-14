"""验证鼠标拖选触发：记事本 -> 粘贴文本 -> 鼠标拖选 -> 期望弹出操作条。"""
import ctypes, subprocess, time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TEXT = ("2026-11-14 13:20:31 ERROR [shop-trade] order-service\n"
        "java.sql.SQLTransientConnectionException: HikariPool-1\n"
        "Caused by: connection leak suspected, active=10 idle=0\n"
        "at com.zaxxer.hikari.pool.HikariPool.createTimeoutException")


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)


def combo(vks, d=0.05):
    for v in vks:
        key(v); time.sleep(d)
    for v in reversed(vks):
        key(v, up=True); time.sleep(d)


def _init_clip():
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.restype = ctypes.c_void_p
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]


def clip_set(text):
    _init_clip()
    u32.OpenClipboard(0); u32.EmptyClipboard()
    if text:
        data = (text + "\0").encode("utf-16-le")
        h = k32.GlobalAlloc(2, len(data))
        p = k32.GlobalLock(h)
        ctypes.memmove(p, data, len(data))
        k32.GlobalUnlock(h)
        u32.SetClipboardData(13, h)
    u32.CloseClipboard()


def clip_get():
    _init_clip()
    u32.OpenClipboard(0)
    u32.GetClipboardData.restype = ctypes.c_void_p
    h = u32.GetClipboardData(13)
    if not h:
        u32.CloseClipboard(); return None
    p = k32.GlobalLock(h)
    out = ctypes.wstring_at(p)
    k32.GlobalUnlock(h)
    u32.CloseClipboard()
    return out


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


def wait_focus(h, timeout=3.0):
    end = time.time() + timeout
    while time.time() < end:
        if u32.GetForegroundWindow() == h:
            return True
        u32.SetForegroundWindow(h)
        time.sleep(0.15)
    return False


subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
time.sleep(0.6)
subprocess.Popen(["notepad.exe"])
time.sleep(2.5)
h = find_notepad()
print("hwnd", h)
u32.ShowWindow(h, 9)
u32.MoveWindow(h, 200, 120, 1000, 620, True)
print("focus:", wait_focus(h))

clip_set(TEXT); time.sleep(0.4)
combo([0x11, 0x56]); time.sleep(1.0)      # 粘贴
combo([0x11, 0x41]); time.sleep(0.5)      # 全选
combo([0x11, 0x43]); time.sleep(0.8)      # 复制，验证文本确实进去了
print("notepad content len:", len(clip_get() or ""))

clip_set("")                              # 清空剪贴板
time.sleep(0.4)

x0, y0 = 280, 330
x1, y1 = 800, 355
u32.SetCursorPos(x0, y0); time.sleep(0.25)
u32.mouse_event(0x0002, 0, 0, 0, 0)
for i in range(1, 31):
    u32.SetCursorPos(x0 + (x1 - x0) * i // 30, y0 + (y1 - y0) * i // 30)
    time.sleep(0.02)
time.sleep(0.2)
u32.mouse_event(0x0004, 0, 0, 0, 0)
print("drag done, fg ok:", u32.GetForegroundWindow() == h)
time.sleep(3)
print("clip after drag:", repr(clip_get())[:70])

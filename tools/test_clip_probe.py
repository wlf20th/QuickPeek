"""诊断：用注入的鼠标拖选 Windows Terminal 后，Python 自己发 Ctrl+Shift+C 能否复制。

用于区分「选区没建立」和「QuickPeek 的发键有问题」。
"""
import ctypes
import time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0002, 0x0004
k32.GlobalAlloc.restype = ctypes.c_void_p
k32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
k32.GlobalLock.restype = ctypes.c_void_p
k32.GlobalLock.argtypes = [ctypes.c_void_p]
k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
u32.SetClipboardData.restype = ctypes.c_void_p
u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def clip_get():
    u32.OpenClipboard(0)
    u32.GetClipboardData.restype = ctypes.c_void_p
    h = u32.GetClipboardData(13)
    if not h:
        u32.CloseClipboard()
        return ""
    p = k32.GlobalLock(h)
    out = ctypes.wstring_at(p)
    k32.GlobalUnlock(h)
    u32.CloseClipboard()
    return out


def clip_clear():
    u32.OpenClipboard(0)
    u32.EmptyClipboard()
    u32.CloseClipboard()


def find_cmd():
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        t = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(h, t, 512)
        if "cmd" in t.value.lower():
            r = RECT()
            u32.GetWindowRect(h, ctypes.byref(r))
            if r.right - r.left > 300:
                found.append((h, r))
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else (None, None)


def drag(x0, y0, x1, y1, steps=20):
    u32.SetCursorPos(x0, y0)
    time.sleep(0.2)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.2)
    for i in range(1, steps + 1):
        u32.SetCursorPos(x0 + (x1 - x0) * i // steps, y0 + (y1 - y0) * i // steps)
        time.sleep(0.05)               # 慢一点，跟真人拖动接近
    time.sleep(0.25)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)


def main():
    import subprocess
    subprocess.Popen(["cmd.exe", "/k", "echo PROBE-LINE-ONE && echo PROBE-LINE-TWO"],
                     creationflags=0x00000010)
    time.sleep(3)

    hwnd, r = find_cmd()
    if not hwnd:
        print("没找到 cmd")
        return
    x, y = r.left, r.top
    print(f"窗口=({x},{y}) 尺寸={r.right - r.left}x{r.bottom - r.top}")

    u32.SetCursorPos(x + 300, y + 200)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)

    class TBI(ctypes.Structure):
        _fields_ = [("cbSize", ctypes.c_ulong), ("rcTitleBar", RECT),
                    ("rgstate", ctypes.c_ulong * 6)]
    info = TBI()
    info.cbSize = ctypes.sizeof(TBI)
    u32.GetTitleBarInfo(hwnd, ctypes.byref(info))
    line_y = info.rcTitleBar.bottom + 22
    print("标题栏底部:", info.rcTitleBar.bottom, "拖选 y =", line_y)

    clip_clear()
    drag(x + 80, line_y, x + 460, line_y)
    print("拖选后剪贴板:", repr(clip_get())[:60])

    # Python 自己发 Ctrl+Shift+C
    u32.keybd_event(0x11, 0, 0, 0)      # Ctrl
    u32.keybd_event(0x10, 0, 0, 0)      # Shift
    time.sleep(0.05)
    u32.keybd_event(0x43, 0, 0, 0)      # C
    time.sleep(0.05)
    u32.keybd_event(0x43, 0, 2, 0)
    u32.keybd_event(0x10, 0, 2, 0)
    u32.keybd_event(0x11, 0, 2, 0)
    time.sleep(1.0)
    print("Ctrl+Shift+C 之后剪贴板:", repr(clip_get())[:120])


if __name__ == "__main__":
    main()

"""真实划词测试：记事本 -> 粘贴文本 -> Ctrl+A -> Alt+Q，验证抓取链路。
注意：Win11 记事本是打包应用，窗口 pid 与启动 pid 不一致，所以按窗口类名找。"""
import ctypes, subprocess, sys, time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TEXT = ("2026-11-14 13:20:31 ERROR [shop-trade] order-service\n"
        "java.sql.SQLTransientConnectionException: HikariPool-1 - Connection is not available\n"
        "Caused by: connection leak suspected, active=10 idle=0")


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)


def combo(vks, delay=0.04):
    for v in vks:
        key(v)
        time.sleep(delay)
    for v in reversed(vks):
        key(v, up=True)
        time.sleep(delay)


def find_notepad():
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        buf = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(h, buf, 256)
        if buf.value == "Notepad":
            found.append(h)
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else 0


def clipboard_set(text):
    """不依赖 tkinter：直接走 Win32 剪贴板 API。"""
    k32 = ctypes.windll.kernel32
    # 64 位下必须显式声明指针返回值，否则句柄被截断成 32 位
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.restype = ctypes.c_void_p
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    u32.OpenClipboard(0)
    u32.EmptyClipboard()
    data = (text + "\0").encode("utf-16-le")
    h = k32.GlobalAlloc(2, len(data))  # GMEM_MOVEABLE
    p = k32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    k32.GlobalUnlock(h)
    u32.SetClipboardData(13, h)       # CF_UNICODETEXT
    u32.CloseClipboard()


def main():
    subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
    time.sleep(0.6)

    proc = subprocess.Popen(["notepad.exe"])
    time.sleep(2.2)

    hwnd = find_notepad()
    if not hwnd:
        print("FAIL: notepad window not found")
        return 1

    u32.ShowWindow(hwnd, 9)
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.6)
    fg = u32.GetForegroundWindow()
    print("notepad hwnd", hwnd, "foreground", fg, "match", fg == hwnd)

    clipboard_set(TEXT)
    time.sleep(0.3)

    combo([0x11, 0x56])      # Ctrl+V
    time.sleep(0.8)
    combo([0x11, 0x41])      # Ctrl+A 全选
    time.sleep(0.6)
    print("sending Alt+Q")
    combo([0x12, 0x51])      # Alt+Q

    time.sleep(5)
    subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())

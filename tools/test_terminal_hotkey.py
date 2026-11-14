"""验证终端里用热键（Alt+Q）仍能抓取，且只发一次键、失败不补发。

前提：QuickPeek 已在运行，hk_peek = Alt+Q。
"""
import ctypes
import time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0002, 0x0004
VK_ALT, VK_Q = 0x12, 0x51


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_cmd():
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        t = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(h, t, 512)
        if "cmd" in t.value.lower() or "命令提示符" in t.value:
            r = RECT()
            u32.GetWindowRect(h, ctypes.byref(r))
            if r.right - r.left > 300:
                found.append((h, r))
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else (None, None)


def drag(x0, y0, x1, y1, steps=14):
    u32.SetCursorPos(x0, y0)
    time.sleep(0.15)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.12)
    for i in range(1, steps + 1):
        u32.SetCursorPos(x0 + (x1 - x0) * i // steps, y0 + (y1 - y0) * i // steps)
        time.sleep(0.03)
    time.sleep(0.1)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)


def hotkey_alt_q():
    u32.keybd_event(VK_ALT, 0, 0, 0)
    time.sleep(0.06)
    u32.keybd_event(VK_Q, 0, 0, 0)
    time.sleep(0.06)
    u32.keybd_event(VK_Q, 0, 2, 0)
    time.sleep(0.06)
    u32.keybd_event(VK_ALT, 0, 2, 0)


def main():
    import subprocess
    subprocess.Popen(["cmd.exe", "/k", "echo HOTKEY-TEST-AAA && echo HOTKEY-TEST-BBB"],
                     creationflags=0x00000010)      # CREATE_NEW_CONSOLE
    time.sleep(3)

    hwnd, r = find_cmd()
    if not hwnd:
        print("没找到 cmd 窗口")
        return
    x, y = r.left, r.top
    w, h = r.right - r.left, r.bottom - r.top
    print(f"cmd 窗口=({x},{y}) {w}x{h}")

    # 点击激活
    u32.SetCursorPos(x + 300, y + 200)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)
    print("前台:", hex(u32.GetForegroundWindow()), "目标:", hex(hwnd))

    # 文本首行在标题栏下方：用 GetTitleBarInfo 拿到标题栏底部，再往下 25px
    class TBI(ctypes.Structure):
        _fields_ = [("cbSize", ctypes.c_ulong), ("rcTitleBar", RECT),
                    ("rgstate", ctypes.c_ulong * 6)]
    info = TBI()
    info.cbSize = ctypes.sizeof(TBI)
    u32.GetTitleBarInfo(hwnd, ctypes.byref(info))
    print("标题栏:", info.rcTitleBar.left, info.rcTitleBar.top, info.rcTitleBar.bottom)
    line_y = max(y + 60, info.rcTitleBar.bottom + 25)

    print(f"拖选文本（y={line_y}）…")
    drag(x + 80, line_y, x + 460, line_y + 2)
    time.sleep(0.5)

    print("按 Alt+Q…")
    hotkey_alt_q()
    time.sleep(4)
    print("完成，请看 qp.log")


if __name__ == "__main__":
    main()

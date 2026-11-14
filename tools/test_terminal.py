"""验证终端里不会再被"拖标题栏/拖一下"误触发抓取。

场景：
  A. 拖 cmd 窗口的标题栏（老版会触发抓取 → 发 Ctrl+Shift+C → 打断命令）
  B. 在 cmd 文本区拖选（应当被跳过，并提示改用热键）
  C. 记事本里拖选（应当正常抓取）

用法：python test_terminal.py
"""
import ctypes
import subprocess
import time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0002, 0x0004


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_window(title_part):
    found = []

    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        t = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(h, t, 512)
        if title_part.lower() in t.value.lower():
            r = RECT()
            u32.GetWindowRect(h, ctypes.byref(r))
            if r.right - r.left > 300:
                found.append((h, r))
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else (None, None)


def drag(x0, y0, x1, y1, steps=12):
    u32.SetCursorPos(x0, y0)
    time.sleep(0.15)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.12)
    for i in range(1, steps + 1):
        u32.SetCursorPos(x0 + (x1 - x0) * i // steps, y0 + (y1 - y0) * i // steps)
        time.sleep(0.03)
    time.sleep(0.1)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.6)


def main():
    proc = subprocess.Popen(
        ["cmd.exe", "/k",
         "echo TEST-LINE-ALPHA-111111 && echo TEST-LINE-BETA-222222 && echo ready"],
        creationflags=0x00000010)      # CREATE_NEW_CONSOLE，否则命令直接跑在当前控制台里

    time.sleep(3)
    hwnd, r = find_window("cmd")
    if not hwnd:
        print("没找到 cmd 窗口")
        return

    x, y = r.left, r.top
    w, h = r.right - r.left, r.bottom - r.top
    print(f"cmd 窗口=({x},{y}) 尺寸={w}x{h}")

    # SetForegroundWindow 会被前台锁定策略忽略，先点一下窗口内部让它真的激活
    u32.SetCursorPos(x + 300, y + 200)
    time.sleep(0.2)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(1.0)
    print("当前前台:", hex(u32.GetForegroundWindow()), "目标:", hex(hwnd))

    print("\n--- A. 拖标题栏（不该触发抓取）")
    drag(x + w // 2, y + 16, x + w // 2 + 120, y + 320)
    time.sleep(2.5)

    print("--- B. 拖文本区（终端里应跳过）")
    # A 拖标题栏把窗口挪走了，必须重新定位，否则会点到别的窗口上
    hwnd, r = find_window("cmd")
    x, y = r.left, r.top
    w, h = r.right - r.left, r.bottom - r.top
    print(f"    窗口已移动 -> ({x},{y})")
    u32.SetCursorPos(x + 300, y + 200)
    time.sleep(0.2)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)
    drag(x + 80, y + 150, x + 320, y + 152)   # 文本区（避开标签栏）
    time.sleep(3)

    print("\n完成，请查看 qp.log")


if __name__ == "__main__":
    main()

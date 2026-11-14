"""验证操作条是否出现在鼠标正下方。

注意：某些环境（虚拟机/远程会话）里 SetCursorPos 后指针会被设备回弹，
所以设置完必须用 GetCursorPos 回读确认，再以回读值作为判定基准。
"""
import ctypes
import subprocess
import time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

EXE = r"C:\Users\Lianfa\WorkBuddy\2026-11-14-13-37-57\QuickPeekCS\bin\Debug\net8.0-windows\QuickPeek.exe"


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def cursor():
    p = POINT()
    u32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def place_cursor(mx, my):
    """移动指针并回读，直到系统确认位置稳定。"""
    for _ in range(6):
        u32.SetCursorPos(mx, my)
        time.sleep(0.35)
        cx, cy = cursor()
        if (cx, cy) == (mx, my):
            return cx, cy
    return cursor()


def windows_of(pid):
    out = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        p = ctypes.c_ulong()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and u32.IsWindowVisible(h):
            cls = ctypes.create_unicode_buffer(128)
            u32.GetClassNameW(h, cls, 128)
            if "WindowsForms" in cls.value:
                r = RECT()
                u32.GetWindowRect(h, ctypes.byref(r))
                out.append((cls.value, r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True

    u32.EnumWindows(CB(cb), 0)
    return out


def run(mx, my, label):
    cx, cy = place_cursor(mx, my)
    print(f"\n[{label}] 请求=({mx},{my}) 实际鼠标=({cx},{cy})")

    proc = subprocess.Popen([EXE, "--demo", "选中这段文字后应该出现在鼠标正下方，验证定位是否跟随光标"])
    time.sleep(2.5)

    ws = windows_of(proc.pid)
    if not ws:
        print("  没找到窗口")
    for _, x, y, w, h in ws:
        print(f"  窗口=({x},{y}) 尺寸={w}x{h}")
        print(f"  相对鼠标：dx={x - cx} dy={y - cy}")
        print(f"  水平居中偏差={(x + w // 2) - cx}px")
        print(f"  在鼠标下方：{'是' if y >= cy else '否（下方放不下，翻到上方）'}")
        print(f"  水平居中：{'是' if abs((x + w // 2) - cx) <= 40 else '否（贴边夹紧）'}")

    time.sleep(0.4)
    proc.kill()
    time.sleep(0.6)


run(1280, 620, "屏幕中部")
run(700, 1320, "靠近底部（应翻到上方）")
run(2540, 700, "贴右边缘（应夹紧）")

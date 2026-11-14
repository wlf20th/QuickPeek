"""诊断：哪些窗口会把客户区也报告成非客户区（HTCAPTION 等）。

对每个可见顶层窗口：
  - 打印窗口矩形
  - 用 GetTitleBarInfo 取真实标题栏矩形
  - 在（标题栏中间 / 内容区中央 / 内容区偏上）三个点发 WM_NCHITTEST

用法：python hittest.py
"""
import ctypes
from ctypes import wintypes

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_NCHITTEST = 0x0084


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class TITLEBARINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_ulong),
                ("rcTitleBar", RECT),
                ("rgstate", ctypes.c_ulong * 3)]


HIT_NAMES = {
    0: "HTNOWHERE", 1: "HTCLIENT", 2: "HTCAPTION", 3: "HTSYSMENU",
    10: "HTLEFT", 11: "HTRIGHT", 12: "HTTOP", 13: "HTTOPLEFT",
    14: "HTTOPRIGHT", 15: "HTBOTTOM", 16: "HTBOTTOMLEFT", 17: "HTBOTTOMRIGHT",
}


def hittest(hwnd, x, y):
    lparam = (y << 16) | (x & 0xFFFF)
    res = ctypes.c_void_p()
    ok = u32.SendMessageTimeoutW(hwnd, WM_NCHITTEST, 0, lparam, 0x0002, 120, ctypes.byref(res))
    if not ok:
        return "TIMEOUT"
    v = ctypes.cast(res, ctypes.POINTER(ctypes.c_long))[0] if res else 0
    return HIT_NAMES.get(v, f"HT{v}")


def titlebar_rect(hwnd):
    info = TITLEBARINFO()
    info.cbSize = ctypes.sizeof(TITLEBARINFO)
    if not u32.GetTitleBarInfo(hwnd, ctypes.byref(info)):
        return None
    return info.rcTitleBar


def main():
    targets = []

    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        n = u32.GetWindowTextLengthW(h)
        if n <= 0:
            return True
        r = RECT()
        u32.GetWindowRect(h, ctypes.byref(r))
        w, hh = r.right - r.left, r.bottom - r.top
        if w < 400 or hh < 300:
            return True
        t = ctypes.create_unicode_buffer(256)
        u32.GetWindowTextW(h, t, 256)
        targets.append((h, t.value[:40], r))
        return True

    u32.EnumWindows(CB(cb), 0)

    for hwnd, title, r in targets[:12]:
        w, hh = r.right - r.left, r.bottom - r.top
        tb = titlebar_rect(hwnd)
        tb_s = "无" if not tb or tb.bottom <= tb.top else f"({tb.left},{tb.top},{tb.right},{tb.bottom})"
        p_title = hittest(hwnd, r.left + w // 2, r.top + max(8, (tb.bottom - tb.top) // 2 if tb else 12))
        p_center = hittest(hwnd, r.left + w // 2, r.top + hh // 2)
        p_upper = hittest(hwnd, r.left + w // 2, r.top + 70)
        print(f"\n{title!r}  rect=({r.left},{r.top},{w}x{hh})  标题栏={tb_s}")
        print(f"   标题栏中部 -> {p_title}   内容区中央 -> {p_center}   内容区 y+70 -> {p_upper}")


if __name__ == "__main__":
    main()

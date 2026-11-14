"""直接验证用户遇到的问题：QuickPeek 会不会打断正在跑的 bat。

做法：起一个每 ~1 秒写一行的 bat，然后做各种"会误触发"的鼠标动作，
看日志文件是否还在增长（增长 = bat 没被打断）。

场景：
  A. 拖窗口标题栏（旧版会触发抓取 -> Ctrl+Shift+C -> 终端当成 Ctrl+C -> 打断）
  B. 在内容区空白处拖一下
  C. 单击窗口内部（不该触发）
"""
import ctypes
import os
import subprocess
import time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0002, 0x0004
LOG = os.path.join(os.environ["TEMP"], "qp_bat_test.log")
BAT = os.path.join(os.environ["TEMP"], "qp_bat_test.bat")


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_win(*title_parts):
    found = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        t = ctypes.create_unicode_buffer(512)
        u32.GetWindowTextW(h, t, 512)
        for part in title_parts:
            if part.lower() in t.value.lower():
                r = RECT()
                u32.GetWindowRect(h, ctypes.byref(r))
                if r.right - r.left > 300:
                    found.append((h, r))
                break
        return True

    u32.EnumWindows(CB(cb), 0)
    return found[-1] if found else (None, None)


def click(hwnd, x, y):
    u32.SetCursorPos(x, y)
    time.sleep(0.2)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.06)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)


def drag(x0, y0, x1, y1, steps=16):
    u32.SetCursorPos(x0, y0)
    time.sleep(0.2)
    u32.mouse_event(WM_LBUTTONDOWN, 0, 0, 0, 0)
    time.sleep(0.15)
    for i in range(1, steps + 1):
        u32.SetCursorPos(x0 + (x1 - x0) * i // steps, y0 + (y1 - y0) * i // steps)
        time.sleep(0.04)
    time.sleep(0.15)
    u32.mouse_event(WM_LBUTTONUP, 0, 0, 0, 0)
    time.sleep(0.8)


def log_lines():
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            return sum(1 for _ in f)
    except FileNotFoundError:
        return 0


def check(before, label):
    time.sleep(3.5)
    now = log_lines()
    alive = (now - before) >= 2
    print("  %s: %d -> %d 行（%s）" % (label, before, now, "仍在跑 OK" if alive else "可能被打断 FAIL"))
    return now, alive


def main():
    with open(LOG, "w", encoding="utf-8"):
        pass

    with open(BAT, "w", encoding="utf-8") as f:
        f.write('@echo off\n')
        f.write(':loop\n')
        f.write('echo tick %time% >> "' + LOG + '"\n')
        f.write('ping -n 2 127.0.0.1 >nul\n')
        f.write('goto loop\n')

    subprocess.Popen(["cmd.exe", "/c", BAT], creationflags=0x00000010)
    time.sleep(6)

    hwnd, r = find_win("命令提示符", "qp_bat", "cmd.exe")
    if not hwnd:
        print("没找到 bat 窗口")
        return
    x, y = r.left, r.top
    w, h = r.right - r.left, r.bottom - r.top
    print("bat 窗口=(%d,%d) %dx%d" % (x, y, w, h))

    click(hwnd, x + 300, y + 200)
    base = log_lines()
    print("起始 %d 行，仍在跑：" % base)

    print("\nA. 拖窗口标题栏")
    drag(x + w // 2, y + 16, x + w // 2 + 140, y + 330)
    base, okA = check(base, "A 拖标题栏")

    hwnd, r = find_win("命令提示符", "qp_bat", "cmd.exe")
    x, y = r.left, r.top
    w, h = r.right - r.left, r.bottom - r.top
    click(hwnd, x + 300, y + 200)

    print("\nB. 内容区空白处拖一下")
    drag(x + 120, y + h - 120, x + 520, y + h - 118)
    base, okB = check(base, "B 拖空白区")

    print("\nC. 单击窗口内部")
    click(hwnd, x + 400, y + 220)
    base, okC = check(base, "C 单击")

    ok = okA and okB and okC
    print("\n结论：" + ("三个场景都没有打断 bat" if ok else "有场景把 bat 打断了"))
    print("日志：" + LOG)
    subprocess.run(["taskkill", "/F", "/IM", "ping.exe"], capture_output=True)


if __name__ == "__main__":
    main()

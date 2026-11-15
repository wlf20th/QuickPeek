"""端到端：用 Alt+Q 热键触发真实抓取，验证非文本剪贴板是否被保住。

选热键而不选鼠标拖选，是因为拖选依赖窗口坐标，容易被工具栏/菜单接走；
记事本里 Ctrl+A 就有确定的选区，热键路径同样会走"备份 → 发键 → 还原"。
"""
import ctypes
import os
import struct
import subprocess
import time

import win32clipboard as cb
import win32con

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.environ.get("QP_LOG") or os.path.join(HERE, "..", "publish", "qp.log")

_orig = cb.OpenClipboard


def open_retry():
    for _ in range(50):
        try:
            _orig()
            return
        except Exception:
            time.sleep(0.03)
    raise RuntimeError("剪贴板打不开")


cb.OpenClipboard = open_retry

STD = {1: "CF_TEXT", 2: "CF_BITMAP", 3: "CF_METAFILEPICT", 8: "CF_DIB",
       9: "CF_PALETTE", 13: "CF_UNICODETEXT", 14: "CF_ENHMETAFILE",
       15: "CF_HDROP", 16: "CF_LOCALE", 17: "CF_DIBV5"}


def log_lines():
    try:
        with open(LOG, encoding="utf-8", errors="ignore") as f:
            return f.read().splitlines()
    except Exception:
        return []


def formats():
    cb.OpenClipboard()
    out = []
    try:
        f = 0
        while True:
            f = cb.EnumClipboardFormats(f)
            if not f:
                break
            n = STD.get(f)
            if not n:
                try:
                    n = cb.GetClipboardFormatName(f)
                except Exception:
                    n = f"fmt#{f}"
            out.append(n)
    finally:
        cb.CloseClipboard()
    return out


def get(fmt):
    cb.OpenClipboard()
    try:
        return cb.GetClipboardData(fmt)
    except Exception:
        return None
    finally:
        cb.CloseClipboard()


def set_text(t):
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_UNICODETEXT, t)
    cb.CloseClipboard()


def set_files(paths):
    header = struct.pack("<Iiiii", 20, 0, 0, 0, 1)
    files = "\0".join(paths) + "\0\0"
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_HDROP, header + files.encode("utf-16-le"))
    cb.CloseClipboard()


def set_image(bmp):
    raw = open(bmp, "rb").read()[14:]
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_DIB, raw)
    cb.CloseClipboard()


def set_html(marker):
    fmt = cb.RegisterClipboardFormat("HTML Format")
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(fmt, f"<html><body><b>{marker}</b></body></html>".encode("utf-8"))
    cb.CloseClipboard()


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)
    time.sleep(0.04)


def combo(vks):
    for v in vks:
        key(v)
    for v in reversed(vks):
        key(v, up=True)
    time.sleep(0.3)


def find_notepad():
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    found = []

    def cbf(h, l):
        if not u32.IsWindowVisible(h):
            return True
        cls = ctypes.create_unicode_buffer(120)
        u32.GetClassNameW(h, cls, 120)
        if cls.value == "Notepad":
            found.append(h)
        return True

    u32.EnumWindows(CB(cbf), 0)
    return found[-1] if found else None


def focus(h):
    u32.ShowWindow(h, 9)
    time.sleep(0.3)
    for _ in range(4):
        fg = u32.GetForegroundWindow()
        ft = u32.GetWindowThreadProcessId(fg, None)
        mt = k32.GetCurrentThreadId()
        u32.AttachThreadInput(mt, ft, True)
        u32.SetForegroundWindow(h)
        u32.BringWindowToTop(h)
        u32.AttachThreadInput(mt, ft, False)
        time.sleep(0.45)
        if u32.GetForegroundWindow() == h:
            return True
    return False


DONE = ("抓取成功", "等待剪贴板超时", "不做任何动作", "不发键、不弹条",
        "本次放弃", "只有空白字符", "选区由 UIA 直接读取", "热键触发")


def wait_done(before, timeout=10.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        new = log_lines()[before:]
        if any(any(k in l for k in DONE) for l in new):
            time.sleep(0.5)
            return log_lines()[before:]
        time.sleep(0.05)
    return log_lines()[before:]


def hide_bar():
    """操作条弹出来会抢焦点，按 Esc 收掉，避免干扰下一轮。"""
    key(0x1B)
    key(0x1B, up=True)
    time.sleep(0.5)


def main():
    h = find_notepad()
    if not h:
        print("没有记事本窗口，先启动一个")
        return
    if not focus(h):
        print("警告: 记事本没抢到前台")

    # 光标到文末，清掉旧选区
    combo([0x11, 0x23])

    bmp = os.path.join(os.environ["TEMP"], "qp_test.bmp")
    from PIL import Image, ImageDraw
    im = Image.new("RGB", (80, 50), (200, 60, 60))
    ImageDraw.Draw(im).ellipse([10, 10, 70, 40], fill=(255, 220, 0))
    im.save(bmp, "BMP")

    fpath = os.path.join(os.environ["TEMP"], "qp_clip_file.txt")
    open(fpath, "w").write("preserve-me")

    cases = [
        ("文件(CF_HDROP)", lambda: set_files([fpath]),
         lambda: get(win32con.CF_HDROP) == (fpath,)),
        ("图片(CF_DIB)", lambda: set_image(bmp),
         lambda: get(win32con.CF_DIB) is not None and len(get(win32con.CF_DIB)) > 1000),
        ("富文本(HTML)", lambda: set_html("MARKER-KEEP-999"),
         lambda: "MARKER-KEEP-999" in read_reg("HTML Format")),
    ]

    for title, make, verify in cases:
        if not focus(h):
            print("警告: 焦点丢了")
        combo([0x11, 0x41])   # Ctrl+A 选中全部
        time.sleep(0.4)

        clear_sel = formats()
        make()
        before = formats()
        print(f"\n=== {title} ===")
        print("  前:", before)

        n = len(log_lines())
        combo([0x12, 0x51])   # Alt+Q
        for line in wait_done(n):
            print("   ", line[:160])

        after = formats()
        print("  后:", after)
        print("  格式一致:", after == before, "  内容校验:", verify())
        hide_bar()

    # 无选区场景：点一下取消选区，剪贴板应一动不动
    if not focus(h):
        print("警告: 焦点丢了")
    combo([0x11, 0x23])       # Ctrl+End，无选区
    set_files([fpath])
    before = formats()
    seq = u32.GetClipboardSequenceNumber()
    print("\n=== 无选区 + 热键 ===")
    n = len(log_lines())
    combo([0x12, 0x51])
    for line in wait_done(n):
        print("   ", line[:160])
    print("  序列号:", seq, "→", u32.GetClipboardSequenceNumber())
    print("  格式一致:", formats() == before, "  文件仍在:", get(win32con.CF_HDROP) == (fpath,))
    hide_bar()


def read_reg(name):
    fmt = cb.RegisterClipboardFormat(name)
    cb.OpenClipboard()
    try:
        d = cb.GetClipboardData(fmt)
        return d.decode("utf-8", "ignore") if isinstance(d, bytes) else str(d)
    except Exception:
        return ""
    finally:
        cb.CloseClipboard()


if __name__ == "__main__":
    main()

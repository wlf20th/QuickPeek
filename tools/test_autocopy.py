"""验证"选中即复制"快路径：拖选过程中若剪贴板被应用改写，应直接用该内容出条、且一个键都不发。"""
import ctypes
import subprocess
import sys
import threading
import time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

MARKER = "AUTOCOPY-MARKER-这是应用自己复制的内容-9876"
TEXT = "SELECTION-THAT-SHOULD-NOT-BE-USED-0000"


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
    u32.OpenClipboard(0)
    u32.EmptyClipboard()
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
        u32.CloseClipboard()
        return None
    p = k32.GlobalLock(h)
    out = ctypes.wstring_at(p)
    k32.GlobalUnlock(h)
    u32.CloseClipboard()
    return out


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_notepad():
    out = []

    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        if not u32.IsWindowVisible(h):
            return True
        b = ctypes.create_unicode_buffer(128)
        u32.GetClassNameW(h, b, 128)
        if b.value == "Notepad":
            out.append(h)
        return True

    u32.EnumWindows(CB(cb), 0)
    return out[-1] if out else 0


def main():
    subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
    time.sleep(0.6)
    subprocess.Popen(["notepad.exe"])
    time.sleep(2.5)

    h = find_notepad()
    if not h:
        print("记事本没起来")
        return
    u32.ShowWindow(h, 9)
    u32.MoveWindow(h, 200, 140, 1000, 600, True)
    u32.SetForegroundWindow(h)
    time.sleep(0.8)

    clip_set(TEXT)
    time.sleep(0.3)
    u32.keybd_event(0x11, 0, 0, 0)
    u32.keybd_event(0x56, 0, 0, 0)
    u32.keybd_event(0x56, 0, 2, 0)
    u32.keybd_event(0x11, 0, 2, 0)
    time.sleep(1.2)

    r = RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    x0, y0 = r.left + 30, r.top + 130
    x1 = x0 + 460

    print(f"窗口=({r.left},{r.top}) 拖选 y={y0}")
    u32.SetCursorPos(x0, y0)
    time.sleep(0.15)
    u32.mouse_event(0x0002, 0, 0, 0, 0)          # 按下
    time.sleep(0.12)

    # 按下之后、松手之前，模拟"应用自己把选中内容复制进剪贴板"
    def auto_copy():
        time.sleep(0.05)
        clip_set(MARKER)
        print("  [模拟] 已写入剪贴板:", MARKER[:22], "...")

    th = threading.Thread(target=auto_copy)
    th.start()

    for i in range(1, 26):
        nx = x0 + (x1 - x0) * i // 25
        u32.SetCursorPos(nx, y0)
        time.sleep(0.012)
    time.sleep(0.15)
    u32.mouse_event(0x0004, 0, 0, 0, 0)          # 松开
    th.join()
    print("松手")

    time.sleep(2.0)
    print("剪贴板现在是:", repr((clip_get() or "")[:40]))
    print("期望：操作条显示 MARKER 内容，且日志里没有『使用按键』那行")


if __name__ == "__main__":
    main()

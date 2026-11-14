"""验证：不关闭操作条，重新划选新文本，操作条内容应该跟着更新。
用法：test_reselect.py <QuickPeek pid>"""
import ctypes, os, subprocess, sys, time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TEXT = "FIRST-SELECTION-AAAA\n" + "\n" * 10 + "SECOND-SELECTION-BBBB"


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
    data = (text + "\0").encode("utf-16-le")
    h = k32.GlobalAlloc(2, len(data))
    p = k32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    k32.GlobalUnlock(h)
    u32.SetClipboardData(13, h)
    u32.CloseClipboard()


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)


def combo(vks, d=0.05):
    for v in vks:
        key(v); time.sleep(d)
    for v in reversed(vks):
        key(v, up=True); time.sleep(d)


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


def drag(x0, y0, x1, y1):
    u32.SetCursorPos(x0, y0); time.sleep(0.25)
    u32.mouse_event(0x0002, 0, 0, 0, 0)
    for i in range(1, 31):
        u32.SetCursorPos(x0 + (x1 - x0) * i // 30, y0 + (y1 - y0) * i // 30)
        time.sleep(0.02)
    time.sleep(0.2)
    u32.mouse_event(0x0004, 0, 0, 0, 0)


def snap(pid, out):
    subprocess.run([PY, os.path.join(HERE, "shot.py"), str(pid), out],
                   capture_output=True, text=True)
    return out


def main():
    pid = int(sys.argv[1])

    subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
    time.sleep(0.6)
    subprocess.Popen(["notepad.exe"])
    time.sleep(2.5)
    h = find_notepad()
    u32.ShowWindow(h, 9)
    u32.MoveWindow(h, 200, 120, 1000, 620, True)
    u32.SetForegroundWindow(h)
    time.sleep(0.8)

    clip_set(TEXT); time.sleep(0.4)
    combo([0x11, 0x56]); time.sleep(1.2)      # 粘贴

    print("拖选第一行…")
    drag(235, 270, 350, 271)
    time.sleep(2.0)
    snap(pid, os.path.join(HERE, "reselect_bar1.png"))

    print("不关闭操作条，直接拖选第二行…")
    drag(235, 578, 350, 579)
    time.sleep(2.0)
    snap(pid, os.path.join(HERE, "reselect_bar2.png"))

    subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
    print("done")


if __name__ == "__main__":
    main()

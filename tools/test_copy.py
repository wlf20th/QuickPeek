"""对照实验：只用 python 注入 Ctrl+C，看记事本能不能复制。"""
import ctypes, subprocess, sys, time

u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TEXT = "hello quickpeek 12345"


def key(vk, up=False):
    u32.keybd_event(vk, 0, 2 if up else 0, 0)


def combo(vks, d=0.05):
    for v in vks:
        key(v); time.sleep(d)
    for v in reversed(vks):
        key(v, up=True); time.sleep(d)


def clip_set(text):
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.restype = ctypes.c_void_p
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    u32.OpenClipboard(0); u32.EmptyClipboard()
    data = (text + "\0").encode("utf-16-le")
    h = k32.GlobalAlloc(2, len(data))
    p = k32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    k32.GlobalUnlock(h)
    u32.SetClipboardData(13, h)
    u32.CloseClipboard()


def clip_get():
    u32.OpenClipboard(0)
    h = u32.GetClipboardData(13)
    u32.GetClipboardData.restype = ctypes.c_void_p
    h = u32.GetClipboardData(13)
    if not h:
        u32.CloseClipboard()
        return None
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    p = k32.GlobalLock(h)
    out = ctypes.wstring_at(p)
    k32.GlobalUnlock(h)
    u32.CloseClipboard()
    return out


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


subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)
time.sleep(0.6)
subprocess.Popen(["notepad.exe"])
time.sleep(2.0)
h = find_notepad()
print("hwnd", h)
u32.ShowWindow(h, 9); u32.SetForegroundWindow(h); time.sleep(0.6)
print("fg match", u32.GetForegroundWindow() == h)

clip_set(TEXT); time.sleep(0.4)
print("clip before", repr(clip_get()))

combo([0x11, 0x56]); time.sleep(0.8)     # Ctrl+V
combo([0x11, 0x41]); time.sleep(0.6)     # Ctrl+A
clip_set("")                              # 清空，模拟抓取前状态
time.sleep(0.3)
print("clip cleared", repr(clip_get()))
combo([0x11, 0x43])                       # Ctrl+C
time.sleep(0.8)
print("clip after ctrl+c", repr(clip_get()))

subprocess.run(["taskkill", "/IM", "Notepad.exe", "/F"], capture_output=True)

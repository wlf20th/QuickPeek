"""读取结果窗 RichEdit 里的实际文本，用来核对转义/解析是否破坏了内容。

用法：python gettext.py <pid> [关键字]
"""
import ctypes
import sys

u32 = ctypes.windll.user32
WM_GETTEXT, WM_GETTEXTLENGTH = 0x000D, 0x000E


def find_richedit(pid):
    target = []

    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def on_child(h, l):
        cls = ctypes.create_unicode_buffer(64)
        u32.GetClassNameW(h, cls, 64)
        if "RICHEDIT" in cls.value.upper():
            target.append(h)
            return False
        return True

    def on_top(h, l):
        p = ctypes.c_ulong()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            u32.EnumChildWindows(h, CB(on_child), 0)
        return True

    u32.EnumWindows(CB(on_top), 0)
    return target[0] if target else None


def get_text(hwnd):
    n = u32.SendMessageW(hwnd, WM_GETTEXTLENGTH, 0, 0)
    buf = ctypes.create_unicode_buffer(n + 2)
    u32.SendMessageW(hwnd, WM_GETTEXT, n + 1, buf)
    return buf.value


def main():
    pid = int(sys.argv[1])
    hwnd = find_richedit(pid)
    if not hwnd:
        print("没找到 RichEdit")
        return
    text = get_text(hwnd)
    print("=== 控件文本长度:", len(text))
    if len(sys.argv) > 2:
        key = sys.argv[2]
        idx = text.find(key)
        print("=== 关键字", key, "位置", idx)
        if idx >= 0:
            print(repr(text[max(0, idx - 60): idx + 200]))
    else:
        print(repr(text[:1200]))


if __name__ == "__main__":
    main()

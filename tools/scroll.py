"""给结果窗里的 RichEdit 发翻页/滚动消息，便于截图查看长内容的不同部分。

用法：python scroll.py <pid> [pages]  —— pages 为负数表示向上
"""
import ctypes
import sys

u32 = ctypes.windll.user32
WM_VSCROLL = 0x0115
SB_LINEDOWN, SB_LINEUP, SB_PAGEDOWN, SB_PAGEUP, SB_BOTTOM = 1, 0, 3, 2, 7


def main():
    pid = int(sys.argv[1])
    pages = int(sys.argv[2]) if len(sys.argv) > 2 else 1

    target = None
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, l):
        nonlocal target
        p = ctypes.c_ulong()
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value != pid:
            return True
        cls = ctypes.create_unicode_buffer(64)
        u32.GetClassNameW(h, cls, 64)
        if "RICHEDIT" in cls.value.upper():
            target = h
            return False
        return True

    u32.EnumWindows(CB(cb), 0)
    if target is None:
        # 富文本框是子窗口，EnumWindows 找不到，改用 EnumChildWindows
        CB2 = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

        def cb2(h, l):
            nonlocal target
            cls = ctypes.create_unicode_buffer(64)
            u32.GetClassNameW(h, cls, 64)
            if "RICHEDIT" in cls.value.upper():
                target = h
                return False
            return True

        def top(h, l):
            p = ctypes.c_ulong()
            u32.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == pid:
                u32.EnumChildWindows(h, CB2(cb2), 0)
            return True

        u32.EnumWindows(CB(top), 0)

    if target is None:
        print("没找到 RichEdit 控件")
        return

    cmd = SB_PAGEDOWN if pages > 0 else SB_PAGEUP
    for _ in range(abs(pages)):
        u32.SendMessageW(target, WM_VSCROLL, cmd, 0)
    print(f"scrolled {pages} page(s) on {target}")
    return target


if __name__ == "__main__":
    main()

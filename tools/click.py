"""在屏幕绝对坐标点一下（用于自动化测试 GUI）。用法：click.py x y"""
import ctypes, sys, time

u32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

x, y = int(sys.argv[1]), int(sys.argv[2])
u32.SetCursorPos(x, y)
time.sleep(0.15)
u32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
time.sleep(0.08)
u32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
print("clicked", x, y)

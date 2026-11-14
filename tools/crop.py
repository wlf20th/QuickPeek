"""截取屏幕指定区域。用法：crop.py x y w h out.png"""
import ctypes, sys
from ctypes import wintypes
from PIL import Image

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

x, y, w, h = (int(v) for v in sys.argv[1:5])
out = sys.argv[5]

hdc = u32.GetDC(0)
mdc = g32.CreateCompatibleDC(hdc)
bmp = g32.CreateCompatibleBitmap(hdc, w, h)
g32.SelectObject(mdc, bmp)
g32.BitBlt(mdc, 0, 0, w, h, hdc, x, y, 0x00CC0020)


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD),
                ("biCompression", wintypes.DWORD), ("biSizeImage", wintypes.DWORD),
                ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
                ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD)]


bi = BIH(); bi.biSize = ctypes.sizeof(BIH); bi.biWidth = w; bi.biHeight = -h
bi.biPlanes = 1; bi.biBitCount = 32
buf = ctypes.create_string_buffer(w * h * 4)
g32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB").save(out)
g32.DeleteObject(bmp); g32.DeleteDC(mdc); u32.ReleaseDC(0, hdc)
print("saved", out)

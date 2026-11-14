"""截取指定进程的窗口（零依赖，ctypes + PrintWindow）。用法：shot.py <pid> <out.png>"""
import ctypes, ctypes.wintypes as wt, sys, zlib, struct

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 2
SRCCOPY = 0x00CC0020

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

def windows_of(pid):
    out = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, lparam):
        p = wt.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and u32.IsWindowVisible(hwnd):
            r = RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            w, h = r.right - r.left, r.bottom - r.top
            if w > 30 and h > 20:
                out.append((w * h, hwnd, w, h, r.left, r.top))
        return True
    u32.EnumWindows(CB(cb), 0)
    out.sort(reverse=True)
    return out

def capture(hwnd, w, h):
    hdc = u32.GetWindowDC(hwnd)
    mdc = g32.CreateCompatibleDC(hdc)
    bmp = g32.CreateCompatibleBitmap(hdc, w, h)
    g32.SelectObject(mdc, bmp)
    u32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]
    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth, bi.biHeight = w, -h
    bi.biPlanes, bi.biBitCount = 1, 32
    bi.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    g32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)

    rows = []
    for y in range(h):
        row = buf.raw[y * w * 4:(y + 1) * w * 4]
        # BGRA -> RGB
        rgb = bytearray(w * 3)
        rgb[0::3] = row[2::4]
        rgb[1::3] = row[1::4]
        rgb[2::3] = row[0::4]
        rows.append(b"\x00" + bytes(rgb))

    raw = b"".join(rows)
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))

    g32.DeleteObject(bmp)
    g32.DeleteDC(mdc)
    u32.ReleaseDC(hwnd, hdc)
    return png

if __name__ == "__main__":
    pid, out = int(sys.argv[1]), sys.argv[2]
    wins = windows_of(pid)
    if not wins:
        print("no visible window")
        sys.exit(1)
    area, hwnd, w, h, x, y = wins[0]
    print(f"capture hwnd={hwnd} {w}x{h} at ({x},{y})  total={len(wins)}")
    open(out, "wb").write(capture(hwnd, w, h))

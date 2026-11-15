"""精确验证 ClipboardSnapshot：设置 -> 快照 -> 覆盖剪贴板 -> 还原 -> 比对。

不依赖鼠标拖选，直接调用 QuickPeek.exe --snaptest，
避免被窗口焦点/时序干扰。
"""
import ctypes
import os
import struct
import subprocess
import sys
import time

import win32clipboard as cb
import win32con

HERE = os.path.dirname(os.path.abspath(__file__))
_DEFAULT_EXE = os.path.join(HERE, "..", "publish", "QuickPeek.exe")
EXE = os.environ.get("QP_EXE") or _DEFAULT_EXE
LOG = os.environ.get("QP_LOG") or os.path.join(os.path.dirname(_DEFAULT_EXE), "qp.log")
ENV = dict(os.environ)
ENV["DOTNET_ROOT"] = os.environ.get("DOTNET_ROOT") or os.path.expanduser(r"~\.dotnet")

# 上一轮进程刚退出时剪贴板可能还被短暂持有，给 OpenClipboard 加重试，
# 免得把"测试脚本抢不到剪贴板"误报成产品问题
_orig_open = cb.OpenClipboard


def _open_retry():
    for _ in range(50):
        try:
            _orig_open()
            return
        except Exception:
            time.sleep(0.03)
    raise RuntimeError("剪贴板打不开（被其他进程长期占用）")


cb.OpenClipboard = _open_retry

STD = {
    1: "CF_TEXT", 2: "CF_BITMAP", 3: "CF_METAFILEPICT", 8: "CF_DIB",
    9: "CF_PALETTE", 13: "CF_UNICODETEXT", 14: "CF_ENHMETAFILE",
    15: "CF_HDROP", 16: "CF_LOCALE", 17: "CF_DIBV5",
}


def log_lines():
    try:
        with open(LOG, encoding="utf-8", errors="ignore") as f:
            return f.read().splitlines()
    except Exception:
        return []


def snapshot_state():
    """把剪贴板当前内容做成可比对的快照。"""
    cb.OpenClipboard()
    try:
        formats, payload = [], {}
        f = 0
        while True:
            f = cb.EnumClipboardFormats(f)
            if not f:
                break
            name = STD.get(f, "(注册格式)")
            if name == "(注册格式)":
                try:
                    name = cb.GetClipboardFormatName(f)
                except Exception:
                    name = f"fmt#{f}"
            formats.append(name)
            try:
                if f == win32con.CF_UNICODETEXT:
                    payload[name] = cb.GetClipboardData(f)
                elif f == win32con.CF_HDROP:
                    payload[name] = cb.GetClipboardData(f)
                elif f in (win32con.CF_DIB, win32con.CF_DIBV5):
                    payload[name] = ("DIB", len(cb.GetClipboardData(f)))
                else:
                    try:
                        d = cb.GetClipboardData(f)
                        payload[name] = ("data", len(d) if isinstance(d, (bytes, str)) else type(d).__name__)
                    except Exception:
                        payload[name] = "(不可读)"
            except Exception as e:
                payload[name] = f"<{type(e).__name__}>"
        return formats, payload
    finally:
        cb.CloseClipboard()


def clip_text():
    cb.OpenClipboard()
    try:
        return cb.GetClipboardData(win32con.CF_UNICODETEXT)
    except Exception:
        return None
    finally:
        cb.CloseClipboard()


def clip_files():
    cb.OpenClipboard()
    try:
        return cb.GetClipboardData(win32con.CF_HDROP)
    except Exception:
        return None
    finally:
        cb.CloseClipboard()


def clip_dib():
    cb.OpenClipboard()
    try:
        return cb.GetClipboardData(win32con.CF_DIB)
    except Exception:
        return None
    finally:
        cb.CloseClipboard()


def clear():
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.CloseClipboard()


def set_text(t):
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_UNICODETEXT, t)
    cb.CloseClipboard()


def set_files(paths):
    files = "\0".join(paths) + "\0\0"
    header = struct.pack("<Iiiii", 20, 0, 0, 0, 1)
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_HDROP, header + files.encode("utf-16-le"))
    cb.CloseClipboard()


def set_html(marker):
    fmt = cb.RegisterClipboardFormat("HTML Format")
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(fmt, f"<html><body><b>{marker}</b></body></html>".encode("utf-8"))
    cb.CloseClipboard()


def set_rtf(marker):
    fmt = cb.RegisterClipboardFormat("Rich Text Format")
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(fmt, rb"{\rtf1\ansi " + marker.encode() + b"}")
    cb.CloseClipboard()


def set_image(bmp_path):
    raw = open(bmp_path, "rb").read()[14:]
    cb.OpenClipboard()
    cb.EmptyClipboard()
    cb.SetClipboardData(win32con.CF_DIB, raw)
    cb.CloseClipboard()
    return raw


def set_bitmap(bmp_path):
    """用 HBITMAP 走 CF_BITMAP 分支（验证 CopyImage 那条路）。"""
    from PIL import Image
    import io
    im = Image.open(bmp_path)
    # 用 GDI 创建 DDB 位图需要 CreateDIBSection 等，这里借 tkinter 的 PhotoImage 太绕，
    # 改为：先设 CF_DIB，Windows 不会自动生成 CF_BITMAP，所以这条分支单独用别的方式测。
    raise NotImplementedError


def run_snaptest():
    n = len(log_lines())
    subprocess.run([EXE, "--snaptest"], env=ENV, timeout=60)
    time.sleep(0.5)
    return [l for l in log_lines()[n:]]


def make_bmp(path):
    from PIL import Image, ImageDraw
    im = Image.new("RGB", (60, 40), (30, 120, 200))
    d = ImageDraw.Draw(im)
    d.rectangle([8, 8, 52, 32], outline=(255, 255, 0), width=3)
    im.save(path, "BMP")
    return path


def main():
    bmp = make_bmp(os.path.join(os.environ["TEMP"], "qp_test.bmp"))
    f = os.path.join(os.environ["TEMP"], "qp_clip_file.txt")
    open(f, "w").write("demo-content-123")

    cases = []
    cases.append(("纯文本", lambda: set_text("MARKER-PLAIN-888"), lambda: clip_text() == "MARKER-PLAIN-888"))
    cases.append(("文件(CF_HDROP)", lambda: set_files([f]), lambda: clip_files() == (f,)))
    cases.append(("图片(CF_DIB)", lambda: set_image(bmp), lambda: clip_dib() is not None and len(clip_dib()) > 1000))
    cases.append(("富文本(HTML)", lambda: set_html("MARKER-HTML-777"),
                  lambda: "MARKER-HTML-777" in read_registered("HTML Format")))
    cases.append(("RTF", lambda: set_rtf("MARKER-RTF-666"),
                  lambda: "MARKER-RTF-666" in read_registered("Rich Text Format")))
    cases.append(("文本+HTML 混合", lambda: (set_html("MARKER-MIX-555"), add_text("MARKER-MIX-TEXT")),
                  lambda: "MARKER-MIX-555" in read_registered("HTML Format")))

    ok_all = True
    for title, make, verify in cases:
        clear()
        make()
        before_formats, before_payload = snapshot_state()

        logs = run_snaptest()

        after_formats, after_payload = snapshot_state()
        ok = verify()
        same_formats = before_formats == after_formats
        ok_all = ok_all and ok and same_formats

        print(f"\n=== {title} ===")
        print(f"  前: {before_formats}")
        print(f"  后: {after_formats}")
        print(f"  格式一致={same_formats}  内容校验={ok}")
        for line in logs:
            print("   ", line[:170])

    print("\n总体:", "全部通过" if ok_all else "有失败项")
    return 0 if ok_all else 1


def read_registered(name):
    fmt = cb.RegisterClipboardFormat(name)
    cb.OpenClipboard()
    try:
        d = cb.GetClipboardData(fmt)
        return d.decode("utf-8", "ignore") if isinstance(d, bytes) else str(d)
    except Exception:
        return ""
    finally:
        cb.CloseClipboard()


def add_text(t):
    """在不清空的前提下追加文本格式（模拟 Word 复制：HTML + 纯文本并存）。"""
    cb.OpenClipboard()
    cb.SetClipboardData(win32con.CF_UNICODETEXT, t)
    cb.CloseClipboard()


if __name__ == "__main__":
    sys.exit(main())

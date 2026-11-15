using System.Runtime.InteropServices;
using System.Text;

namespace QuickPeek;

/// <summary>
/// 剪贴板全格式快照。
///
/// 为什么不用 <c>Clipboard.GetText()</c> 备份：那只能存文本。用户复制了一张截图、
/// 一个文件、一段富文本之后再划词，原来的做法是"清空剪贴板 → 发复制键 → 只写回文本"，
/// 非文本内容在清空那一步就没了，还原时因为备份是空串连写回都跳过。
///
/// 这里改用 Win32 直接枚举所有格式并逐个深拷贝：
///   · 绝大多数格式（文本、DIB、HDROP、HTML Format、RTF、PNG…）都是 HGLOBAL，
///     按 GlobalSize 整块复制即可；
///   · CF_BITMAP / CF_PALETTE / CF_METAFILEPICT / CF_ENHMETAFILE 是 GDI 句柄，
///     各自用 CopyImage / CreatePalette / CopyMetaFile / CopyEnhMetaFile 克隆；
///   · CF_OWNERDISPLAY、DSP*、GDIOBJ*、PRIVATE* 是合成/私有格式，跨进程本来就克隆不了，
///     跳过并记日志（这类格式只有原程序自己能渲染，跳过不影响还原效果）。
/// </summary>
internal sealed class ClipboardSnapshot : IDisposable
{
    private sealed class Item
    {
        public uint Format;
        public IntPtr Handle;
        public string Name = "";
        /// <summary>句柄还归我们时怎么释放（SetClipboardData 成功后系统接管，就不再调）。</summary>
        public Action<IntPtr>? Releaser;
    }

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint IMAGE_BITMAP = 0;

    private readonly List<Item> _items = new();
    private readonly List<string> _skipped = new();
    private bool _restored;

    public bool Captured { get; private set; }
    public int Count => _items.Count;
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>快照覆盖的格式清单，用于日志。</summary>
    public string Summary => _items.Count == 0
        ? "空"
        : string.Join("+", _items.Select(i => i.Name));

    // ---------------- 抓取 ----------------

    /// <summary>抓一份快照。剪贴板被占用时重试几次；彻底拿不到返回 null（调用方按旧逻辑继续）。</summary>
    public static ClipboardSnapshot? Capture(int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            var snap = CaptureOnce();
            if (snap != null) return snap;
            Thread.Sleep(20);
        }
        return null;
    }

    private static ClipboardSnapshot? CaptureOnce()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;

        var snap = new ClipboardSnapshot();
        try
        {
            uint fmt = 0;
            while (true)
            {
                fmt = EnumClipboardFormats(fmt);
                if (fmt == 0) break;

                string name = FormatName(fmt);

                // OLE 的私有格式里存的是进程内对象引用，跨进程写回去只会让别的程序
                // 拿到一堆野指针，比不还原更糟。各具体格式（文本/图片/HTML/RTF）已经单独存了。
                if (name is "DataObject" or "Ole Private Data")
                {
                    snap._skipped.Add(name);
                    continue;
                }

                // 合成格式和私有格式：数据由原窗口自己渲染，GetClipboardData 拿到的
                // 只是个句柄，没有可复制的字节。
                if (IsSynthetic(fmt))
                {
                    snap._skipped.Add(name);
                    continue;
                }

                IntPtr src = GetClipboardData(fmt);
                if (src == IntPtr.Zero)
                {
                    snap._skipped.Add(name + "(读取失败)");
                    continue;
                }

                var item = CloneFormat(fmt, name, src);
                if (item == null)
                {
                    snap._skipped.Add(name + "(克隆失败)");
                    continue;
                }
                snap._items.Add(item);
            }

            snap.Captured = true;
            return snap;
        }
        catch (Exception ex)
        {
            Log.Write($"⚠️ 剪贴板快照异常: {ex.GetType().Name} {ex.Message}");
            snap.Dispose();
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static Item? CloneFormat(uint fmt, string name, IntPtr src)
    {
        switch (fmt)
        {
            case 2: // CF_BITMAP：DDB 位图句柄
            {
                IntPtr copy = CopyImage(src, IMAGE_BITMAP, 0, 0, 0);
                if (copy == IntPtr.Zero) return null;
                return new Item { Format = fmt, Name = name, Handle = copy, Releaser = DeleteObjectVoid };
            }

            case 9: // CF_PALETTE：调色板句柄，要先读出 LOGPALETTE 再重建
            {
                int size = GetObject(src, 0, IntPtr.Zero);
                if (size <= 0) return null;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetObject(src, size, buf) <= 0) return null;
                    IntPtr pal = CreatePalette(buf);
                    if (pal == IntPtr.Zero) return null;
                    return new Item { Format = fmt, Name = name, Handle = pal, Releaser = DeleteObjectVoid };
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            case 3: // CF_METAFILEPICT：结构体里还挂着 HMETAFILE
            {
                IntPtr p = GlobalLock(src);
                if (p == IntPtr.Zero) return null;
                METAFILEPICT mp;
                try { mp = Marshal.PtrToStructure<METAFILEPICT>(p); }
                finally { GlobalUnlock(src); }

                IntPtr hmf = mp.hMF == IntPtr.Zero ? IntPtr.Zero : CopyMetaFile(mp.hMF, null);
                if (mp.hMF != IntPtr.Zero && hmf == IntPtr.Zero) return null;

                IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Marshal.SizeOf<METAFILEPICT>());
                if (mem == IntPtr.Zero)
                {
                    if (hmf != IntPtr.Zero) DeleteMetaFile(hmf);
                    return null;
                }
                IntPtr pNew = GlobalLock(mem);
                if (pNew == IntPtr.Zero)
                {
                    GlobalFree(mem);
                    if (hmf != IntPtr.Zero) DeleteMetaFile(hmf);
                    return null;
                }
                try
                {
                    Marshal.StructureToPtr(new METAFILEPICT { mm = mp.mm, xExt = mp.xExt, yExt = mp.yExt, hMF = hmf }, pNew, false);
                }
                finally { GlobalUnlock(mem); }

                return new Item
                {
                    Format = fmt, Name = name, Handle = mem,
                    Releaser = h =>
                    {
                        // 结构体里那个 HMETAFILE 也要释放，否则每次划词泄一个元文件
                        IntPtr q = GlobalLock(h);
                        if (q != IntPtr.Zero)
                        {
                            var m = Marshal.PtrToStructure<METAFILEPICT>(q);
                            GlobalUnlock(h);
                            if (m.hMF != IntPtr.Zero) DeleteMetaFile(m.hMF);
                        }
                        GlobalFree(h);
                    }
                };
            }

            case 14: // CF_ENHMETAFILE
            {
                IntPtr copy = CopyEnhMetaFile(src, null);
                if (copy == IntPtr.Zero) return null;
                return new Item { Format = fmt, Name = name, Handle = copy, Releaser = DeleteEnhMetaFileVoid };
            }

            default:
            {
                // 其余全部是 HGLOBAL（CF_TEXT/OEMTEXT/UNICODETEXT/DIB/DIBV5/HDROP/LOCALE/
                // TIFF/RIFF/WAVE，以及 "HTML Format" / "Rich Text Format" / "PNG" 等注册格式）
                IntPtr copy = CloneGlobalMemory(src);
                if (copy == IntPtr.Zero) return null;
                return new Item { Format = fmt, Name = name, Handle = copy, Releaser = GlobalFreeVoid };
            }
        }
    }

    private static IntPtr CloneGlobalMemory(IntPtr src)
    {
        UIntPtr size = GlobalSize(src);
        if (size == UIntPtr.Zero) return IntPtr.Zero;

        IntPtr pSrc = GlobalLock(src);
        if (pSrc == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            long n = (long)size;
            IntPtr dst = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)n);
            if (dst == IntPtr.Zero) return IntPtr.Zero;

            IntPtr pDst = GlobalLock(dst);
            if (pDst == IntPtr.Zero)
            {
                GlobalFree(dst);
                return IntPtr.Zero;
            }
            try
            {
                RtlMoveMemory(pDst, pSrc, (UIntPtr)n);
            }
            finally
            {
                GlobalUnlock(dst);
            }
            return dst;
        }
        finally
        {
            GlobalUnlock(src);
        }
    }

    // ---------------- 还原 ----------------

    /// <summary>把快照写回剪贴板。调用前剪贴板里一般是我们自己复制的选区文本。</summary>
    public bool Restore(int attempts = 10)
    {
        if (!Captured || _restored) return false;

        for (int i = 0; i < attempts; i++)
        {
            if (RestoreOnce())
            {
                _restored = true;
                return true;
            }
            Thread.Sleep(30);
        }
        Log.Write("⚠️ 还原剪贴板失败（多次重试），原内容可能已丢失");
        return false;
    }

    private bool RestoreOnce()
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            if (!EmptyClipboard()) return false;

            // 空快照：本来就是空的，清空就算还原完成
            if (_items.Count == 0) return true;

            bool allOk = true;
            foreach (var item in _items)
            {
                if (item.Handle == IntPtr.Zero) continue;

                IntPtr r = SetClipboardData(item.Format, item.Handle);
                if (r == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Write($"⚠️ 还原剪贴板格式 {item.Name} 失败 (err={err})");
                    item.Releaser?.Invoke(item.Handle);   // 没被接管，得自己释放
                    item.Handle = IntPtr.Zero;
                    allOk = false;
                }
                else
                {
                    // 系统接管了这个句柄，之后由系统释放，我们再碰就是野指针
                    item.Handle = IntPtr.Zero;
                }
            }
            return allOk;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        foreach (var item in _items)
        {
            if (item.Handle == IntPtr.Zero) continue;
            item.Releaser?.Invoke(item.Handle);
            item.Handle = IntPtr.Zero;
        }
    }

    // ---------------- 辅助 ----------------

    private static bool IsSynthetic(uint fmt) =>
        fmt == 0x0080                       // CF_OWNERDISPLAY
        || (fmt >= 0x0081 && fmt <= 0x0084) // CF_DSPTEXT/DSPBITMAP/DSPMETAFILEPICT/DSPENHMETAFILE
        || (fmt >= 0x0200 && fmt <= 0x03FF);// CF_GDIOBJFIRST..LAST / CF_PRIVATEFIRST..LAST

    private static string FormatName(uint fmt)
    {
        switch (fmt)
        {
            case 1: return "CF_TEXT";
            case 2: return "CF_BITMAP";
            case 3: return "CF_METAFILEPICT";
            case 4: return "CF_SYLK";
            case 5: return "CF_DIF";
            case 6: return "CF_TIFF";
            case 7: return "CF_OEMTEXT";
            case 8: return "CF_DIB";
            case 9: return "CF_PALETTE";
            case 10: return "CF_PENDATA";
            case 11: return "CF_RIFF";
            case 12: return "CF_WAVE";
            case 13: return "CF_UNICODETEXT";
            case 14: return "CF_ENHMETAFILE";
            case 15: return "CF_HDROP";
            case 16: return "CF_LOCALE";
            case 17: return "CF_DIBV5";
        }

        var sb = new StringBuilder(256);
        int n = GetClipboardFormatNameW(fmt, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : $"fmt#{fmt}";
    }

    // Dispose 里的 Releaser 是 Action<IntPtr>，这几个包装只为对齐签名
    private static void DeleteObjectVoid(IntPtr h) => DeleteObject(h);
    private static void DeleteEnhMetaFileVoid(IntPtr h) => DeleteEnhMetaFile(h);
    private static void GlobalFreeVoid(IntPtr h) => GlobalFree(h);

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct METAFILEPICT
    {
        public int mm;
        public int xExt;
        public int yExt;
        public IntPtr hMF;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClipboardFormatNameW(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyImage(IntPtr hImage, uint uType, int cxDesired, int cyDesired, uint fuFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void RtlMoveMemory(IntPtr dest, IntPtr src, UIntPtr count);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CopyEnhMetaFile(IntPtr hemfSrc, string? lpszFile);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteEnhMetaFile(IntPtr hemf);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CopyMetaFile(IntPtr hWmf, string? lpszFile);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteMetaFile(IntPtr hwmf);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr h, int c, IntPtr pv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreatePalette(IntPtr lplgpl);
}

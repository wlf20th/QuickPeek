using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickPeek;

/// <summary>
/// API Key 落盘加密。用 Windows DPAPI（CryptProtectData，默认 CurrentUser 作用域）：
/// 密文绑定**当前 Windows 用户**，拷到别的机器或别的账户上就是一堆解不开的字节。
/// 这样 settings.json 可以放心放桌面、丢进 git，也不会被别的程序顺手读走。
///
/// 为什么不引 NuGet：ProtectedData 需要额外的包，而这里只要两个 crypt32 导出函数，
/// 几十行 P/Invoke 就够了，也不影响单文件发布体积。
/// </summary>
internal static class SecretStore
{
    private const string Prefix = "dpapi:v1:";

    /// <summary>不弹任何 UI（否则在服务/无桌面场景会卡住）。</summary>
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>这个字符串看起来是密文吗（用于识别"还没迁移的旧明文"）。</summary>
    public static bool IsEncrypted(string? s) =>
        s != null && s.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>明文 → "dpapi:v1:base64"。空串原样返回空串。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";

        var bytes = Encoding.UTF8.GetBytes(plain);
        var inBlob = NewBlob(bytes);
        try
        {
            if (!CryptProtectData(ref inBlob, "QuickPeek API Key", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var enc = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, enc, 0, enc.Length);
                return Prefix + Convert.ToBase64String(enc);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    /// <summary>
    /// 密文 → 明文。返回 false 表示"确实是密文但解不开"（换用户/换机器/blob 损坏）。
    /// 传进来的是旧明文时返回 true 并原样带出，方便调用方判断需不需要迁移。
    /// </summary>
    public static bool TryUnprotect(string? stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored)) return true;

        if (!IsEncrypted(stored))
        {
            plain = stored;      // 旧格式：明文
            return true;
        }

        byte[] enc;
        try
        {
            enc = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var inBlob = NewBlob(enc);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var outBlob))
            {
                Log.Write($"⚠️ 解密 API Key 失败 (Win32 {Marshal.GetLastWin32Error()})");
                return false;
            }

            try
            {
                var dec = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, dec, 0, dec.Length);
                plain = Encoding.UTF8.GetString(dec);
                return true;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    private static DataBlob NewBlob(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }
}

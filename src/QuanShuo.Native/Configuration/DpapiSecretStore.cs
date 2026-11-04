using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using QuanShuo.Core.Storage;

namespace QuanShuo.Native.Configuration;

public sealed class DpapiSecretStore
{
    private const uint CryptProtectUiForbidden = 0x01;
    private static readonly byte[] Entropy = "QuanShuo.DashScope.ApiKey.v1"u8.ToArray();
    private readonly string path;

    public DpapiSecretStore(string? secretPath = null)
    {
        if (secretPath is not null)
        {
            path = Path.GetFullPath(secretPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return;
        }

        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuanShuo");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "dashscope-key.dpapi");
    }

    public string LoadApiKey()
    {
        if (!File.Exists(path)) return string.Empty;
        byte[] protectedBytes = File.ReadAllBytes(path);
        byte[] clearBytes = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public async Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        byte[] clearBytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            byte[] protectedBytes = Protect(clearBytes);
            await AtomicFile.WriteAllBytesAsync(path, protectedBytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private static byte[] Protect(byte[] clearBytes) => Transform(clearBytes, true);

    private static byte[] Unprotect(byte[] protectedBytes) => Transform(protectedBytes, false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        IntPtr inputBuffer = Alloc(input);
        IntPtr entropyBuffer = Alloc(Entropy);
        try
        {
            var inputBlob = new DataBlob { Size = input.Length, Data = inputBuffer };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyBuffer };
            bool success = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out DataBlob output)
                : CryptUnprotectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!success) throw new InvalidOperationException("Windows 无法保护或读取 API Key。请确认当前 Windows 用户凭据可用。");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuffer);
            Marshal.FreeHGlobal(entropyBuffer);
        }
    }

    private static IntPtr Alloc(byte[] bytes)
    {
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

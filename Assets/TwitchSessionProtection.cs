using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class TwitchSessionProtection
{
    private const int ServicesWithoutPrompt = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int size;
        public IntPtr data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));

    public static string Unprotect(string value)
    {
        byte[] plaintext = Transform(Convert.FromBase64String(value), false);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { Array.Clear(plaintext, 0, plaintext.Length); }
    }

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { size = data.Length, data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.data, data.Length);
            bool success = encrypt
                ? CryptProtectData(ref input, "TwitchFishes sign-in", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ServicesWithoutPrompt, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ServicesWithoutPrompt, out output);
            if (!success)
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[output.size];
            Marshal.Copy(output.data, result, 0, output.size);
            return result;
        }
        finally
        {
            Array.Clear(data, 0, data.Length);
            Marshal.Copy(data, 0, input.data, data.Length);
            Marshal.FreeHGlobal(input.data);
            if (output.data != IntPtr.Zero)
                LocalFree(output.data);
        }
    }
}
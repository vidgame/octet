using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Octet;

internal static class Explorer
{
    private const uint CoInitMultithreaded = 0;

    public static Task RevealAsync(string path) => Task.Run(() =>
    {
        path = Path.GetFullPath(path);
        File.GetAttributes(path);
        if (string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return;
        }

        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, CoInitMultithreaded));
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var item, 0, IntPtr.Zero));
            try
            {
                // A zero child count selects the absolute item in its parent directory.
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(item);
            }
        }
        finally
        {
            CoUninitialize();
        }
    });

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, IntPtr outputAttributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}

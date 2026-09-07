using System.Runtime.InteropServices;

namespace AkiSpace.Native;

/// <summary>
/// P/Invoke wrappers for kernel32.dll and miscellaneous system interop.
/// </summary>
internal static class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateNamedPipe(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
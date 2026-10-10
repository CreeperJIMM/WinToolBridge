using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinToolBridge.Services;

internal static class DeviceIoControlQuery
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint returned, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedRequest
    {
        public IntPtr internalStatus, internalHigh;
        public uint offset, offsetHigh;
        public IntPtr completionEvent;
    }

    internal static SafeFileHandle Open(string path, uint access = 0) =>
        CreateFile(path, access, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);

    internal static bool Query(SafeFileHandle handle, uint code, byte[] data, out int returned, out int error, int? inputLength = null)
    {
        var buffer = Marshal.AllocHGlobal(data.Length);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedRequest>());
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var pending = false;
        try {
            Marshal.Copy(data, 0, buffer, data.Length);
            Marshal.StructureToPtr(new OverlappedRequest { completionEvent = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            var ok = DeviceIoControl(handle, code, inputLength == 0 ? IntPtr.Zero : buffer, (uint)(inputLength ?? data.Length),
                data.Length == 0 ? IntPtr.Zero : buffer, (uint)data.Length, out var count, overlapped);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            pending = !ok && error == 997; // ERROR_IO_PENDING
            if (pending && !completed.WaitOne(TimeSpan.FromSeconds(3))) {
                CancelIoEx(handle, overlapped);
                // Cancellation is cooperative. Wait for completion before freeing native buffers
                // or closing the handle; a client deadline alone does not release an active IRP.
                GetOverlappedResult(handle, overlapped, out _, true);
                pending = false;
                returned = 0;
                error = 1460; // ERROR_TIMEOUT
                return false;
            }
            if (ok || pending) {
                ok = GetOverlappedResult(handle, overlapped, out count, true);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                pending = false;
            }
            Marshal.Copy(buffer, data, 0, data.Length);
            if (count > data.Length) throw new InvalidDataException("Device driver returned an invalid buffer length.");
            returned = (int)count;
            return ok;
        }
        finally {
            if (pending) {
                CancelIoEx(handle, overlapped);
                GetOverlappedResult(handle, overlapped, out _, true);
            }
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(buffer);
        }
    }

}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XISOSharp;

/// <summary>
/// Sparse-file helpers for large pre-sized outputs (LIB-03). On Windows NTFS a
/// file must be marked sparse before <see cref="FileStream.SetLength(long)"/>,
/// otherwise the pre-sized range is physically allocated. Unix file systems
/// create holes when a file is extended and written sparsely, so marking is a
/// no-op there. Every operation is best-effort: unsupported file systems keep
/// the historical dense behavior instead of failing the write.
/// </summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;
    private const int FileStandardInfoClass = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileStandardInfo fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    /// <summary>
    /// Marks <paramref name="stream"/> sparse when the platform supports it.
    /// Returns <c>true</c> when subsequent seeks over zero ranges can leave
    /// holes (always true on Unix; true on Windows only when FSCTL_SET_SPARSE
    /// succeeded).
    /// </summary>
    internal static bool TryEnable(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!OperatingSystem.IsWindows())
        {
            // Unix file systems represent a seek over unwritten ranges as a hole.
            return true;
        }

        try
        {
            return DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug($"Sparse marking failed for '{stream.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Marks the file sparse (best effort) and pre-sizes it to
    /// <paramref name="length"/> bytes. The length call remains authoritative:
    /// a store that rejects pre-sizing still surfaces its natural exception.
    /// </summary>
    internal static void Prepare(FileStream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        TryEnable(stream);
        stream.SetLength(length);
    }

    /// <summary>
    /// Writes <paramref name="data"/>, or seeks over it when it is entirely
    /// zeros and <paramref name="sparse"/> is set. The final file length is
    /// established separately (<see cref="Prepare"/> or a final SetLength), so
    /// the skipped range reads back as zeros.
    /// </summary>
    internal static void WriteOrSeekZeros(Stream stream, ReadOnlySpan<byte> data, bool sparse)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (data.Length == 0)
        {
            return;
        }

        if (sparse && IsAllZero(data))
        {
            stream.Seek(data.Length, SeekOrigin.Current);
            return;
        }

        stream.Write(data);
    }

    /// <summary>
    /// Returns the bytes physically allocated to <paramref name="stream"/> on
    /// Windows, or <c>-1</c> when the platform cannot report it.
    /// </summary>
    internal static long GetAllocatedSize(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!OperatingSystem.IsWindows())
        {
            return -1;
        }

        try
        {
            return GetFileInformationByHandleEx(stream.SafeFileHandle, FileStandardInfoClass,
                out FileStandardInfo info, (uint)Marshal.SizeOf<FileStandardInfo>())
                ? info.AllocationSize
                : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}

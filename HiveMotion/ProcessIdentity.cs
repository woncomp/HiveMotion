using System;
using System.Text;

namespace HiveMotion;

/// <summary>
/// Captures the launch identity of another process: image path, command line arguments
/// and working directory. Image path goes through QueryFullProcessImageName; the command
/// line and current directory are read from the target's PEB (no WMI dependency).
/// Reads can fail for elevated/protected processes — callers must handle nulls.
/// </summary>
internal static class ProcessIdentity
{
    // Field offsets in PEB / RTL_USER_PROCESS_PARAMETERS.
    private const int PebProcessParametersOffset64 = 0x20;
    private const int PebProcessParametersOffset86 = 0x10;
    private const int ParamsCurrentDirectoryOffset64 = 0x38;
    private const int ParamsCurrentDirectoryOffset86 = 0x24;
    private const int ParamsCommandLineOffset64 = 0x70;
    private const int ParamsCommandLineOffset86 = 0x40;

    /// <summary>Full Win32 path of the process image, or null when the process denies query.</summary>
    public static string? TryGetImagePath(uint processId)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads the raw command line and current directory from the target PEB.
    /// Returns false when the process could not be opened or its memory read.
    /// </summary>
    public static bool TryReadProcessParameters(uint processId, out string? commandLine, out string? currentDirectory)
    {
        commandLine = null;
        currentDirectory = null;

        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, processId);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            var info = new NativeMethods.PROCESS_BASIC_INFORMATION();
            if (NativeMethods.NtQueryInformationProcess(handle, 0, ref info,
                    System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return false;

            bool wow64 = NativeMethods.IsWow64Process(handle, out bool isWow64) && isWow64;
            int pointerSize = wow64 ? 4 : IntPtr.Size;
            int paramsOffset = wow64 ? PebProcessParametersOffset86 : PebProcessParametersOffset64;
            int curDirOffset = wow64 ? ParamsCurrentDirectoryOffset86 : ParamsCurrentDirectoryOffset64;
            int cmdLineOffset = wow64 ? ParamsCommandLineOffset86 : ParamsCommandLineOffset64;

            if (!ReadPointer(handle, Add(info.PebBaseAddress, paramsOffset), pointerSize, out IntPtr parameters))
                return false;

            commandLine = ReadUnicodeString(handle, Add(parameters, cmdLineOffset), pointerSize);
            currentDirectory = ReadUnicodeString(handle, Add(parameters, curDirOffset), pointerSize);
            return commandLine != null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Strips argv[0] from a raw command line, keeping the remaining arguments with their
    /// original quoting. Both pin capture and window matching use this, so comparisons
    /// stay symmetric.
    /// </summary>
    public static string ExtractArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return string.Empty;

        string cl = commandLine.TrimStart();
        int end;
        if (cl[0] == '"')
        {
            int closing = cl.IndexOf('"', 1);
            end = closing < 0 ? cl.Length : closing + 1;
        }
        else
        {
            end = 0;
            while (end < cl.Length && !char.IsWhiteSpace(cl[end]))
                end++;
        }
        return cl.Substring(end).Trim();
    }

    private static IntPtr Add(IntPtr baseAddress, int offset) =>
        IntPtr.Size == 8
            ? new IntPtr(baseAddress.ToInt64() + offset)
            : new IntPtr(baseAddress.ToInt32() + offset);

    private static bool ReadPointer(IntPtr process, IntPtr address, int pointerSize, out IntPtr value)
    {
        value = IntPtr.Zero;
        var buffer = new byte[pointerSize];
        if (!NativeMethods.ReadProcessMemory(process, address, buffer, pointerSize, out int read) || read != pointerSize)
            return false;
        value = pointerSize == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));
        return true;
    }

    /// <summary>UNICODE_STRING: USHORT Length; USHORT MaximumLength; pad; PWSTR Buffer.</summary>
    private static string? ReadUnicodeString(IntPtr process, IntPtr address, int pointerSize)
    {
        int headerSize = pointerSize * 2; // 2 shorts padded to pointer alignment + pointer
        var header = new byte[headerSize];
        if (!NativeMethods.ReadProcessMemory(process, address, header, headerSize, out int read) || read != headerSize)
            return null;

        ushort length = BitConverter.ToUInt16(header, 0);
        if (length == 0)
            return string.Empty;

        IntPtr bufferAddress = pointerSize == 8
            ? new IntPtr(BitConverter.ToInt64(header, pointerSize))
            : new IntPtr(BitConverter.ToInt32(header, pointerSize));

        var bytes = new byte[length];
        if (!NativeMethods.ReadProcessMemory(process, bufferAddress, bytes, length, out read) || read != length)
            return null;

        return Encoding.Unicode.GetString(bytes);
    }
}

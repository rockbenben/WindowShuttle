using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WindowShuttle.Core.Native;

/// <summary>Identity reads never create a token: an old HWND must not acquire a new identity during undo.</summary>
public static partial class WindowIdentityProbe
{
    internal const string PropertyName = "WindowShuttle.UndoIdentity.v1";

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetPropW(nint hwnd, string name);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPropW(nint hwnd, string name, nint value);
    [LibraryImport("kernel32.dll")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);

    public static WindowIdentity? Capture(nint hwnd)
    {
        var owner = ReadOwner(hwnd);
        if (owner is null) return null;
        nint token = GetPropW(hwnd, PropertyName);
        if (token == 0)
        {
            // Scalar data, not a pointer or allocated handle. The window owns the property lifetime.
            long random = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue;
            if (nint.Size == 4) random &= int.MaxValue;
            token = (nint)Math.Max(1, random);
            if (!SetPropW(hwnd, PropertyName, token)) return null;
        }
        var identity = new WindowIdentity(owner.Value.Pid, owner.Value.Created, (long)token);
        return Inspect(hwnd, identity) == WindowIdentityStatus.Match ? identity : null;
    }

    public static WindowIdentityStatus Inspect(nint hwnd, WindowIdentity? identity)
    {
        if (identity is not { IsComplete: true }) return WindowIdentityStatus.Unavailable;
        uint thread = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (thread == 0 || pid != identity.ProcessId) return WindowIdentityStatus.Different;
        var owner = ReadOwner(hwnd);
        if (owner is null) return WindowIdentityStatus.Unavailable;
        if (owner.Value.Pid != identity.ProcessId || owner.Value.Created != identity.ProcessCreated)
            return WindowIdentityStatus.Different;
        nint token = GetPropW(hwnd, PropertyName);
        // GetProp does not distinguish a missing property from an inaccessible one.
        if (token == 0) return WindowIdentityStatus.Unavailable;
        if ((long)token != identity.WindowToken) return WindowIdentityStatus.Different;
        // Recheck ownership after the property read as the target can exit while being inspected.
        return Win32.GetWindowThreadProcessId(hwnd, out pid) != 0 && pid == identity.ProcessId
            ? WindowIdentityStatus.Match : WindowIdentityStatus.Different;
    }

    private static (uint Pid, long Created)? ReadOwner(nint hwnd)
    {
        if (Win32.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid || !GetProcessTimes(process, out long created, out _, out _, out _)) return null;
        return (pid, created);
    }
}

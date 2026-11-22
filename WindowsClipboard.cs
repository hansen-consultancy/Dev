// Win32 clipboard for `dev b64 -c`. Text is tagged so Windows clipboard history
// (Win+V), cloud clipboard and clipboard monitors skip it, then a detached copy of
// this tool (`dev b64 --clear-clipboard <sequence> <seconds>`) clears it later.
// The clearer compares the clipboard sequence number first, so it never wipes
// something the user copied after us, and it never sees the value itself.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dev;

[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboard : ITransientClipboard
{
    internal const string ClearFlag = "--clear-clipboard";

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public void Copy(string text, TimeSpan lifetime)
    {
        WithOpenClipboard(() =>
        {
            if (!EmptyClipboard()) throw new InvalidOperationException("Could not empty the clipboard.");
            SetData(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            // Data of the first is ignored; the two DWORD flags must be 0.
            SetData(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), [0]);
            SetData(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), [0, 0, 0, 0]);
            SetData(RegisterClipboardFormatW("CanUploadToCloudClipboard"), [0, 0, 0, 0]);
        });
        var sequence = GetClipboardSequenceNumber();

        try
        {
            StartClearer(sequence, lifetime);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            WithOpenClipboard(() => EmptyClipboard());
            throw new InvalidOperationException("Could not schedule clearing the clipboard, so it was cleared now.", ex);
        }
    }

    // Entry point of the detached clearer. Returns false when args are not a clear request.
    public static bool TryRunScheduledClear(string[] args)
    {
        if (args is not [_, ClearFlag, var sequenceArg, var secondsArg]) return false;
        if (uint.TryParse(sequenceArg, CultureInfo.InvariantCulture, out var sequence)
            && int.TryParse(secondsArg, CultureInfo.InvariantCulture, out var seconds))
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            if (GetClipboardSequenceNumber() == sequence)
            {
                try { WithOpenClipboard(() => EmptyClipboard()); }
                catch (InvalidOperationException) { /* best effort: nobody to report to */ }
            }
        }
        return true;
    }

    private static void StartClearer(uint sequence, TimeSpan lifetime)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        // Under `dotnet Dev.dll` the process is the dotnet host; pass the app dll.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add("b64");
        psi.ArgumentList.Add(ClearFlag);
        psi.ArgumentList.Add(sequence.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(((int)lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        using var _ = Process.Start(psi);
    }

    private static void WithOpenClipboard(Action action)
    {
        // Another app may hold the clipboard briefly; retry for ~1 s.
        for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt >= 20)
                throw new InvalidOperationException($"Could not open the clipboard (Win32 error {Marshal.GetLastWin32Error()}); another application may be holding it.");
            Thread.Sleep(50);
        }
        try { action(); }
        finally { CloseClipboard(); }
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not allocate clipboard memory.");
        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("Could not lock clipboard memory.");
        }
        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(handle);
        // On success the system owns the memory; on failure it is still ours to free.
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new InvalidOperationException("Could not write to the clipboard.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

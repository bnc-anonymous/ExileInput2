using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExileInput2;

/// <summary>
/// Notices the player's own key presses, for a caller that asked to be stopped by them. See ExileInput2.StopOnPlayerKeys.
///
/// A low-level keyboard hook on a thread of its own, installed while watching and removed when not. It only watches:
/// every key goes on to the game as before. A key-down without the injected flag is the player's; keys this plugin
/// sends carry the flag and are not counted. Each is queued by virtual-key code for the next tick to read.
/// </summary>
internal static class KeyWatch
{
    private static volatile bool _watching;
    private static Thread _thread;
    private static readonly object Gate = new();

    /// <summary>Kept in a field so the delegate the hook calls is not collected while installed.</summary>
    private static readonly LowLevelKeyboardProc Proc = Hook;

    /// <summary>The player's key-downs since the last Drain, by virtual-key code.</summary>
    private static readonly ConcurrentQueue<int> Pressed = new();

    /// <summary>Starts watching, if not already.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            _watching = true;

            if (_thread is { IsAlive: true })
                return;

            _thread = new Thread(Run) { IsBackground = true, Name = "ExileInput2 key watch" };
            _thread.Start();
        }
    }

    /// <summary>Stops watching; the hook comes off within a few milliseconds.</summary>
    public static void Stop() => _watching = false;

    /// <summary>Takes every key-down queued since the last call.</summary>
    public static int[] Drain()
    {
        var keys = new System.Collections.Generic.List<int>();

        while (Pressed.TryDequeue(out var key))
            keys.Add(key);

        return keys.ToArray();
    }

    private static void Run()
    {
        var hook = SetWindowsHookEx(WhKeyboardLl, Proc, GetModuleHandle(null), 0);

        if (hook == IntPtr.Zero)
            return;

        try
        {
            while (true)
            {
                // The hook is called from within this thread's message wait, so the thread must keep pumping.
                MsgWaitForMultipleObjects(0, null, false, 20, QsAllInput);

                while (PeekMessage(out _, IntPtr.Zero, 0, 0, PmRemove))
                {
                }

                lock (Gate)
                {
                    if (!_watching)
                    {
                        _thread = null;
                        return;
                    }
                }
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    private static IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _watching && (int)message is WmKeyDown or WmSysKeyDown)
        {
            var info = Marshal.PtrToStructure<KeyHookInfo>(data);

            if ((info.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
                Pressed.Enqueue((int)info.VirtualKey);
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private const int WhKeyboardLl = 13;
    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 0x0001;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfLowerIlInjected = 0x02;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyHookInfo
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms, uint wake);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}

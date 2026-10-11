using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExileInput2;

/// <summary>
/// Drops the player's own mouse input for a bounded time, so a caller's move and click land where they were sent.
///
/// A low-level mouse hook on a thread of its own, installed while a block is wanted and removed when it ends. Physical
/// movement, button presses and the wheel are dropped; button releases are let through, so a button the player was
/// holding when the block began is not left down. Input this plugin sends is let through: clicks carry the injected
/// flag, and a cursor move is recognised by its position, since SetCursorPos may reach the hook without that flag.
///
/// The deadline is checked by the hook itself, so the block ends on time however the caller fares, and the hook goes
/// with the process. Never longer than MostMs.
/// </summary>
internal static class MouseBlock
{
    /// <summary>The longest one block may last.</summary>
    public const int MostMs = 1000;

    /// <summary>Environment.TickCount64 at which the block ends; 0 when there is none.</summary>
    private static long _untilTicks;

    /// <summary>The screen point of this plugin's last cursor move, packed as x in the high half and y in the low.</summary>
    private static long _ownMove = long.MinValue;

    private static Thread _thread;

    private static readonly object Gate = new();

    /// <summary>Kept in a field so the delegate the hook calls is not collected while installed.</summary>
    private static readonly LowLevelMouseProc Proc = Hook;

    /// <summary>Whether the player's mouse is being dropped now.</summary>
    public static bool Blocking => Environment.TickCount64 < Interlocked.Read(ref _untilTicks);

    /// <summary>Drops the player's mouse for this long from now, or extends a block already going.</summary>
    public static void Block(int ms)
    {
        ms = Math.Clamp(ms, 0, MostMs);

        lock (Gate)
        {
            Interlocked.Exchange(ref _untilTicks, Environment.TickCount64 + ms);

            if (ms == 0 || _thread is { IsAlive: true })
                return;

            _thread = new Thread(Run) { IsBackground = true, Name = "ExileInput2 mouse block" };
            _thread.Start();
        }
    }

    /// <summary>Ends any block now.</summary>
    public static void Stop() => Interlocked.Exchange(ref _untilTicks, 0);

    /// <summary>Marks a screen point as this plugin's own cursor move, so the hook lets it through. See Run.</summary>
    public static void OwnMoveTo(int x, int y) => Interlocked.Exchange(ref _ownMove, ((long)x << 32) | (uint)y);

    private static void Run()
    {
        var hook = SetWindowsHookEx(WhMouseLl, Proc, GetModuleHandle(null), 0);

        if (hook == IntPtr.Zero)
            return;

        try
        {
            while (true)
            {
                // The hook is called from within this thread's message wait, so the thread must keep pumping.
                MsgWaitForMultipleObjects(0, null, false, 10, QsAllInput);

                while (PeekMessage(out _, IntPtr.Zero, 0, 0, PmRemove))
                {
                }

                lock (Gate)
                {
                    if (!Blocking)
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
        if (code >= 0 && Blocking && Dropped((int)message, data))
            return 1;

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    /// <summary>Whether a mouse event is the player's movement, press or wheel, as opposed to a release or this plugin's.</summary>
    private static bool Dropped(int message, IntPtr data)
    {
        var info = Marshal.PtrToStructure<MouseHookInfo>(data);

        if ((info.Flags & (LlmhfInjected | LlmhfLowerIlInjected)) != 0)
            return false;

        return message switch
        {
            WmMouseMove => (((long)info.X << 32) | (uint)info.Y) != Interlocked.Read(ref _ownMove),
            WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown or WmMouseWheel or WmMouseHWheel => true,
            _ => false,
        };
    }

    private const int WhMouseLl = 14;
    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 0x0001;
    private const uint LlmhfInjected = 0x01;
    private const uint LlmhfLowerIlInjected = 0x02;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmMouseHWheel = 0x020E;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public int X;
        public int Y;
        public uint MouseData;
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
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc proc, IntPtr module, uint thread);

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

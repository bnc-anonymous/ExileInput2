using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExileInput2;

/// <summary>
/// Notices the player's own key presses, for a caller that asked to be stopped by them, and drops them for a caller that
/// asked for them blocked. See ExileInput2.StopOnPlayerKeys and ExileInput2.BlockPlayerKeys.
///
/// A low-level keyboard hook on a thread of its own, installed while watching or blocking and removed when neither. A
/// key-down without the injected flag is the player's; keys this plugin sends carry the flag and pass. Watching queues
/// the player's key-downs by virtual-key code for the next tick to read; blocking drops them before the game sees them.
/// A key-up always passes, so no key is left down. The keys still held when a block ends are pressed again. See Restore.
/// </summary>
internal static class KeyWatch
{
    private static volatile bool _watching;
    private static Thread _thread;
    private static readonly object Gate = new();

    /// <summary>Kept in a field so the delegate the hook calls is not collected while installed.</summary>
    private static readonly LowLevelKeyboardProc Proc = Hook;

    /// <summary>
    /// The keys the player is holding through a block: those let go when it began, and those pressed during it, less
    /// any let go since - by virtual-key code. Pressed again when the block ends, so a walk on a held A goes on. Windows
    /// cannot be asked instead: after the let-go its key state reads up, finger on the key or not. See Restore.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> HeldThroughBlock = new();

    /// <summary>
    /// A key the block let go of while the player held it, for Restore to press again. See ExileInput2.BlockPlayerKeys.
    /// The undirected Shift, Ctrl and Alt (0x10-0x12) are left out: the hook reports the sided codes (0xA0-0xA5), so a
    /// physical release during the block would never take the undirected one off, and Restore would press it stuck.
    /// The sided code, held at the same time, stands for it.
    /// </summary>
    public static void NoteHeld(int key)
    {
        if (key is < 0x10 or > 0x12)
            HeldThroughBlock[key] = 0;
    }

    /// <summary>
    /// Presses again every key the player is still holding as a block ends, so the game sees it held once more; the
    /// player's own key-up lets it go as usual. Run once, by the hook's thread, when the block ends.
    /// </summary>
    private static void Restore()
    {
        foreach (var key in HeldThroughBlock.Keys)
        {
            try
            {
                ExileCore2.Input.KeyDown((System.Windows.Forms.Keys)key);
            }
            catch
            {
                // A key that would not go down again is no worse than the block it came from.
            }
        }

        HeldThroughBlock.Clear();
    }

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

    /// <summary>Stops watching; the hook comes off within a few milliseconds, unless a block is on.</summary>
    public static void Stop() => _watching = false;

    /// <summary>The longest one block of the player's keys may last.</summary>
    public const int MostBlockMs = 3000;

    /// <summary>Environment.TickCount64 at which the block of the player's keys ends; 0 when there is none.</summary>
    private static long _blockUntil;

    /// <summary>Whether the player's key-downs are being dropped now.</summary>
    public static bool Blocking => Environment.TickCount64 < Interlocked.Read(ref _blockUntil);

    /// <summary>Drops the player's key-downs for this long from now, replacing any block; 0 ends it.</summary>
    public static void Block(int ms)
    {
        ms = Math.Clamp(ms, 0, MostBlockMs);

        lock (Gate)
        {
            Interlocked.Exchange(ref _blockUntil, ms == 0 ? 0 : Environment.TickCount64 + ms);

            if (ms == 0 || _thread is { IsAlive: true })
                return;

            _thread = new Thread(Run) { IsBackground = true, Name = "ExileInput2 key watch" };
            _thread.Start();
        }
    }

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

        var wasBlocking = false;

        try
        {
            while (true)
            {
                // The hook is called from within this thread's message wait, so the thread must keep pumping.
                MsgWaitForMultipleObjects(0, null, false, 20, QsAllInput);

                while (PeekMessage(out _, IntPtr.Zero, 0, 0, PmRemove))
                {
                }

                // A block that has just ended, by its time or by Block(0): the keys still held pressed again.
                var blocking = Blocking;

                if (wasBlocking && !blocking)
                    Restore();

                wasBlocking = blocking;

                lock (Gate)
                {
                    if (!_watching && !Blocking)
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
        if (code >= 0 && (int)message is WmKeyDown or WmSysKeyDown)
        {
            var info = Marshal.PtrToStructure<KeyHookInfo>(data);

            if ((info.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
            {
                // Dropped, and remembered as held, for Restore.
                if (Blocking)
                {
                    HeldThroughBlock[(int)info.VirtualKey] = 0;
                    return 1;
                }

                if (_watching)
                    Pressed.Enqueue((int)info.VirtualKey);
            }
        }
        else if (code >= 0 && (int)message is WmKeyUp or WmSysKeyUp && Blocking)
        {
            // Let go during the block: not pressed again when it ends. The key-up itself passes.
            var info = Marshal.PtrToStructure<KeyHookInfo>(data);

            if ((info.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
                HeldThroughBlock.TryRemove((int)info.VirtualKey, out _);
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
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

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

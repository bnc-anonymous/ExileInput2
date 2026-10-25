using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;

namespace ExileInput2;

/// <summary>
/// Notices the player's own key presses, for a caller that asked to be stopped by them, and drops them for a caller that
/// asked for them blocked. See ExileInput2.StopOnPlayerKeys and ExileInput2.BlockPlayerKeys.
///
/// A low-level keyboard hook on a thread of its own, installed while watching or blocking and removed when neither. A
/// key-down without the injected flag is the player's; keys this plugin sends carry the flag and pass. Watching queues
/// the player's key-downs by virtual-key code for the next tick to read; blocking drops them before the game sees them.
/// A key-up always passes, so no key is left down. The keys held when a block began and still held when it ends are pressed again. See Restore.
/// </summary>
internal static class KeyWatch
{
    private static volatile bool _watching;
    private static Thread _thread;
    private static readonly object Gate = new();

    /// <summary>Kept in a field so the delegate the hook calls is not collected while installed.</summary>
    private static readonly LowLevelKeyboardProc Proc = Hook;

    /// <summary>
    /// The keys the player is holding through a block: those let go when it began, less any let go since - by
    /// virtual-key code. A key first pressed during the block is dropped and never added, so no combination the game
    /// never saw, such as Alt+F4, is sent at the end. Pressed again when the block ends, so a walk on a held A goes on. Windows
    /// cannot be asked instead: after the let-go its key state reads up, finger on the key or not. See Restore.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> HeldThroughBlock = new();

    /// <summary>
    /// A key the block let go of while the player held it, for Restore to press again. See ExileInput2.BlockPlayerKeys.
    /// Only the letters A-Z, the digits 0-9 and Space are kept. A modifier, function key or any other key is let go and
    /// stays up, so nothing pressed again can form a combination such as Alt+F4.
    /// </summary>
    public static void NoteHeld(int key)
    {
        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9' or 0x20)
            HeldThroughBlock[key] = 0;
    }

    /// <summary>
    /// What each key block did, line by line, timestamped: the keys let go at its start, the player's key-downs dropped
    /// and key-ups seen during it, and the keys pressed again at its end. ExileInput2.Tick appends them to
    /// key_blocks.log in the plugin's config directory. Kept to find why keys stayed held in the game after instant
    /// loot's blocks, S and Space and then W, with a fix for each suspected cause in place (2026-10-25).
    /// </summary>
    public static readonly ConcurrentQueue<string> Journal = new();

    /// <summary>Adds a line to Journal, stamped with the local time.</summary>
    public static void Note(string line)
    {
        if (Journal.Count < 2000)
            Journal.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
    }

    /// <summary>The player's keys already noted dropped in this block, so a held key's repeats are noted once.</summary>
    private static readonly ConcurrentDictionary<int, byte> DroppedThisBlock = new();

    /// <summary>A virtual-key code as a name, such as W or Space.</summary>
    public static string NameOfKey(int key) => ((System.Windows.Forms.Keys)key).ToString();

    /// <summary>
    /// Presses again every key the player is still holding as a block ends, so the game sees it held once more; the
    /// player's own key-up lets it go as usual. Run once, by the hook's thread, when the block ends.
    /// </summary>
    private static void Restore()
    {
        Note(HeldThroughBlock.IsEmpty
            ? "block over: nothing to press again"
            : $"block over: pressed again {string.Join(", ", HeldThroughBlock.Keys.Select(NameOfKey))}");
        DroppedThisBlock.Clear();

        foreach (var key in HeldThroughBlock.Keys)
        {
            try
            {
                // With the key's scan code, so the player's own key-up lets it go in the game. See PhysicalKeys.
                PhysicalKeys.Down(key);
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

    /// <summary>
    /// The virtual-key code whose press ends a block and the mouse block with it, 0 for none. The press itself is
    /// dropped, so it does not also act as that key. See Hook and BlockCancelled.
    /// </summary>
    private static volatile int _cancelKey;

    /// <summary>Set by the hook when the player's press of the cancel key ended a block. See TakeBlockCancelled.</summary>
    private static volatile bool _blockCancelled;

    /// <summary>
    /// Whether the cancel key is a mouse button - left, right, middle, X1 or X2 (virtual-key 1, 2, 4, 5, 6) - which this
    /// keyboard hook never sees; MouseBlock's hook watches for it instead, for as long as the key block lasts.
    /// </summary>
    public static bool CancelKeyIsMouseButton => _cancelKey is 1 or 2 or 4 or 5 or 6;

    /// <summary>Whether a block is on and this virtual-key code is its cancel key.</summary>
    public static bool IsCancelKey(int key) => _cancelKey != 0 && key == _cancelKey && Blocking;

    /// <summary>
    /// The player's press of the cancel key: the key block and the mouse block end now, and TakeBlockCancelled reports
    /// it. Called from either hook. Held keys are pressed again by Run as the block ends, as for any other end.
    /// </summary>
    public static void CancelBlock()
    {
        Interlocked.Exchange(ref _blockUntil, 0);
        MouseBlock.Stop();
        _blockCancelled = true;
    }

    /// <summary>Whether the player ended the last block with the cancel key, cleared by the read.</summary>
    public static bool TakeBlockCancelled()
    {
        var cancelled = _blockCancelled;
        _blockCancelled = false;
        return cancelled;
    }

    /// <summary>
    /// Drops the player's key-downs for this long from now, replacing any block; 0 ends it. A press of cancelKey, if
    /// not 0, ends it early, and the mouse block with it.
    /// </summary>
    public static void Block(int ms, int cancelKey = 0)
    {
        ms = Math.Clamp(ms, 0, MostBlockMs);

        lock (Gate)
        {
            if (ms > 0)
            {
                _cancelKey = cancelKey;
                _blockCancelled = false;
            }

            Interlocked.Exchange(ref _blockUntil, ms == 0 ? 0 : Environment.TickCount64 + ms);

            // A mouse button as the cancel key: the mouse hook is kept for the whole key block, not only its own.
            if (ms > 0 && CancelKeyIsMouseButton)
                MouseBlock.Watch();

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
                // Dropped, and not remembered for Restore: only a key already held when the block began is pressed
                // again. Remembering presses made during the block would let Alt then F4, both pressed while blocked,
                // arrive together when it ends and close the game.
                if (Blocking)
                {
                    if (DroppedThisBlock.TryAdd((int)info.VirtualKey, 0))
                        Note($"player's {NameOfKey((int)info.VirtualKey)} down dropped");

                    // The cancel key: the player taking control back. Both blocks end now, in the hook, so this does
                    // not wait on the caller's next tick; the caller learns of it through TakeBlockCancelled.
                    if (IsCancelKey((int)info.VirtualKey))
                        CancelBlock();

                    return 1;
                }

                if (_watching)
                    Pressed.Enqueue((int)info.VirtualKey);
            }
        }
        else if (code >= 0 && (int)message is WmKeyUp or WmSysKeyUp && !HeldThroughBlock.IsEmpty)
        {
            // Let go during the block, or after it ended and before Run pressed the held keys again: not pressed again.
            // The key-up itself passes. Asked only while blocking, a key let go in the up to 20ms between a block's end
            // and Restore was pressed again with the player's finger off it, and stayed down in the game; a key the
            // game holds reads down to GetAsyncKeyState, so the next block noted it held and pressed it again too (the
            // player's account of S staying held after instant loot, AutoExpedition, 2026-10-25).
            var info = Marshal.PtrToStructure<KeyHookInfo>(data);

            if ((info.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
            {
                if (HeldThroughBlock.TryRemove((int)info.VirtualKey, out _))
                    Note($"player's {NameOfKey((int)info.VirtualKey)} up{(Blocking ? "" : ", after the block ended")}: not to be pressed again");
            }
            else if (HeldThroughBlock.ContainsKey((int)info.VirtualKey))
                Note($"an injected {NameOfKey((int)info.VirtualKey)} up,  (this plugin's let-go, or another program's), flags {info.Flags:X}: still to be pressed again");
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

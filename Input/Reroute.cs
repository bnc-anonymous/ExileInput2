using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Keys = System.Windows.Forms.Keys;

namespace ExileInput2;

/// <summary>
/// Other plugins' input, sent on the hardware-input boards instead: ReAgent pressing a flask key, a plugin clicking or
/// moving the cursor with ExileCore2.Input. See AdvancedSettings.RerouteOtherInput.
///
/// **How.** Every way a plugin in this process sends input - SendInput, keybd_event, mouse_event, ExileCore2.Input,
/// InputHelper.SendInput*, a plugin's own P/Invoke - ends in one of two system-call stubs in win32u.dll: NtUserSendInput
/// and NtUserSetCursorPos (checked on Windows 11 26200, 2026-11-04: user32's SendInput and SetCursorPos jump straight to
/// them, and keybd_event and mouse_event call NtUserSendInput). Install puts a jump over the start of each, into this
/// class, so the input goes to the boards on the caller's own thread, at once, in order, and nothing waits on a queue,
/// a hook or a lease. What the boards cannot send - typed text, a key with no USB equivalent, a horizontal wheel -
/// goes on to the real stub, of which a copy is kept to call.
///
/// **Why it is safe to patch.** Each stub is 24 bytes that only load a system-call number and make the call, so a copy
/// of them works anywhere and no instruction needs moving or decoding. The jump is 5 bytes inside one aligned 8, written
/// in one atomic exchange, so no thread can run half an old instruction and half a new one, and no thread is suspended.
/// Only this process is patched - the HUD's, not the game's. Install refuses, saying why, if a stub is not the shape
/// expected: another Windows build, or something else already hooked there.
///
/// **What it keeps.** A key or button let go that this did not press - pressed before it began, or the old way - goes
/// the old way, so Windows does not keep it down. Turned off, it lets go on the boards of whatever it pressed there.
/// Only while the game is in front (Active); otherwise every call goes straight through.
/// </summary>
internal static unsafe class Reroute
{
    /// <summary>Whether input is taken now: installed, and the game in front. See SetActive.</summary>
    public static volatile bool Active;

    /// <summary>Why the patch could not be put in, for the settings panel; empty when it could.</summary>
    public static volatile string Warning = "";

    /// <summary>Whether the patch is in.</summary>
    public static bool Installed => _sendInput is { On: true };

    private static readonly object Gate = new();

    private static Patch _sendInput;

    private static Patch _setCursorPos;

    /// <summary>Set while this thread is inside a detour, so nothing this calls can come back into it.</summary>
    [ThreadStatic]
    private static bool _inside;

    /// <summary>Keys pressed on the boards for other plugins and not yet let go, by virtual-key code.</summary>
    private static readonly ConcurrentDictionary<int, byte> HeldKeys = new();

    /// <summary>Mouse buttons pressed on the boards for other plugins and not yet let go: 0 left to 4 forward.</summary>
    private static readonly ConcurrentDictionary<int, byte> HeldButtons = new();

    /// <summary>Extra info that marks input to be sent the old way, untouched. "EI2R".</summary>
    private static readonly UIntPtr Mark = (UIntPtr)0x45493252u;

    /// <summary>
    /// How long a rerouted press waits for the cursor to reach where it was sent: a plugin that sets the cursor and
    /// clicks at once expects the click there, and the board moves the cursor rather than putting it.
    /// </summary>
    private const int PressWaitMs = 100;

    //--------------------------------------------------------------------+
    // Turning it on and off
    //--------------------------------------------------------------------+

    /// <summary>Puts the patch in, if not already. Null when it is in; otherwise why not, also kept in Warning.</summary>
    public static string Install()
    {
        lock (Gate)
        {
            if (Installed)
                return null;

            try
            {
                if (_sendInput == null)
                {
                    var win32u = NativeLibrary.Load("win32u.dll");
                    var sendInput = NativeLibrary.GetExport(win32u, "NtUserSendInput");
                    var setCursorPos = NativeLibrary.GetExport(win32u, "NtUserSetCursorPos");

                    if (Unexpected(sendInput) is { } bad1)
                        return Refuse($"NtUserSendInput {bad1}");

                    if (Unexpected(setCursorPos) is { } bad2)
                        return Refuse($"NtUserSetCursorPos {bad2}");

                    var page = AllocateNear(sendInput, setCursorPos);

                    if (page == 0)
                        return Refuse("no memory could be had near win32u.dll for the jumps");

                    _sendInput = new Patch(sendInput, page, 0,
                        (nint)(delegate* unmanaged<uint, InputRecord*, int, uint>)&SendInputDetour);
                    _setCursorPos = new Patch(setCursorPos, page, 128,
                        (nint)(delegate* unmanaged<int, int, int>)&SetCursorPosDetour);

                    if (!VirtualProtect(page, (nuint)PageSize, PageExecuteRead, out _))
                        return Refuse("the jumps' memory could not be made executable");

                    FlushInstructionCache(GetCurrentProcess(), page, (nuint)PageSize);
                }

                if (!_sendInput.Apply(true))
                    return Refuse("NtUserSendInput changed while being patched");

                if (!_setCursorPos.Apply(true))
                {
                    _sendInput.Apply(false);
                    return Refuse("NtUserSetCursorPos changed while being patched");
                }

                Warning = "";
                return null;
            }
            catch (Exception e)
            {
                return Refuse(e.Message);
            }
        }
    }

    private static string Refuse(string why)
    {
        var said = $"Rerouting other plugins' input is not available: {why}.";
        Warning = said;
        return said;
    }

    /// <summary>Takes the patch out, letting go on the boards of what it pressed there. Safe when out.</summary>
    public static void Uninstall()
    {
        SetActive(false);

        lock (Gate)
        {
            _setCursorPos?.Apply(false);
            _sendInput?.Apply(false);
        }
    }

    /// <summary>
    /// Takes input or lets it through, as the game comes to the front or leaves it. Going off lets go on the boards of
    /// what it pressed there; the plugin that pressed it presses it again if it still wants it, the old way.
    /// </summary>
    public static void SetActive(bool active)
    {
        if (Active == active || (active && !Installed))
            return;

        Active = active;

        if (active)
            return;

        foreach (var key in HeldKeys.Keys)
            Boards.Key((Keys)key, false);

        foreach (var button in HeldButtons.Keys)
            Boards.Button(button, false);

        HeldKeys.Clear();
        HeldButtons.Clear();
    }

    //--------------------------------------------------------------------+
    // The detours
    //--------------------------------------------------------------------+

    [UnmanagedCallersOnly]
    private static uint SendInputDetour(uint count, InputRecord* inputs, int size)
    {
        if (!Active || _inside || inputs == null || size != sizeof(InputRecord))
            return SendInputOriginal(count, inputs, size);

        _inside = true;

        try
        {
            uint sent = 0;

            for (var i = 0; i < count; i++)
            {
                bool routed;

                try
                {
                    routed = Route(&inputs[i]);
                }
                catch
                {
                    // Never let an exception out of here: it would take the HUD down with it.
                    routed = false;
                }

                sent += routed ? 1u : SendInputOriginal(1, &inputs[i], size);
            }

            return sent;
        }
        finally
        {
            _inside = false;
        }
    }

    [UnmanagedCallersOnly]
    private static int SetCursorPosDetour(int x, int y)
    {
        if (!Active || _inside)
            return SetCursorPosOriginal(x, y);

        _inside = true;

        try
        {
            // A click just sent may still be holding its button for its shortest hold; moving now would drag.
            Boards.WaitForButtonUps(30);
            Boards.CursorTo(x, y);
            return 1;
        }
        catch
        {
            return SetCursorPosOriginal(x, y);
        }
        finally
        {
            _inside = false;
        }
    }

    private static uint SendInputOriginal(uint count, InputRecord* inputs, int size) =>
        ((delegate* unmanaged<uint, InputRecord*, int, uint>)_sendInput.Original)(count, inputs, size);

    private static int SetCursorPosOriginal(int x, int y) =>
        ((delegate* unmanaged<int, int, int>)_setCursorPos.Original)(x, y);

    /// <summary>Sends one input on the boards. False when it is to go the old way, untouched.</summary>
    private static bool Route(InputRecord* input) => input->Type switch
    {
        InputKeyboard => RouteKey(input->Keyboard),
        InputMouse => RouteMouse(input->Mouse),
        _ => false,
    };

    private static bool RouteKey(KeyInput key)
    {
        if (key.ExtraInfo == Mark || (key.Flags & KeyeventfUnicode) != 0)
            return false;

        int code = key.Vk;

        if ((key.Flags & KeyeventfScanCode) != 0)
            code = (int)MapVirtualKey(key.Scan | ((key.Flags & KeyeventfExtendedKey) != 0 ? 0xE000u : 0u), MapvkVscToVkEx);

        if (code is <= 0 or >= 0xFF)
            return false;

        if ((key.Flags & KeyeventfKeyUp) != 0)
        {
            // A let-go of a key this never pressed: it went down the old way, and comes up the old way.
            if (!HeldKeys.TryRemove(code, out _))
                return false;

            Boards.Key((Keys)code, false);
            return true;
        }

        // A key the board has no way to send goes the old way.
        if (!Boards.Key((Keys)code, true))
            return false;

        HeldKeys[code] = 0;
        return true;
    }

    private static bool RouteMouse(MouseInput mouse)
    {
        if (mouse.ExtraInfo == Mark || (mouse.Flags & MouseeventfHWheel) != 0)
            return false;

        var flags = mouse.Flags;

        // In the order Windows applies them: the move, the buttons, then the wheel.
        if ((flags & MouseeventfMove) != 0)
        {
            Boards.WaitForButtonUps(30);

            if ((flags & MouseeventfAbsolute) != 0)
            {
                var (x, y) = ToScreen(mouse.Dx, mouse.Dy, (flags & MouseeventfVirtualDesk) != 0);
                Boards.CursorTo(x, y);
            }
            else
            {
                Boards.MoveBy(mouse.Dx, mouse.Dy);
            }
        }

        var x1OrX2 = mouse.MouseData == 2 ? 4 : 3;

        RouteButton(flags, MouseeventfLeftDown, 0, true, mouse);
        RouteButton(flags, MouseeventfLeftUp, 0, false, mouse);
        RouteButton(flags, MouseeventfRightDown, 1, true, mouse);
        RouteButton(flags, MouseeventfRightUp, 1, false, mouse);
        RouteButton(flags, MouseeventfMiddleDown, 2, true, mouse);
        RouteButton(flags, MouseeventfMiddleUp, 2, false, mouse);
        RouteButton(flags, MouseeventfXDown, x1OrX2, true, mouse);
        RouteButton(flags, MouseeventfXUp, x1OrX2, false, mouse);

        if ((flags & MouseeventfWheel) != 0)
        {
            var delta = (int)mouse.MouseData;

            // Whole notches on the board; a high-resolution part-notch, which the board cannot send, the old way.
            if (delta != 0 && delta % 120 == 0)
                Boards.Scroll(delta / 120);
            else
                SendMouseOriginal(MouseeventfWheel, mouse.MouseData);
        }

        return true;
    }

    private static void RouteButton(uint flags, uint flag, int button, bool down, MouseInput mouse)
    {
        if ((flags & flag) == 0)
            return;

        if (down)
        {
            HeldButtons[button] = 0;
            Boards.Button(button, true, PressWaitMs);
            return;
        }

        // A let-go of a button this never pressed: it went down the old way, and comes up the old way.
        if (HeldButtons.TryRemove(button, out _))
            Boards.Button(button, false);
        else
            SendMouseOriginal(flag, mouse.MouseData);
    }

    /// <summary>One part of a mouse input - a button or the wheel - sent the old way on its own.</summary>
    private static void SendMouseOriginal(uint flag, uint data)
    {
        var record = new InputRecord
        {
            Type = InputMouse,
            Mouse = new MouseInput { Flags = flag, MouseData = data, ExtraInfo = Mark },
        };

        SendInputOriginal(1, &record, sizeof(InputRecord));
    }

    /// <summary>An absolute move's 0 to 65535 position as a screen pixel, on the primary screen or the whole desktop.</summary>
    private static (int X, int Y) ToScreen(int dx, int dy, bool wholeDesktop)
    {
        int left = 0, top = 0, width, height;

        if (wholeDesktop)
        {
            left = GetSystemMetrics(SmXVirtualScreen);
            top = GetSystemMetrics(SmYVirtualScreen);
            width = GetSystemMetrics(SmCxVirtualScreen);
            height = GetSystemMetrics(SmCyVirtualScreen);
        }
        else
        {
            width = GetSystemMetrics(SmCxScreen);
            height = GetSystemMetrics(SmCyScreen);
        }

        return (left + (int)Math.Round(dx * (width - 1) / 65535.0), top + (int)Math.Round(dy * (height - 1) / 65535.0));
    }

    //--------------------------------------------------------------------+
    // The patch
    //--------------------------------------------------------------------+

    /// <summary>
    /// What a win32u system-call stub looks like on x64: mov r10,rcx; mov eax,<number>; test byte [7FFE0308],1; jne +3;
    /// syscall; ret; int 2E; ret. Null when it is that, aligned so the jump fits in one aligned 8 bytes; else what is
    /// wrong with it.
    /// </summary>
    private static string Unexpected(nint stub)
    {
        if ((stub & 7) > 3)
            return "is not aligned for an atomic patch";

        var code = new ReadOnlySpan<byte>((void*)stub, StubSize);

        return code[..4].SequenceEqual(StubHead) && code[8..].SequenceEqual(StubTail)
            ? null
            : $"is not the expected system-call stub ({Convert.ToHexString(code)}); another Windows build, or already hooked";
    }

    private const int StubSize = 24;

    private static ReadOnlySpan<byte> StubHead => [0x4C, 0x8B, 0xD1, 0xB8];

    private static ReadOnlySpan<byte> StubTail =>
        [0xF6, 0x04, 0x25, 0x08, 0x03, 0xFE, 0x7F, 0x01, 0x75, 0x03, 0x0F, 0x05, 0xC3, 0xCD, 0x2E, 0xC3];

    private const int PageSize = 4096;

    /// <summary>
    /// One patched stub. In the page near win32u: a copy of the stub, to call as the original, and a jump on to the
    /// detour, which the 5-byte jump written over the stub lands on. The page is never freed: a thread may be passing
    /// through it as the patch comes out.
    /// </summary>
    private sealed class Patch
    {
        private readonly nint _target;
        private readonly long _before;
        private readonly long _after;

        public nint Original { get; }

        public bool On { get; private set; }

        public Patch(nint target, nint page, int offset, nint detour)
        {
            _target = target;

            var original = page + offset;
            var relay = page + offset + 32;

            // The stub, copied whole: it only loads a number and makes a system call, so it runs the same from here.
            Buffer.MemoryCopy((void*)target, (void*)original, StubSize, StubSize);

            // jmp qword [rip+0]; the detour's address.
            var r = (byte*)relay;
            r[0] = 0xFF;
            r[1] = 0x25;
            *(int*)(r + 2) = 0;
            *(long*)(r + 6) = detour;

            Original = original;

            // The 8 aligned bytes holding the stub's first 5, with a jmp rel32 to the relay in place of those 5.
            var aligned = target & ~(nint)7;
            var at = (int)(target - aligned);

            _before = *(long*)aligned;

            var bytes = BitConverter.GetBytes(_before);
            bytes[at] = 0xE9;
            BitConverter.GetBytes((int)(relay - (target + 5))).CopyTo(bytes, at + 1);
            _after = BitConverter.ToInt64(bytes);
        }

        /// <summary>Puts the jump in or takes it out, in one atomic write. False when the bytes were not as left.</summary>
        public bool Apply(bool on)
        {
            if (On == on)
                return true;

            var aligned = _target & ~(nint)7;

            if (!VirtualProtect(aligned, 8, PageExecuteReadWrite, out var old))
                return false;

            var from = on ? _before : _after;
            var seen = Interlocked.CompareExchange(ref *(long*)aligned, on ? _after : _before, from);

            VirtualProtect(aligned, 8, old, out _);
            FlushInstructionCache(GetCurrentProcess(), aligned, 8);

            if (seen != from)
                return false;

            On = on;
            return true;
        }
    }

    /// <summary>A page within a rel32 jump of both stubs, for the copies and the jumps on; 0 if none could be had.</summary>
    private static nint AllocateNear(nint first, nint second)
    {
        const long granularity = 0x10000;
        const long reach = 0x70000000;

        var start = Math.Min((long)first, (long)second) & ~(granularity - 1);

        for (long distance = granularity; distance < reach; distance += granularity)
        {
            foreach (var at in new[] { start - distance, start + distance })
            {
                if (at <= 0)
                    continue;

                var page = VirtualAlloc((nint)at, PageSize, MemCommit | MemReserve, PageReadWrite);

                if (page == 0)
                    continue;

                if (Math.Abs((long)page - (long)first) < reach && Math.Abs((long)page - (long)second) < reach)
                    return page;

                VirtualFree(page, 0, MemRelease);
            }
        }

        return 0;
    }

    //--------------------------------------------------------------------+
    // Windows
    //--------------------------------------------------------------------+

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint KeyeventfScanCode = 0x0008;

    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfRightDown = 0x0008;
    private const uint MouseeventfRightUp = 0x0010;
    private const uint MouseeventfMiddleDown = 0x0020;
    private const uint MouseeventfMiddleUp = 0x0040;
    private const uint MouseeventfXDown = 0x0080;
    private const uint MouseeventfXUp = 0x0100;
    private const uint MouseeventfWheel = 0x0800;
    private const uint MouseeventfHWheel = 0x1000;
    private const uint MouseeventfVirtualDesk = 0x4000;
    private const uint MouseeventfAbsolute = 0x8000;

    private const uint MapvkVscToVkEx = 3;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

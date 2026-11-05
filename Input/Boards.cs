using System;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>
/// The hardware-input passthrough boards, when the player has ticked Use Hardware Input: two boards between the real
/// keyboard and mouse and the PC that merge this plugin's input into the real devices' own, so Windows and the game see
/// only the real devices. See AdvancedSettings.UseHardwareInput and InputOut.
///
/// The boards are opened once and shared, since only one process can own their COM ports. They decide takeover: real
/// input stops injection in progress on that board, and InterruptedByPlayer says so for Tick to stop the caller. Key and
/// mouse blocks are the boards' locks (see LockKeyboard). What the boards do not do yet - steering by the game's
/// cursor, the follow and arrival checks, paths and timing - stays this plugin's, as before.
///
/// Built without hardware-input (its repository not found beside this one), every member says it is unavailable.
/// </summary>
internal static partial class Boards
{
    /// <summary>Whether this plugin's input goes to the boards now. Set by ExileInput2.Tick once they are open.</summary>
    public static volatile bool InUse;

    /// <summary>What the boards are doing, for the settings panel.</summary>
    public static volatile string Status = "";

    /// <summary>Why the boards were last found unavailable, for the settings panel; empty when they were not.</summary>
    public static volatile string Warning = "";

    /// <summary>Whether the boards are open and ready for input.</summary>
    public static bool Open => _state == State.Open;

    /// <summary>Whether the boards are being looked for.</summary>
    public static bool Opening => _state == State.Opening;

    /// <summary>Whether the boards are closed, with no attempt under way.</summary>
    public static bool Closed => _state == State.Closed;

    private enum State
    {
        Closed,
        Opening,
        Open,
    }

    private static volatile State _state;

    /// <summary>Why opening failed or the boards were lost since the last call, cleared by the read; null for neither.</summary>
    public static string TakeFailure() => System.Threading.Interlocked.Exchange(ref _failure, null);

    private static string _failure;

    private static void Fail(string why)
    {
        Warning = why;
        Status = "";
        System.Threading.Interlocked.Exchange(ref _failure, why);
    }

    /// <summary>
    /// The windows virtual-key code a key is sent as on the boards: the left-hand key for the generic Shift, Control
    /// and Alt, since a keyboard has no generic key to press.
    /// </summary>
    internal static int SidedKey(Keys key) => (key & Keys.KeyCode) switch
    {
        Keys.ShiftKey => (int)Keys.LShiftKey,
        Keys.ControlKey => (int)Keys.LControlKey,
        Keys.Menu => (int)Keys.LMenu,
        var code => (int)code,
    };
}

#if HARDWARE_INPUT
internal static partial class Boards
{
    private static HardwareInput.Passthrough.PassthroughDevices _devices;

    private static readonly object Gate = new();

    /// <summary>
    /// Why the boards cannot be used on this system, found without opening them, or null when they may be. For the
    /// checkbox, so it does not tick on a system with no boards at all.
    /// </summary>
    public static string Unavailable()
    {
        try
        {
            return HardwareInput.Passthrough.PassthroughBoards.FindDebugProbePorts().Count == 0
                ? "Hardware input is not available on this system: no passthrough board's Debug Probe is plugged in."
                : null;
        }
        catch (Exception e)
        {
            return $"Hardware input is not available on this system: {e.Message}";
        }
    }

    /// <summary>Opens the boards in the background, if not open or opening. TakeFailure says if it fails.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_state != State.Closed)
                return;

            _state = State.Opening;
        }

        Warning = "";
        Status = "Looking for the boards...";
        System.Threading.Tasks.Task.Run(OpenAsync);
    }

    private static async System.Threading.Tasks.Task OpenAsync()
    {
        HardwareInput.Passthrough.PassthroughDevices devices = null;

        try
        {
            devices = await HardwareInput.Passthrough.PassthroughBoards.OpenAsync().ConfigureAwait(false);

            if (devices.Keyboard == null || devices.Mouse == null)
                throw new HardwareInput.HardwareInputException(devices.Keyboard == null
                    ? "no board has a keyboard plugged in."
                    : "no board has a mouse plugged in.");

            foreach (var injector in new HardwareInput.Passthrough.PassthroughInjector[] { devices.Keyboard, devices.Mouse })
            {
                if (injector.Link.Info is { State: not HardwareInput.Passthrough.PassthroughState.Active } info)
                    throw new HardwareInput.HardwareInputException(
                        $"the board on {injector.Link.Name} is not ready ({info.State}); Windows may still be setting its device up.");
            }

            // Each board stops on its own device only: a key pressed while walking with WASD must not cut a loot click
            // short, any more than it did before. Keys stop a caller only when it asks (KeyWatch).
            devices.LinkInterruptions = false;

            // The mouse board stops this plugin's input for the player's mouse, as a moved mouse always has. The keyboard
            // board merges: under Interrupt it refuses every injected key while the player holds any real one, so a flask
            // pressed while walking on W would never go out, and the player's keys never stopped this plugin's input
            // unless a caller asked (KeyWatch).
            await devices.Mouse.Link.ConfigureAsync(new HardwareInput.Passthrough.ArbitrationSettings
            {
                Policy = HardwareInput.Passthrough.ArbitrationPolicy.Interrupt,
            }).ConfigureAwait(false);

            await devices.Keyboard.Link.ConfigureAsync(new HardwareInput.Passthrough.ArbitrationSettings
            {
                Policy = HardwareInput.Passthrough.ArbitrationPolicy.Merge,
            }).ConfigureAwait(false);

            // The side buttons exempt, and no escape input until a key block asks for its cancel key. See InputRules.
            await devices.ConfigureInputsAsync(InputRules(0)).ConfigureAwait(false);

            devices.Mouse.Link.Interrupted += OnMouseInterrupted;
            devices.Mouse.Link.PhysicalActivity += OnMouseActivity;
            devices.Keyboard.Link.Interrupted += OnKeyboardInterrupted;
            devices.Mouse.Link.LockChanged += OnLockChanged;
            devices.Keyboard.Link.LockChanged += OnLockChanged;
            devices.Mouse.Link.Faulted += OnFaulted;
            devices.Keyboard.Link.Faulted += OnFaulted;
            devices.Mouse.Link.StateChanged += OnStateChanged;
            devices.Keyboard.Link.StateChanged += OnStateChanged;

            lock (Gate)
            {
                if (_state != State.Opening)
                {
                    // Closed while opening: the player unticked the box.
                    devices.Dispose();
                    return;
                }

                _devices = devices;
                _escapeKey = 0;
                _holdBack = devices.Keyboard.Link.Info.FirmwareVersion < ShowsEveryChangeFrom ||
                            devices.Mouse.Link.Info.FirmwareVersion < ShowsEveryChangeFrom;
                _state = State.Open;
            }

            Status = $"Keyboard board on {devices.Keyboard.Link.Name}, mouse board on {devices.Mouse.Link.Name}" +
                     $" (firmware {Firmware(devices.Keyboard.Link.Info.FirmwareVersion)}, {Firmware(devices.Mouse.Link.Info.FirmwareVersion)}).";
            StartSteering();
        }
        catch (Exception e)
        {
            try
            {
                devices?.Dispose();
            }
            catch
            {
                // Closing what failed to open; nothing more to do.
            }

            // Said before the state reads closed, so Tick does not find the box ticked and try again; and not said at all
            // when the box was unticked meanwhile.
            if (_state == State.Opening)
                Fail($"Hardware input is not available on this system: {Said(e)}");

            lock (Gate)
            {
                if (_state == State.Opening)
                    _state = State.Closed;
            }
        }
    }

    private static string Firmware(int version) => $"{version >> 8}.{version & 0xFF}";

    private static string Said(Exception e) => e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;

    /// <summary>Closes the boards, letting go of everything injected. Safe when closed.</summary>
    public static void Close()
    {
        HardwareInput.Passthrough.PassthroughDevices devices;

        lock (Gate)
        {
            devices = _devices;
            _devices = null;
            _state = State.Closed;
        }

        Status = "";
        StopSteering();
        OwnKeys.Clear();
        _buttonsHeld = 0;

        if (devices == null)
            return;

        try
        {
            devices.Mouse.Link.Interrupted -= OnMouseInterrupted;
            devices.Keyboard.Link.Interrupted -= OnKeyboardInterrupted;
            devices.Dispose();
        }
        catch
        {
            // The boards let go of everything injected on their own when the link goes quiet.
        }
    }

    private static void OnFaulted(object sender, Exception e)
    {
        if (_state != State.Open)
            return;

        // Not on this thread, which is the link's reader: closing joins it.
        System.Threading.Tasks.Task.Run(() =>
        {
            // Said first, so Tick does not find the boards closed and the box ticked, and open them again.
            Fail($"Hardware input stopped: the connection to a board was lost ({e.Message}).");
            Close();
        });
    }

    private static void OnStateChanged(object sender, HardwareInput.Passthrough.PassthroughInfo info)
    {
        if (_state != State.Open || info.State == HardwareInput.Passthrough.PassthroughState.Active)
            return;

        // Not on this thread, which is the link's reader: closing joins it.
        System.Threading.Tasks.Task.Run(() =>
        {
            Fail($"Hardware input stopped: a board is no longer active ({info.State}).");
            Close();
        });
    }

    //--------------------------------------------------------------------+
    // Keys
    //--------------------------------------------------------------------+

    /// <summary>
    /// Keys sent on the keyboard board, by virtual-key code, with when they stop counting as sent - never while held, and
    /// OwnKeyGraceMs after they are let go - so KeyWatch's hook can tell them from the player's: a board's keys reach
    /// Windows without the injected flag, like the player's own. The grace is for a tap, whose let-go is sent before
    /// Windows has seen the press.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> OwnKeys = new();

    private const int OwnKeyGraceMs = 150;

    /// <summary>Whether the keyboard board holds this key for this plugin: pressed and not yet let go.</summary>
    private static bool HeldOnBoard(int key) => OwnKeys.TryGetValue(key, out var until) && until == long.MaxValue;

    /// <summary>Whether this key was sent on the keyboard board, and not the player's. See OwnKeys.</summary>
    public static bool IsOwnKey(int key) => InUse && OwnKeys.TryGetValue(key, out var until) && Environment.TickCount64 < until;

    /// <summary>
    /// Presses or lets go of a key on the keyboard board. False when the board has no way to send the key, for the
    /// caller to send it the old way; true otherwise, sent or not. With the boards just lost, nothing is sent: Tick is
    /// about to move input off them, and a key pressed the old way meanwhile would be let go the wrong way.
    /// </summary>
    public static bool Key(Keys key, bool down)
    {
        var keyboard = _devices?.Keyboard;

        if (keyboard == null)
            return true;

        var code = SidedKey(key);

        if (code is <= 0 or > 0xFF || !Enum.IsDefined(typeof(HardwareInput.Key), (byte)code))
            return false;

        try
        {
            if (down)
            {
                // Pressed again before a held-back let-go went: the key stays down, and this press's let-go ends it.
                HeldBackKeyUps.TryRemove(code, out _);
                OwnKeys[code] = long.MaxValue;
                KeyDownAt[code] = Environment.TickCount64;
                keyboard.KeyDown((HardwareInput.Key)code);
            }
            else
            {
                // Not held on the board: nothing to let go of, and nothing sent. Releases are sent "just in case" on
                // every way out - Buttons.Release lets go of Ctrl, Shift and Alt each time a caller gives the cursor
                // back - which cost nothing as Windows input but is a message on the board's link each.
                if (!HeldOnBoard(code))
                    return true;

                var heldMs = Environment.TickCount64 - (KeyDownAt.TryGetValue(code, out var at) ? at : long.MinValue / 2);

                if (_holdBack && heldMs < ShortestHoldMs)
                    HoldBackKeyUp(code, (int)(ShortestHoldMs - heldMs));
                else
                    KeyUpNow(code);
            }
        }
        catch (ArgumentException)
        {
            // No USB key for it on this layout.
            OwnKeys.TryRemove(code, out _);
            return false;
        }
        catch
        {
            // The link failing; OnFaulted says so, and the board lets go of its keys on its own.
        }

        return true;
    }

    /// <summary>
    /// The shortest a key or button is held on boards older than firmware 0.3, in milliseconds. Those boards send what
    /// is held when each USB report is built, so a press and let-go sent together - a tap, ExileCore2.Input.Click,
    /// InputHelper.SendInputPress - cancelled out before any report carried them, and the game never saw the press. The
    /// press goes out at once; a let-go sooner than this waits until it is due. 20 covers a keyboard polled every 8ms,
    /// with room for the link. See _holdBack.
    /// </summary>
    private const int ShortestHoldMs = 20;

    /// <summary>
    /// The firmware from which a board shows every press and release in a report of its own, however fast they come,
    /// so nothing here needs to hold a let-go back (hardware-input firmware 0.3, 2026-11-04).
    /// </summary>
    private const ushort ShowsEveryChangeFrom = 0x0003;

    /// <summary>Whether a board is older than ShowsEveryChangeFrom, so let-gos are held back to ShortestHoldMs.</summary>
    private static volatile bool _holdBack = true;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> KeyDownAt = new();

    /// <summary>Let-gos waiting for their key's shortest hold, by virtual-key code, each with its own number.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> HeldBackKeyUps = new();

    private static long _heldBackNumber;

    private static void HoldBackKeyUp(int code, int ms)
    {
        var number = System.Threading.Interlocked.Increment(ref _heldBackNumber);
        HeldBackKeyUps[code] = number;

        System.Threading.Tasks.Task.Delay(Math.Max(1, ms)).ContinueWith(_ =>
        {
            // Not if the key was pressed again meanwhile, or the boards let go of everything.
            if (HeldBackKeyUps.TryRemove(new System.Collections.Generic.KeyValuePair<int, long>(code, number)))
                KeyUpNow(code);
        });
    }

    private static void KeyUpNow(int code)
    {
        try
        {
            OwnKeys[code] = Environment.TickCount64 + OwnKeyGraceMs;
            _devices?.Keyboard?.KeyUp((HardwareInput.Key)code);
        }
        catch
        {
            // The link failing; OnFaulted says so, and the board lets go of its keys on its own.
        }
    }

    /// <summary>Lets go of every key and button this plugin has injected, and nothing of the player's.</summary>
    public static void ReleaseAll()
    {
        var devices = _devices;

        if (devices == null)
            return;

        OwnKeys.Clear();
        HeldBackKeyUps.Clear();
        HeldBackButtonUps.Clear();
        _buttonsHeld = 0;
        Forget(devices.Keyboard?.ReleaseAllAsync());
        Forget(devices.Mouse?.ReleaseAllAsync());
    }

    //--------------------------------------------------------------------+
    // Mouse
    //--------------------------------------------------------------------+

    /// <summary>Presses or lets go of the left or right button on the mouse board. A press waits briefly for the cursor.</summary>
    public static void Button(bool right, bool down) =>
        Button(right ? HardwareInput.MouseButton.Right : HardwareInput.MouseButton.Left, down);

    /// <summary>
    /// Presses or lets go of a button on the mouse board by its number: 0 left, 1 right, 2 middle, 3 back (X1), 4 forward
    /// (X2). A press waits up to waitMs for the cursor to reach where it was sent. See Button.
    /// </summary>
    public static void Button(int which, bool down, int waitMs = SettleWaitMs)
    {
        if (which is >= 0 and <= 4)
            Button((HardwareInput.MouseButton)which, down, waitMs);
    }

    /// <summary>Moves the cursor by raw counts on the mouse board, as a mouse does, ending any steering.</summary>
    public static void MoveBy(int dx, int dy)
    {
        DropTarget();

        try
        {
            _devices?.Mouse?.Move(dx, dy);
        }
        catch
        {
            // The link failing; OnFaulted says so.
        }
    }

    /// <summary>
    /// Waits up to this long for every held-back button let-go to be sent, so a move straight after a click does not
    /// drag. See ShortestHoldMs.
    /// </summary>
    public static void WaitForButtonUps(int ms)
    {
        var until = Environment.TickCount64 + ms;

        while (!HeldBackButtonUps.IsEmpty && Environment.TickCount64 < until)
            System.Threading.Thread.Sleep(1);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> ButtonDownAt = new();

    /// <summary>Button let-gos waiting for their shortest hold, by button, each with its own number.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> HeldBackButtonUps = new();

    /// <summary>Turns the wheel on the mouse board: positive away from the player, a notch each.</summary>
    public static void Scroll(int notches)
    {
        try
        {
            _devices?.Mouse?.Scroll(notches);
        }
        catch
        {
            // The link failing; OnFaulted says so.
        }
    }

    private static void Button(HardwareInput.MouseButton button, bool down, int waitMs = SettleWaitMs)
    {
        var mouse = _devices?.Mouse;

        if (mouse == null)
            return;

        var which = (int)button;

        if (down)
        {
            // The board moves the cursor by counts, which Windows applies a millisecond or two later; a press sent while
            // the steering is still closing the last few pixels would land short of where the cursor was sent.
            WaitForCursor(waitMs);
            HeldBackButtonUps.TryRemove(which, out _);
            ButtonDownAt[which] = Environment.TickCount64;
            ButtonNow(button, true);
            return;
        }

        // Not held on the board, and no let-go of it waiting: nothing to let go of, and nothing sent. See Key.
        if ((_buttonsHeld & (1 << which)) == 0 && !HeldBackButtonUps.ContainsKey(which))
            return;

        var heldMs = Environment.TickCount64 - (ButtonDownAt.TryGetValue(which, out var at) ? at : long.MinValue / 2);

        if (!_holdBack || heldMs >= ShortestHoldMs)
        {
            ButtonNow(button, false);
            return;
        }

        var number = System.Threading.Interlocked.Increment(ref _heldBackNumber);
        HeldBackButtonUps[which] = number;

        System.Threading.Tasks.Task.Delay((int)Math.Max(1, ShortestHoldMs - heldMs)).ContinueWith(_ =>
        {
            if (HeldBackButtonUps.TryRemove(new System.Collections.Generic.KeyValuePair<int, long>(which, number)))
                ButtonNow(button, false);
        });
    }

    /// <summary>The buttons the mouse board holds for this plugin, as bits: 1 left, 2 right, 4 middle, 8 back, 16 forward.</summary>
    private static volatile int _buttonsHeld;

    private static void ButtonNow(HardwareInput.MouseButton button, bool down)
    {
        try
        {
            var mouse = _devices?.Mouse;
            var bit = 1 << (int)button;

            if (down)
            {
                _buttonsHeld |= bit;
                mouse?.ButtonDown(button);
            }
            else
            {
                _buttonsHeld &= ~bit;
                mouse?.ButtonUp(button);
            }
        }
        catch
        {
            // The link failing; OnFaulted says so.
        }
    }

    /// <summary>The longest a press waits for the cursor to reach where it was sent. See Button.</summary>
    private const int SettleWaitMs = 30;

    //--------------------------------------------------------------------+
    // Takeover, locks and escape
    //--------------------------------------------------------------------+

    /// <summary>
    /// What the mouse board saw when it last stopped this plugin's input for the player's own, cleared by the read; null
    /// when it has not since the last call. The board's word, so it is the player's hand and not a guess from positions.
    /// </summary>
    public static string TakeInterruptedByPlayer() => System.Threading.Interlocked.Exchange(ref _interruptedBy, null);

    private static string _interruptedBy;

    /// <summary>When a lock last ended for an escape input, as Environment.TickCount64. See OnMouseInterrupted.</summary>
    private static long _escapedAt;

    /// <summary>
    /// The player's own mouse, as the mouse board reports it: movement past its threshold (6 counts in 50ms), a button
    /// other than the exempt side buttons, or the wheel - whether or not anything is being injected. The board sees the
    /// real device before the PC does, so this is the player's hand and nothing else, where the position check it
    /// replaces on the boards (MousePointer.WatchForPlayer) guesses from where the cursor is, and a steered cursor is
    /// never exactly where it was put: it stopped Currency Exchange Flipper with a cursor 14px ahead of its path and
    /// nobody touching the mouse (2026-11-04). Not while a mouse block hides the player's input.
    /// </summary>
    private static void OnMouseActivity(object sender, HardwareInput.Passthrough.PhysicalActivityEventArgs e)
    {
        const HardwareInput.Passthrough.PhysicalActivity hand =
            HardwareInput.Passthrough.PhysicalActivity.Move | HardwareInput.Passthrough.PhysicalActivity.Button |
            HardwareInput.Passthrough.PhysicalActivity.Wheel;

        if ((e.Kinds & hand) == 0 || MouseBlocked || _devices?.Mouse?.Link.IsLocked == true)
            return;

        // The player has the mouse: the steering stops fighting them for it.
        DropTarget();
        System.Threading.Interlocked.Exchange(ref _interruptedBy, $"the mouse board saw the player's {e.Kinds & hand}");
    }

    private static void OnMouseInterrupted(object sender, HardwareInput.Passthrough.InterruptedEventArgs e)
    {
        // The player has the mouse: the steering stops fighting them for it. The board let go of its buttons.
        DropTarget();
        _buttonsHeld = 0;

        // Kinds None is the other board's escape relayed by PassthroughDevices.
        if (e.Kinds == HardwareInput.Passthrough.PhysicalActivity.None)
            return;

        // An escape input - a key block's cancel key - ends the blocks, and never stops the caller, as the cancel key
        // did before. See KeyWatch.CancelBlock. Pressed outside a block it does nothing here: AutoExpedition's action
        // key is its cancel key, and pressing it to start the next run stopped that run as it began (2026-11-04). An
        // escape input reaches the board's Interrupted whether or not it is locked, so a mouse button as a key block's
        // cancel key needs no mouse lock.
        if (e.Kinds.HasFlag(HardwareInput.Passthrough.PhysicalActivity.Escape))
        {
            BlockEscaped();
            return;
        }

        System.Threading.Interlocked.Exchange(ref _interruptedBy, $"the mouse board saw the player's {e.Kinds}");
    }

    private static void OnKeyboardInterrupted(object sender, HardwareInput.Passthrough.InterruptedEventArgs e)
    {
        // The player's keys let go of the board's, and the board forgot them; so does this.
        OwnKeys.Clear();

        if (e.Kinds.HasFlag(HardwareInput.Passthrough.PhysicalActivity.Escape))
            BlockEscaped();
    }

    /// <summary>
    /// An escape input pressed: when a block was on, or ended for one a moment ago, the player cancelled it, which
    /// KeyWatch.TakeBlockCancelled reports, and true. PassthroughDevices ends the other board's lock and stops it.
    /// </summary>
    private static bool BlockEscaped()
    {
        var now = Environment.TickCount64;
        var recent = now - System.Threading.Interlocked.Read(ref _escapedAt) < 250;

        if (!KeyBlockOn && !MouseBlocked && !recent && _devices?.IsLocked != true)
            return false;

        if (KeyBlockOn)
            KeyWatch.NoteBlockCancelled();

        _keyBlockOn = false;
        _mouseBlockOn = false;
        System.Threading.Interlocked.Exchange(ref _escapedAt, now);
        return true;
    }

    /// <summary>
    /// Whether the keyboard board's lock ended in the last moment, when the board presses again the keys the player still
    /// holds. Those presses are the player's held keys and not new ones, so KeyWatch does not count them.
    /// </summary>
    public static bool KeysJustUnlocked =>
        InUse && Environment.TickCount64 - System.Threading.Interlocked.Read(ref _keysUnlockedAt) < 150;

    private static long _keysUnlockedAt = long.MinValue / 2;

    private static void OnLockChanged(object sender, HardwareInput.Passthrough.LockChangedEventArgs e)
    {
        if (!e.Locked && sender == _devices?.Keyboard.Link)
            System.Threading.Interlocked.Exchange(ref _keysUnlockedAt, Environment.TickCount64);

        // The player pressed the block's cancel key.
        if (!e.Locked && e.Reason == HardwareInput.Passthrough.LockReason.Escape)
            BlockEscaped();
    }

    /// <summary>Whether a key block asked for is on the keyboard board. See LockKeyboard.</summary>
    private static volatile bool _keyBlockOn;

    private static long _keyBlockUntil;

    /// <summary>Whether a key block asked for is on and not yet run out.</summary>
    private static bool KeyBlockOn => _keyBlockOn && Environment.TickCount64 < System.Threading.Interlocked.Read(ref _keyBlockUntil);

    private static volatile bool _mouseBlockOn;

    private static long _mouseBlockUntil;

    /// <summary>Whether a mouse block is on: the mouse board's lock, which this plugin asked for.</summary>
    public static bool MouseBlocked => _mouseBlockOn && Environment.TickCount64 < System.Threading.Interlocked.Read(ref _mouseBlockUntil);

    /// <summary>
    /// A key block, as the keyboard board's lock: for up to KeyWatch.MostBlockMs the player's keys are hidden from the
    /// game, the keys they hold go up at its start, and the board presses again those of its resume list still held at
    /// the end (the letters, digits, Ctrl, Shift, Alt and Space). 0 ends it. A press of cancelKey, if not 0, ends it and the
    /// mouse block with it, and KeyWatch.TakeBlockCancelled reports it.
    /// </summary>
    public static void LockKeyboard(int ms, int cancelKey)
    {
        var devices = _devices;

        if (devices == null)
            return;

        ms = Math.Clamp(ms, 0, (int)HardwareInput.Passthrough.PassthroughLink.MaxLockDuration.TotalMilliseconds);
        _keyBlockOn = ms > 0;
        System.Threading.Interlocked.Exchange(ref _keyBlockUntil, Environment.TickCount64 + ms);

        Queue(async () =>
        {
            if (ms == 0)
            {
                await devices.Keyboard.Link.UnlockAsync().ConfigureAwait(false);
                return;
            }

            if (cancelKey != _escapeKey)
            {
                await devices.ConfigureInputsAsync(InputRules(cancelKey)).ConfigureAwait(false);
                _escapeKey = cancelKey;
            }

            await devices.Keyboard.Link.LockAsync(TimeSpan.FromMilliseconds(ms)).ConfigureAwait(false);
        });
    }

    /// <summary>A mouse block, as the mouse board's lock, for up to MouseBlock.MostMs; 0 ends it. See LockKeyboard.</summary>
    public static void LockMouse(int ms)
    {
        var devices = _devices;

        if (devices == null)
            return;

        var wasOn = _mouseBlockOn;

        _mouseBlockOn = ms > 0;
        System.Threading.Interlocked.Exchange(ref _mouseBlockUntil, Environment.TickCount64 + Math.Max(0, ms));

        if (ms == 0 && !wasOn)
            return;

        Queue(() => ms > 0
            ? devices.Mouse.Link.LockAsync(TimeSpan.FromMilliseconds(ms))
            : devices.Mouse.Link.UnlockAsync());
    }

    /// <summary>The cancel key last given to the boards as an escape input, 0 for none. See EscapeRules.</summary>
    private static int _escapeKey;

    /// <summary>
    /// The boards' input sets, with a key block's cancel key, if any, as the only escape input.
    ///
    /// **Mouse 4 and Mouse 5 are exempt**: pressing one is never the player taking over, so it does not stop what is
    /// being sent. They are hotkeys - AutoExpedition's action key is Mouse 4 - and under the library's defaults Mouse 4
    /// was also an escape input, so pressing it to start a run stopped the run as "Mouse moved" (2026-11-04). This
    /// plugin never counted a button as taking over before the boards; moving, the wheel and the left and right buttons
    /// still do. The library's other defaults are kept: Caps Lock exempt, and the resume list.
    /// </summary>
    private static HardwareInput.Passthrough.InputRules InputRules(int cancelKey)
    {
        HardwareInput.MouseButton? button = cancelKey switch
        {
            1 => HardwareInput.MouseButton.Left,
            2 => HardwareInput.MouseButton.Right,
            4 => HardwareInput.MouseButton.Middle,
            5 => HardwareInput.MouseButton.X1,
            6 => HardwareInput.MouseButton.X2,
            _ => null,
        };

        var buttons = new System.Collections.Generic.List<HardwareInput.MouseButton>();
        var keys = new System.Collections.Generic.List<HardwareInput.Key>();

        if (button is { } b)
            buttons.Add(b);
        else if (cancelKey is > 0 and <= 0xFF && Enum.IsDefined(typeof(HardwareInput.Key), (byte)cancelKey))
            keys.Add((HardwareInput.Key)cancelKey);

        return new HardwareInput.Passthrough.InputRules
        {
            ExemptButtons = [HardwareInput.MouseButton.X1, HardwareInput.MouseButton.X2],
            EscapeButtons = buttons,
            EscapeKeys = keys,
        };
    }

    /// <summary>Lock, unlock and input-set commands, sent one after another in the order asked.</summary>
    private static System.Threading.Tasks.Task _queue = System.Threading.Tasks.Task.CompletedTask;

    private static readonly object QueueGate = new();

    private static void Queue(Func<System.Threading.Tasks.Task> command)
    {
        lock (QueueGate)
        {
            var next = _queue.ContinueWith(async _ =>
            {
                try
                {
                    await command().ConfigureAwait(false);
                }
                catch
                {
                    // A lock not taken: the player's input is not hidden, which is how it was without the boards.
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            _queue = System.Threading.Tasks.TaskExtensions.Unwrap(next);
        }
    }

    private static void Forget(System.Threading.Tasks.Task task) =>
        task?.ContinueWith(t => _ = t.Exception, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

    //--------------------------------------------------------------------+
    // Steering the cursor
    //--------------------------------------------------------------------+

    // The mouse board moves the cursor by counts, as a mouse does, not to a point. So a point asked for is a target, and
    // a thread of its own steers the cursor onto it: each step sends how far the target moved since the last, plus a
    // little of the gap between where the cursor is and the target, as hardware-input's own MoveToAsync does. The
    // cursor trails Windows by a millisecond or two, so the gap is closed slowly rather than all at once, or it would be
    // sent twice. Once the target stops moving, small steps every few milliseconds bring it onto the exact pixel. The
    // path itself, its timing and the checks on it are this plugin's, as they were (see MousePointer).

    /// <summary>How strongly each step closes the gap between the cursor and its target, per step.</summary>
    private const double SteerGain = 0.15;

    /// <summary>The most counts one step sends on either axis, so no one report is large enough to fling the cursor.</summary>
    private const int MostStep = 60;

    /// <summary>A gap, in pixels on either axis, beyond which settling steps every millisecond. See Steer.</summary>
    private const int FarGap = 24;

    /// <summary>How long a target is steered for after it was last set, at most.</summary>
    private const int SteerForMs = 1000;

    private static readonly object TargetGate = new();

    private static bool _hasTarget;

    private static int _targetX;

    private static int _targetY;

    private static int _lastTargetX;

    private static int _lastTargetY;

    private static long _targetSetAt;

    private static long _lastSettleAt;

    private static double _carryX;

    private static double _carryY;

    private static int _onTarget;

    private static System.Threading.Thread _steering;

    private static volatile bool _steeringStopping;

    /// <summary>Sends the cursor to a screen point. See the steering notes above.</summary>
    public static void CursorTo(int x, int y)
    {
        lock (TargetGate)
        {
            if (!_hasTarget)
            {
                // Steering starts from where the cursor is, so the first step carries the path's first stretch.
                GetCursorPos(out var at);
                _lastTargetX = at.X;
                _lastTargetY = at.Y;
                _carryX = _carryY = 0;
            }

            _targetX = x;
            _targetY = y;
            _hasTarget = true;
            _onTarget = 0;
            _targetSetAt = Environment.TickCount64;
        }
    }

    /// <summary>Stops steering: the player has the mouse, or the boards are closing.</summary>
    private static void DropTarget()
    {
        lock (TargetGate)
            _hasTarget = false;
    }

    /// <summary>Waits up to this long for the cursor to settle on its target, or for there to be none.</summary>
    private static void WaitForCursor(int ms)
    {
        var until = Environment.TickCount64 + ms;

        while (Environment.TickCount64 < until)
        {
            lock (TargetGate)
            {
                if (!_hasTarget)
                    return;

                GetCursorPos(out var at);

                if (Math.Abs(at.X - _targetX) <= 1 && Math.Abs(at.Y - _targetY) <= 1 &&
                    _targetX == _lastTargetX && _targetY == _lastTargetY)
                    return;
            }

            System.Threading.Thread.Sleep(1);
        }
    }

    private static void StartSteering()
    {
        StopSteering();
        _steeringStopping = false;
        _steering = new System.Threading.Thread(SteerLoop)
        {
            IsBackground = true,
            Name = "ExileInput2 board steering",
            Priority = System.Threading.ThreadPriority.AboveNormal,
        };
        _steering.Start();
    }

    private static void StopSteering()
    {
        DropTarget();
        _steeringStopping = true;

        if (_steering is { } thread && thread != System.Threading.Thread.CurrentThread)
            thread.Join(500);

        _steering = null;
    }

    private static void SteerLoop()
    {
        var fine = false;

        try
        {
            fine = TimeBeginPeriod(1) == 0;
        }
        catch
        {
            // Steps at the system's default tick instead, about 15ms: slower to settle, no less right.
        }

        try
        {
            while (!_steeringStopping)
            {
                System.Threading.Thread.Sleep(1);

                try
                {
                    Steer();
                }
                catch
                {
                    // The link failing; OnFaulted closes the boards.
                }
            }
        }
        finally
        {
            if (fine)
                TimeEndPeriod(1);
        }
    }

    /// <summary>One step of the steering. See the notes above.</summary>
    private static void Steer()
    {
        var mouse = _devices?.Mouse;

        if (mouse == null)
            return;

        int stepX, stepY;

        lock (TargetGate)
        {
            if (!_hasTarget)
                return;

            var now = Environment.TickCount64;

            if (now - _targetSetAt > SteerForMs)
            {
                _hasTarget = false;
                return;
            }

            GetCursorPos(out var at);

            int gapX = _targetX - at.X, gapY = _targetY - at.Y;
            int movedX = _targetX - _lastTargetX, movedY = _targetY - _lastTargetY;

            _lastTargetX = _targetX;
            _lastTargetY = _targetY;

            if (movedX == 0 && movedY == 0)
            {
                // Settling: a third of the gap every 3ms, at least a count, so it creeps on rather than overshooting.
                if (gapX == 0 && gapY == 0)
                {
                    if (++_onTarget >= 3)
                        _hasTarget = false;

                    return;
                }

                _onTarget = 0;

                // Far off - a jump to a point, such as another plugin's SetCursorPos - a third of the gap every
                // millisecond, up to MostStep; near, every 3ms, creeping on as before.
                var far = Math.Abs(gapX) > FarGap || Math.Abs(gapY) > FarGap;

                if (!far && now - _lastSettleAt < 3)
                    return;

                _lastSettleAt = now;
                stepX = SettleStep(gapX);
                stepY = SettleStep(gapY);
                _carryX = _carryY = 0;
            }
            else
            {
                _onTarget = 0;
                _carryX += movedX + gapX * SteerGain;
                _carryY += movedY + gapY * SteerGain;
                stepX = Math.Clamp((int)Math.Round(_carryX), -MostStep, MostStep);
                stepY = Math.Clamp((int)Math.Round(_carryY), -MostStep, MostStep);

                // What a step could not send is left to the gap to bring back, not banked up as a backlog.
                _carryX = Math.Clamp(_carryX - stepX, -1, 1);
                _carryY = Math.Clamp(_carryY - stepY, -1, 1);
            }
        }

        if (stepX != 0 || stepY != 0)
            mouse.Move(stepX, stepY);
    }

    private static int SettleStep(int gap) =>
        gap == 0 ? 0 : Math.Sign(gap) * Math.Clamp((int)Math.Round(Math.Abs(gap) / 3.0), 1, MostStep);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
#else
internal static partial class Boards
{
    private const string NotBuilt =
        "Hardware input is not available on this system: this build of ExileInput2 has no hardware-input " +
        "(its repository was not found beside this one when the plugin was built).";

    public static string Unavailable() => NotBuilt;

    public static void Start()
    {
        _state = State.Closed;
        Fail(NotBuilt);
    }

    public static void Close()
    {
    }

    public static bool IsOwnKey(int key) => false;

    public static bool Key(Keys key, bool down) => false;

    public static void ReleaseAll()
    {
    }

    public static void Button(bool right, bool down)
    {
    }

    public static void Button(int which, bool down, int waitMs = 30)
    {
    }

    public static void MoveBy(int dx, int dy)
    {
    }

    public static void WaitForButtonUps(int ms)
    {
    }

    public static void Scroll(int notches)
    {
    }

    public static void CursorTo(int x, int y)
    {
    }

    public static string TakeInterruptedByPlayer() => null;

    public static bool MouseBlocked => false;

    public static bool KeysJustUnlocked => false;

    public static void LockKeyboard(int ms, int cancelKey)
    {
    }

    public static void LockMouse(int ms)
    {
    }
}
#endif

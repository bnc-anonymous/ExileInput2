using ExileCore2;
using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>
/// The button and the modifier keys, as things that can be done and asked about - and nothing else.
///
/// Lifted from the currency flipper, where the reasoning below was paid for. The shuttle paused
/// between pressing control and clicking; the collector and the roller sent both in the same breath.
/// Only the two without the pause ever turned a ctrl+right click into a bare right-click - which on
/// a Chaos Orb does not fail quietly, it uses the orb: off the stack and onto the cursor, where it
/// blocks every click after it.
///
/// A pause was the first answer, and reading the key state is the better one where it can be read:
/// control being down is a fact rather than something to wait out. So it is asked, and a click that
/// would go bare is not sent - a click not sent costs an attempt, where a click sent bare costs an
/// orb on the cursor and a run untangled by hand.
///
/// Where it cannot be read the pause is what is left, and that is a fallback rather than a failure.
/// An injected key does not always show up in any state we can see, and refusing on that reading is
/// not caution - it stopped every ctrl+click in the plugin, four times in a row, until the exchange
/// gave up on being opened. A check that never answers is not evidence of anything, so it is asked
/// once, believed if it ever answers, and dropped if it never does.
///
/// What is *not* here any more is the waiting. This used to own a Send that pressed the modifier,
/// polled up to 200ms for it to read back, held 60ms either side of the click and released - all on
/// the calling thread, which in this HUD is the one thread that draws every plugin. Better than a
/// third of a second of frozen overlay, for one click. The waiting happens across ticks in
/// <see cref="Stroke"/> now, and what is left here does its thing and returns.
/// </summary>
internal static class Buttons
{
    /// <summary>
    /// Whether the key state can be read at all from here.
    ///
    /// Null until something has been tried. Injected keys do not always show up in the state the
    /// game exposes, and where they never do the confirmation is not evidence of anything - so it
    /// is asked once, believed if it ever answers, and quietly dropped if it never does.
    /// </summary>
    private static bool? _readable;

    /// <summary>Whether the key state has ever been readable, for saying so on a panel.</summary>
    public static bool? Readable => _readable;

    /// <summary>Remembers that the state answered, so a later silence is a real no.</summary>
    public static void Answered() => _readable = true;

    /// <summary>
    /// Remembers that it never answers here, so nothing waits on it again.
    ///
    /// Only called once a whole confirmation window has gone by with no answer, and never over the
    /// top of a yes. Learning it from the first tick that reads nothing would learn "unreadable"
    /// from a key that had simply not arrived yet, and then never ask again.
    /// </summary>
    public static void NeverAnswers() => _readable ??= false;

    /// <summary>
    /// Whether control reads as held. Kept for the callers that only ever ask about control.
    /// </summary>
    public static bool ControlIsDown() => IsDown(Keys.LControlKey);

    /// <summary>
    /// Whether a modifier reads as held, asked both ways it might be visible.
    ///
    /// The Windows async state first, since that is where an injected key lands if it was injected
    /// at all, and the game's own view second. Either answering yes is a yes.
    ///
    /// Generalised from a control-only check, and that is a safety improvement rather than tidying.
    /// Before this, only control could be confirmed - a shift or an alt went down unverified and
    /// nothing would notice if it had not taken. For crafting that is the difference between
    /// applying the currency you meant and applying a different one.
    ///
    /// One look, no waiting. Somebody who wants to wait asks again next tick.
    /// </summary>
    public static bool IsDown(Keys key)
    {
        try
        {
            var vk = Modifier.VirtualKey(key);

            if (vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0)
                return true;

            return Input.IsKeyDown(key);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether every one of these reads as held.
    ///
    /// All of them, not any: a gesture that asked for shift and alt and got only shift is not
    /// half-right, it is a different gesture with a different outcome.
    /// </summary>
    public static bool AllDown(IReadOnlyList<Keys> keys)
    {
        if (!Modifier.Any(keys))
            return true;

        for (var i = 0; i < keys.Count; i++)
        {
            if (!IsDown(keys[i]))
                return false;
        }

        return true;
    }

    /// <summary>What was last pressed here, so that exactly that can be let go again.</summary>
    private static Keys[] _holding = Modifier.None;

    /// <summary>
    /// Presses the modifiers a gesture asked for, and only those.
    ///
    /// Only those is the change that was made when this took two bools, and the note then said the
    /// pair was "a trap laid for whoever first wants a plain shift-click". Taking a set closes the
    /// trap rather than widening it by one: alt is not a third bool, it is another key in a list
    /// that was always going to have to be a list.
    /// </summary>
    public static void Hold(IReadOnlyList<Keys> keys)
    {
        if (!Modifier.Any(keys))
            return;

        var held = new Keys[keys.Count];

        for (var i = 0; i < keys.Count; i++)
            held[i] = keys[i];

        // Recorded before pressing, not after. A key that may or may not have gone down is one to
        // try to release - the same rule HeldKeys states for the same reason.
        _holding = held;

        try
        {
            foreach (var key in held)
                Input.KeyDown(key);
        }
        catch
        {
            // Nothing useful to do about it here. The confirmation is what decides whether a click
            // goes out, and it refuses on its own if this did not take.
        }
    }

    /// <summary>
    /// Sends a click and nothing else, for a caller already holding whatever it needs held.
    ///
    /// Nothing here releases anything, because the caller decides when the modifier comes up.
    /// </summary>
    /// <summary>
    /// Puts a mouse button down and leaves it there, for a drag.
    ///
    /// **Press is a whole click and cannot be used for this.** It sends the down and the up in one
    /// call, so a drag built on it pressed and released at the source and then moved the cursor with
    /// nothing held - which is exactly what a drag is not. Held separately, and remembered, so
    /// LetGo can put it up on any path out even where nothing knows a drag was running.
    /// </summary>
    public static void Hold(bool right)
    {
        if (right)
        {
            Input.RightDown();
            _right = true;
        }
        else
        {
            Input.LeftDown();
            _left = true;
        }
    }

    /// <summary>
    /// Lets go of whichever mouse button is being held, if any.
    ///
    /// Unconditional in the same spirit as the modifier release below it: a left button left down is
    /// the worst thing this plugin can leave behind, so it comes up on every way out rather than
    /// only where somebody remembered a drag was in progress.
    /// </summary>
    public static void LetGo()
    {
        EndPress();

        if (_left)
        {
            Input.LeftUp();
            _left = false;
        }

        if (_right)
        {
            Input.RightUp();
            _right = false;
        }
    }

    /// <summary>Whether a mouse button is being held right now. See Hold.</summary>
    public static bool Holding => _left || _right;

    private static bool _left;
    private static bool _right;

    /// <summary>
    /// Puts a button down for a click and leaves it there for holdMs, after which Step brings it up. Nothing waits: the
    /// release comes from the plugin's own tick, whoever holds the cursor by then, so a caller letting go of the cursor
    /// straight after its click does not cut the hold short. A click already down is brought up first.
    ///
    /// It was a down and an up in one call, held for no time at all; a person's click is held 50 to 100 ms. See
    /// HumanTiming.
    /// </summary>
    public static void Press(bool right, int holdMs)
    {
        if (_pressUpAt != 0)
            EndPress();

        if (right)
            Input.RightDown();
        else
            Input.LeftDown();

        _pressRight = right;
        LastPressDownAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _pressUpAt = Environment.TickCount64 + Math.Max(0, holdMs);
    }

    /// <summary>
    /// When the last click's button went down, as System.Diagnostics.Stopwatch.GetTimestamp, so a caller reads the time
    /// since with Stopwatch.GetElapsedTime; 0 before any. The Stopwatch rather than Environment.TickCount64, which moves
    /// in steps of about 15.6ms. For a caller timing what the game does after its click from the moment the game was
    /// sent it, rather than from its own next frame. See ExileInput2.LastPressDownAt.
    /// </summary>
    public static long LastPressDownAt { get; private set; }

    /// <summary>When the button of the click in hand comes up, as Environment.TickCount64; 0 with none down.</summary>
    private static long _pressUpAt;

    private static bool _pressRight;

    /// <summary>What to do once the click's button is up and AfterPressMs more has gone by: let go of its modifiers.</summary>
    private static Action _afterPress;

    private static int _afterPressMs;

    /// <summary>When the after-press action is due, once the button is up; 0 until then.</summary>
    private static long _afterPressAt;

    /// <summary>Whether a click's button is down now. See Press.</summary>
    public static bool PressHeld => _pressUpAt != 0;

    /// <summary>How long until the click's button comes up, in milliseconds; 0 with none down.</summary>
    public static int MsUntilPressUp => _pressUpAt == 0 ? 0 : (int)Math.Max(0L, _pressUpAt - Environment.TickCount64);

    /// <summary>
    /// Does this delayMs after the click's button comes up, or after now with none down: how a caller giving the cursor
    /// back mid-click still lets go of ctrl after the button rather than before it. A second request replaces the first.
    /// </summary>
    public static void AfterPress(int delayMs, Action act)
    {
        _afterPress = act;
        _afterPressMs = Math.Max(0, delayMs);
        _afterPressAt = _pressUpAt == 0 ? Environment.TickCount64 + _afterPressMs : 0;
    }

    /// <summary>Brings a click's button up when its hold is over, then runs what was to follow it. Called every tick.</summary>
    public static void Step()
    {
        var now = Environment.TickCount64;

        if (_pressUpAt != 0 && now >= _pressUpAt)
        {
            Up(_pressRight);
            _pressUpAt = 0;

            if (_afterPress != null)
                _afterPressAt = now + _afterPressMs;
        }

        if (_afterPress != null && _pressUpAt == 0 && _afterPressAt != 0 && now >= _afterPressAt)
        {
            var act = _afterPress;
            _afterPress = null;
            _afterPressAt = 0;
            act();
        }
    }

    /// <summary>Brings a click's button up now and runs what was to follow it, for a stop or an unload.</summary>
    public static void EndPress()
    {
        if (_pressUpAt != 0)
        {
            Up(_pressRight);
            _pressUpAt = 0;
        }

        if (_afterPress != null)
        {
            var act = _afterPress;
            _afterPress = null;
            _afterPressAt = 0;
            act();
        }
    }

    private static void Up(bool right)
    {
        try
        {
            if (right)
                Input.RightUp();
            else
                Input.LeftUp();
        }
        catch
        {
            // Nothing more to be done; LetGo tries again on the way out.
        }
    }

    /// <summary>
    /// Lets go of every modifier, whatever happened.
    ///
    /// Worth calling on any path out, and deliberately unconditional. A control left down turns
    /// every click the player makes afterwards into a ctrl+click, which in a stash moves things; a
    /// shift left down makes it a whole-stack move; an alt left down is the one that will be worst
    /// to diagnose, because on Windows it also arms the window menu.
    ///
    /// All three go up regardless of what was pressed. The whole point of this method is that it
    /// does not have to know, and releasing something never held is free.
    /// </summary>
    public static void Release(IReadOnlyList<Keys> keep = null)
    {
        // The mouse button too, which this did not do and every caller assumed it did. Abandon, a
        // stale lease and the plugin unloading all come through here; a drag running when any of
        // those happens has to end with the button up. See Hold.
        LetGo();

        var held = _holding;
        _holding = Modifier.None;

        // Keys another holder keeps down - a loiter's ctrl, for the click it waits on - are left alone. Only a gesture
        // ending passes any; every way of giving the cursor up releases everything. See Stroke.KeepHeld.
        bool Kept(Keys key) => keep != null && System.Linq.Enumerable.Contains(keep, key);

        try
        {
            foreach (var key in held)
                if (!Kept(key))
                    Input.KeyUp(key);

            // And the standard three on top, in case something went down by a path that did not
            // record itself. Cheap insurance against the failure that is hardest to notice.
            if (!Kept(Keys.LControlKey))
                Input.KeyUp(Keys.LControlKey);

            if (!Kept(Keys.LShiftKey))
                Input.KeyUp(Keys.LShiftKey);

            if (!Kept(Keys.LMenu))
                Input.KeyUp(Keys.LMenu);
        }
        catch
        {
            // Releasing something never held is harmless; failing to try is not.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}

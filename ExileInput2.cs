using ExileCore2;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2;

/// <summary>
/// One owner for the mouse and keyboard, shared by every plugin that wants them.
///
/// Nothing here is referenced by its consumers. The whole surface goes over PluginBridge in types
/// both sides already have - RectangleF, Vector2, Keys, bool, float - so there is no assembly to
/// share, no DLL to drop in the HUD root, and no version to keep in step. A consumer copies
/// Client/ExileInput2Client.cs into its own source and that is the entire integration.
///
/// That is not merely tidier than passing an interface across. An interface means both sides must
/// load the same assembly, which means a root DLL, which means the two can disagree about its
/// version and fail at a cast rather than at a name. Strings and framework types cannot.
///
/// Every call names its caller, and that name is the claim. A call from anyone who is not the
/// current holder is refused - not queued - because the callers all already cope with a click that
/// did not go through, and none of them cope with one that happened later than they thought.
///
/// If this plugin is not installed, a consumer's GetMethod comes back null and it can drive
/// ExileCore2.Input itself as it always did. Nothing here is load-bearing for anyone.
/// </summary>
public class ExileInput2 : BaseSettingsPlugin<ExileInput2Settings>
{
    private readonly Lease _lease = new();
    private readonly Dictionary<string, MousePointer> _pointers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HeldKeys> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stroke> _strokes = new(StringComparer.OrdinalIgnoreCase);

    public override bool Initialise()
    {
        // The last resort, for the ways out that never reach OnUnload.
        //
        // A lease going stale is covered, and so is the plugin being disabled or reloaded. What is
        // not is the host dying: an unhandled exception taking the process down, or somebody
        // closing it while a modifier is held. A key pressed with SendInput is operating system
        // state and outlives the process that pressed it, so shift stays down afterwards - and
        // every click the player makes then means something else.
        //
        // Neither handler can survive being killed outright, and nothing can. They cover the
        // orderly and the semi-orderly, which is most of it.
        AppDomain.CurrentDomain.ProcessExit += LetGoOfEverything;
        AppDomain.CurrentDomain.UnhandledException += LetGoOfEverything;

        var b = GameController.PluginBridge;

        // Claiming. Take is safe to call every tick rather than once, so a caller need not keep a
        // flag about whether it already holds the cursor.
        b.SaveMethod("ExileInput2.Take", (string who) => Claim(who));
        b.SaveMethod("ExileInput2.Release", (string who) => Give(who));
        b.SaveMethod("ExileInput2.Holder", () => _lease.Holder);

        // Why a caller lost the cursor, as a string so the bridge carries nothing this plugin
        // defines - "" while all is well, otherwise a StopReason name. A caller that never asks
        // simply stops working, which is the safe way round for it to be wrong.
        b.SaveMethod("ExileInput2.Stopped", (string who) => StoppedReason(who));

        // Aiming. A rect is what every caller has, since that is what an element reports.
        b.SaveMethod("ExileInput2.MoveTo",
            (string who, RectangleF rect, bool ctrl, bool shift) => Move(who, rect, Modifier.Of(ctrl, shift)));
        b.SaveMethod("ExileInput2.MoveToPoint",
            (string who, Vector2 client, bool ctrl, bool shift) => MoveTo(who, client, Modifier.Of(ctrl, shift)));

        // The same three, taking the modifiers as a set.
        //
        // Added ALONGSIDE the ctrl/shift pair rather than replacing it, and that is the whole point
        // of doing it this way. The bridge is how other plugins reach this one, and a consumer's
        // copy of the client file is a copy - there is no way to update it from here. Changing the
        // shape of a registered method breaks every caller the instant this plugin reloads, before
        // anybody has had a chance to update anything.
        //
        // So the old names keep working, unchanged, forever if need be. A caller moves to the new
        // ones when it wants a modifier the old shape cannot express, and not before.
        b.SaveMethod("ExileInput2.MoveToKeys",
            (string who, RectangleF rect, Keys[] modifiers) => Move(who, rect, modifiers));
        b.SaveMethod("ExileInput2.MoveToPointKeys",
            (string who, Vector2 client, Keys[] modifiers) => MoveTo(who, client, modifiers));
        b.SaveMethod("ExileInput2.Arrived", (string who) => Arrived(who));
        b.SaveMethod("ExileInput2.Drift", (string who) => Drift(who));
        b.SaveMethod("ExileInput2.Aiming", (string who) => Aiming(who));
        b.SaveMethod("ExileInput2.Expected", (string who) => Expected(who));
        b.SaveMethod("ExileInput2.Forget", (string who) => Forget(who));

        // Per caller, over the shared default. The tolerance that suits a stash tab is not the one
        // that suits an inventory cell, and a single figure would be wrong for one of them - so the
        // setting is the default and a caller that knows better says so.
        b.SaveMethod("ExileInput2.SetTolerance", (string who, int px) => SetTolerance(who, px));

        // Pressing.
        b.SaveMethod("ExileInput2.Click",
            (string who, bool ctrl, bool shift, bool right) => Click(who, Modifier.Of(ctrl, shift), right));
        b.SaveMethod("ExileInput2.ClickKeys",
            (string who, Keys[] modifiers, bool right) => Click(who, modifiers, right));

        b.SaveMethod("ExileInput2.Tap", (string who, Keys key) => Tap(who, key));
        b.SaveMethod("ExileInput2.Hold", (string who, Keys key) => Hold(who, key));
        b.SaveMethod("ExileInput2.Let", (string who, Keys key) => Let(who, key));
        b.SaveMethod("ExileInput2.ReleaseKeys", (string who) => LetGo(who));

        return true;
    }

    /// <summary>
    /// Takes the cursor, if the game is in a state where driving it is reasonable at all.
    ///
    /// A caller that was stopped stays stopped until it says so out loud by releasing. Without
    /// that, a machine polling Take every tick resumes the instant the player's hand comes to rest,
    /// and the player has to fight it rather than simply take the mouse back once.
    /// </summary>
    private bool Claim(string who)
    {
        if (Stop(who) != StopReason.None)
            return false;

        if (Blocked() is var blocked and not StopReason.None)
        {
            PointerFor(who).Stop(blocked);
            return false;
        }

        // Whether this is a new claim or the same caller polling, because Take is documented as
        // safe to call every tick and a pointer wiped every tick would notice nothing at all.
        var already = _lease.Held(who);

        if (!_lease.Take(who))
            return false;

        // A new claim is a new session, and it starts with no memory of where the cursor has been.
        // Without this the caller inherits the last session's sends: the player repositions between
        // one run and the next - which is the normal thing to do - and the first step of the new run
        // finds the cursor nowhere the ring remembers, calls it interference and stops. From the
        // outside that is a plugin that refuses every other request for no stated reason.
        if (!already)
            PointerFor(who).Fresh();

        return true;
    }

    /// <summary>Whether the game is in a state where synthetic input has any business happening.</summary>
    private StopReason Blocked()
    {
        try
        {
            if (!GameController.Window.IsForeground())
                return StopReason.GameLostFocus;

            // Chat, a stash search box, anything with a caret in it. Keys sent while one of those
            // has focus are typed into it, and a click is the player dismissing it.
            if (GameController.IngameState.FocusedInputElement != null)
                return StopReason.GameIsTakingInput;
        }
        catch
        {
            // A read that throws is the game mid-transition, which is not a moment to be clicking
            // in either.
            return StopReason.GameLostFocus;
        }

        return StopReason.None;
    }

    private StopReason Stop(string who) =>
        _pointers.TryGetValue(who, out var pointer) ? pointer.Stopped : StopReason.None;

    private static string StoppedName(StopReason reason) =>
        reason == StopReason.None ? "" : reason.ToString();

    private string StoppedReason(string who) => StoppedName(Stop(who));

    /// <summary>The pointer belonging to one caller, made on first use.</summary>
    private MousePointer PointerFor(string who)
    {
        if (!_pointers.TryGetValue(who, out var pointer))
            _pointers[who] = pointer = new MousePointer(GameController);

        pointer.SpreadPercent = Settings.SpreadPercent.Value;
        pointer.MinTravelMs = Settings.MinTravelMs.Value;
        pointer.TravelMsPerRootPx = Settings.TravelMsPerRootPx.Value;
        pointer.ArcPercent = Settings.ArcPercent.Value;
        pointer.StayPut = Settings.StayPut.Value;
        pointer.OnModifier ??= key => KeysFor(who).Hold(key);
        pointer.TolerancePx = _tolerances.TryGetValue(who, out var px) ? px : Settings.TolerancePx.Value;
        pointer.StopTolerancePx = Settings.StopTolerancePx.Value;
        pointer.ArriveTimeoutMs = Settings.ArriveTimeoutMs.Value;
        return pointer;
    }

    private HeldKeys KeysFor(string who)
    {
        if (!_keys.TryGetValue(who, out var keys))
            _keys[who] = keys = new HeldKeys();

        return keys;
    }

    private bool Move(string who, RectangleF rect, Keys[] modifiers)
    {
        if (!_lease.Held(who))
            return false;

        var pointer = PointerFor(who);
        pointer.Aim(rect, modifiers ?? Modifier.None);
        return true;
    }

    private bool MoveTo(string who, Vector2 client, Keys[] modifiers)
    {
        if (!_lease.Held(who))
            return false;

        PointerFor(who).Place(client, modifiers ?? Modifier.None);
        return true;
    }

    private bool Arrived(string who) => _lease.Held(who) && PointerFor(who).Arrived();

    private float Drift(string who) => _lease.Held(who) ? PointerFor(who).Drift() : 0f;

    private bool Aiming(string who) => _lease.Held(who) && PointerFor(who).Awaiting;

    private Vector2 Expected(string who) => _lease.Held(who) ? PointerFor(who).Expected : Vector2.Zero;

    private bool Forget(string who)
    {
        if (!_lease.Held(who))
            return false;

        PointerFor(who).Forget();
        return true;
    }

    private bool SetTolerance(string who, int px)
    {
        if (!_lease.Held(who) || px <= 0)
            return false;

        _tolerances[who] = px;
        return true;
    }

    /// <summary>What each caller asked for, since the shared default cannot suit every target.</summary>
    private readonly Dictionary<string, int> _tolerances = new(StringComparer.OrdinalIgnoreCase);

    private bool Click(string who, Keys[] modifiers, bool right)
    {
        modifiers ??= Modifier.None;

        if (!_lease.Held(who))
            return false;

        var pointer = PointerFor(who);

        // Where the approach pressed the modifier and it is still verifiably down, the press goes
        // straight in: it has had the whole journey to register, so there is nothing left to wait
        // for. That is the point of pressing it in flight.
        //
        // Both halves of that condition matter, and the first version had neither right. It asked
        // the pointer whether it had pressed anything - which stayed true after the keys were
        // released, so a later click with no move in between took this path with nothing held - and
        // it guarded that with "Readable == true", which is null until Buttons.Send has run at
        // least once. Take the fast path from the start and Send never runs, so the guard never
        // fires. The result was a bare left click on Faustus, which is the exact thing all of this
        // exists to prevent.
        //
        // So it fails closed now. Anything short of "the key is down and we can see it" goes the
        // long way, which confirms or refuses rather than assuming.
        var stroke = StrokeFor(who);
        stroke.HoldMs = Settings.ModifierHoldMs.Value;

        if (Gesture.StraightIn(modifiers, pointer.ModifiersReady, Buttons.IsDown))
            return Pressed(pointer, stroke.StraightIn(right));

        // Whatever the JOURNEY pressed comes up first, or the stroke presses control on top of a
        // control already down and releases it once at the end - leaving the second held.
        //
        // Gated on the journey having pressed something, and that gate is not a nicety. Without it
        // this released every key the caller was holding deliberately, on every click, before
        // sending it - and the caller's keys live in the same bag because Hold puts them there.
        //
        // For a caller holding shift to keep a currency selected between applications, that is the
        // whole craft: shift came up, the selection ended with it, and the click that followed a
        // microsecond later was a bare left click on the item being crafted. Which picks it up.
        //
        // A click asking for no modifiers has nothing to clean up unless the approach pressed
        // something, and ModifiersReady is exactly the flag that says whether it did.
        if (Gesture.ReleaseBeforePush(pointer.ModifiersReady))
        {
            KeysFor(who).ReleaseAll();
            pointer.ModifiersLetGo();
        }

        return Pressed(pointer, stroke.Push(modifiers, right));
    }

    /// <summary>
    /// Tells the pointer a click went out, and passes the answer straight through.
    ///
    /// So that aiming at the same place again is understood as doing the thing twice rather than as
    /// retrying something that did not work - the first stays where it is, the second must move.
    /// </summary>
    private static bool Pressed(MousePointer pointer, bool clicked)
    {
        if (clicked)
            pointer.Clicked();

        return clicked;
    }

    /// <summary>The gesture in progress for a caller, made on first use like everything else here.</summary>
    private Stroke StrokeFor(string who)
    {
        if (!_strokes.TryGetValue(who, out var stroke))
        {
            _strokes[who] = stroke = new Stroke
            {
                // The keys coming up is the end of the gesture, so it is also the moment the rest
                // of the bookkeeping about them stops being true. Wired here rather than inside
                // the stroke because a stroke that reached into the pointer would be two things.
                OnLetGo = () =>
                {
                    KeysFor(who).ReleaseAll();
                    PointerFor(who).ModifiersLetGo();
                },
            };
        }

        return stroke;
    }

    private bool Tap(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Tap(key);
        return true;
    }

    private bool Hold(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Hold(key);
        return true;
    }

    private bool Let(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Let(key);
        return true;
    }

    private bool LetGo(string who)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).ReleaseAll();
        return true;
    }

    /// <summary>
    /// Hands the cursor back, letting go of anything that caller was holding first.
    ///
    /// Releasing the claim without releasing the keys would leave a control down owned by nobody,
    /// which is the exact fault the tracker exists to prevent - and the next plugin to take the
    /// cursor would inherit it without any way of knowing.
    /// </summary>
    private bool Give(string who)
    {
        // Clearing the stop is the whole reason a stopped caller calls this, and by then the lease
        // has already been taken off it - so the acknowledgement happens whether or not it still
        // holds anything.
        var stopped = Stop(who) != StopReason.None;

        if (stopped)
            PointerFor(who).Resume();

        if (!_lease.Held(who))
            return stopped;

        KeysFor(who).ReleaseAll();
        Buttons.Release();
        _lease.Release(who);
        return true;
    }

    public override void Tick()
    {
        _lease.Expiry = TimeSpan.FromSeconds(Settings.ClaimExpirySeconds.Value);

        // The journey, advanced for whoever holds the cursor. One place doing this is the reason
        // the plugin exists rather than a library: a passive helper would need every consumer to
        // remember to step its own movement, and the one that forgot would simply never arrive.
        if (_lease.Holder is { } driving)
        {
            var pointer = PointerFor(driving);

            // The window going away is as good a reason to stop as the player taking the mouse
            // back: a click sent while the game is not in front lands in whatever is.
            if (Blocked() is var blocked and not StopReason.None)
                pointer.Stop(blocked);

            pointer.Step();

            if (pointer.Stopped != StopReason.None)
            {
                Abandon(driving, pointer.Stopped);
            }
            else
            {
                // And the gesture, which is the half of the tick that used to be a Thread.Sleep. A
                // modifier held around a click comes up here, a frame or four after it went down,
                // rather than by stopping the overlay until it was time.
                StrokeFor(driving).Step();
            }
        }

        // A claim that has gone stale is one whose owner is not coming back, so whatever it was
        // holding comes up with it. Asked here rather than inside the lease because releasing keys
        // is not the lease's business, and a lease that reached into the keyboard would be two
        // things at once.
        if (_lease.Holder == null && _held != null)
        {
            // Whatever was mid-gesture is not going to finish, so it is dropped rather than left
            // holding a modifier that turns the player's next click into something else.
            StrokeFor(_held).Abandon();
            KeysFor(_held).ReleaseAll();
            Buttons.Release();
            _held = null;
        }
        else if (_lease.Holder != null)
        {
            _held = _lease.Holder;
        }
    }

    /// <summary>
    /// Takes the cursor off a caller that has been stopped, and puts down everything it was holding.
    ///
    /// A modifier left down here would turn the player's own next click into something else, which
    /// is the one outcome worse than the automation having misbehaved in the first place.
    /// </summary>
    private void Abandon(string who, StopReason reason)
    {
        StrokeFor(who).Abandon();
        KeysFor(who).ReleaseAll();
        Buttons.Release();
        _lease.Release(who);
        _held = null;

        LogMessage($"ExileInput2: {who} stopped, {Describe(reason)}.");
    }

    private static string Describe(StopReason reason) => reason switch
    {
        StopReason.PlayerMovedTheMouse => "the mouse was moved",
        StopReason.MoveNeverLanded => "the cursor never reached where it was sent",
        StopReason.GameLostFocus => "the game is not in the foreground",
        StopReason.GameIsTakingInput => "the game is taking typed input",
        _ => "no reason given",
    };

    /// <summary>Who held the cursor last tick, so an expiry can be noticed and tidied after.</summary>
    private string _held;

    /// <summary>Brings the modifiers up on the way out, whatever the way out turned out to be.</summary>
    private static void LetGoOfEverything(object sender, EventArgs e)
    {
        try
        {
            HeldKeys.ReleaseEverything();
        }
        catch
        {
            // Nothing can be done about it from here, and throwing on the way out of a process
            // helps nobody.
        }
    }

    public override void OnUnload()
    {
        AppDomain.CurrentDomain.ProcessExit -= LetGoOfEverything;
        AppDomain.CurrentDomain.UnhandledException -= LetGoOfEverything;

        foreach (var stroke in _strokes.Values)
            stroke.Abandon();

        foreach (var keys in _keys.Values)
            keys.ReleaseAll();

        HeldKeys.ReleaseEverything();
        _lease.Break();
    }
}

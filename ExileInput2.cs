using ExileCore2;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>The seed the noise is shuffled by now. See Tick.</summary>
    private int _seedInUse;

    /// <summary>
    /// The version of what this plugin puts on the bridge, as ExileInput2.Version answers it: raised whenever a method is
    /// added or changes shape, so a consumer can tell an install too old for it from one that is missing. An install
    /// from before the version existed registers no ExileInput2.Version at all, which a consumer reads as 0.
    ///
    /// 1: the first with a version.
    /// 2: ExileInput2.LastPressDownAt; letters, digits and Space held or tapped for a caller, and the player's keys pressed
    /// again after a key block, go out with their own scan codes. Under 1 a held W did not walk in the game, and a key
    /// pressed again after a block stayed held after the player let it go.
    /// </summary>
    public const int BridgeVersion = 2;

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

        // This install's seed, drawn the first time and saved at once, so a crash before the next save does not draw
        // another. See ExileInput2Settings.Seed.
        if (!Settings.SeedDrawn)
        {
            Settings.Seed = ExileInput2Settings.NewSeed();
            Settings.SeedDrawn = true;
            _SaveSettings();
        }

        // Shipped changes to saved settings, once, saved at once like the seed. See ExileInput2Settings.Migrate.
        if (Settings.SettingsVersion < ExileInput2Settings.CurrentSettingsVersion)
        {
            ExileInput2Settings.Migrate(Settings);
            _SaveSettings();
        }

        PerlinNoise.UseSeed(Settings.Seed);
        _seedInUse = Settings.Seed;

        var b = GameController.PluginBridge;

        StartMover();

        // Which methods a consumer can count on. See BridgeVersion.
        b.SaveMethod("ExileInput2.Version", () => BridgeVersion);

        // Claiming. Take is safe to call every tick rather than once, so a caller need not keep a
        // flag about whether it already holds the cursor.
        b.SaveMethod("ExileInput2.Take", (string who) => Gated(() => Claim(who)));
        b.SaveMethod("ExileInput2.Release", (string who) => Gated(() => Give(who)));

        // Takes the cursor from a holder of lower priority, which is stopped with TakenOver. See TakeWithPriority.
        b.SaveMethod("ExileInput2.TakeWithPriority", (string who, int priority) => Gated(() => TakeWithPriority(who, priority)));
        b.SaveMethod("ExileInput2.Holder", () => Gated(() => _lease.Holder));

        // Why a caller lost the cursor, as a string so the bridge carries nothing this plugin
        // defines - "" while all is well, otherwise a StopReason name. A caller that never asks
        // simply stops working, which is the safe way round for it to be wrong.
        b.SaveMethod("ExileInput2.Stopped", (string who) => Gated(() => StoppedReason(who)));

        // What the last stop saw, for a caller to record; never drawn here. See MousePointer.StopDetail.
        b.SaveMethod("ExileInput2.StopDetail", (string who) => Gated(() => PointerFor(who).StopDetail));
        // Panels a caller says take no typed input, focus inside which does not stop it. See AllowFocusUnder.
        b.SaveMethod("ExileInput2.AllowFocusUnder", (string who, long[] roots) => Gated(() => AllowFocusUnder(who, roots)));

        // Aiming. A rect is what every caller has, since that is what an element reports.
        b.SaveMethod("ExileInput2.MoveTo",
            (string who, RectangleF rect, bool ctrl, bool shift) => Gated(() => Move(who, rect, Modifier.Of(ctrl, shift))));
        b.SaveMethod("ExileInput2.MoveToPoint",
            (string who, Vector2 client, bool ctrl, bool shift) => Gated(() => MoveTo(who, client, Modifier.Of(ctrl, shift))));

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
            (string who, RectangleF rect, Keys[] modifiers) => Gated(() => Move(who, rect, modifiers)));
        b.SaveMethod("ExileInput2.MoveToPointKeys",
            (string who, Vector2 client, Keys[] modifiers) => Gated(() => MoveTo(who, client, modifiers)));
        // A plain click armed to go out the moment the cursor is put on its target, from the input thread, without
        // waiting for the caller's next frame; cancelled, and whether it went out, read once. See ClickOnArrival.
        b.SaveMethod("ExileInput2.ClickOnArrival", (string who, bool right) => Gated(() => ClickOnArrival(who, right)));
        b.SaveMethod("ExileInput2.CancelClickOnArrival", (string who) => Gated(() => CancelClickOnArrival(who)));
        b.SaveMethod("ExileInput2.ClickedOnArrival", (string who) => Gated(() => ClickedOnArrival(who)));
        // When the last click's button went down, a Stopwatch timestamp, whoever sent it. See Buttons.LastPressDownAt.
        b.SaveMethod("ExileInput2.LastPressDownAt", () => Gated(() => Buttons.LastPressDownAt));
        b.SaveMethod("ExileInput2.Arrived", (string who) => Gated(() => Arrived(who)));
        // The cursor taken to a point and kept moving round it until the next move. See MousePointer.Loiter.
        b.SaveMethod("ExileInput2.Loiter", (string who, Vector2 centre, float radius, int forMs, Keys[] modifiers) => Gated(() =>
        {
            if (!_lease.Held(who))
                return false;

            // Any held from a loiter before, let go unless these are the same. See _loiterKeys.
            var keysCarried = EndLoiterKeys(who, modifiers ?? Modifier.None);
            var pointer = PointerFor(who);

            // **Along the plan, when its next click is where the loiter goes.** A loiter's own move to its point broke
            // off the curve the plan had drawn there - after the third load into the reforging bench, a sharp turn towards
            // the reforged tablet's slot while the game had not yet put the tablet there - and the click on the tablet
            // was planned afresh. So the planned journey is taken, the loiter goes round where it lands, and the move
            // for that click, when it comes, is that planned click. See _loiterOnPlanned.
            if (_plans.TryGetValue(who, out var plan) && plan.Next < plan.Items.Count &&
                plan.Items[plan.Next] is { PointGiven: false } next && MousePointer.Holds(next.Rect, centre))
            {
                var carried = keysCarried | CarryModifiers(who, modifiers);

                pointer.AimAlong(next.Rect, next.To, modifiers ?? Modifier.None, next.Drift,
                    next.LeadMs > 0 ? next.LeadMs : HumanTiming.Ms(Settings.UsedModifierTiming), next.MinMs, next.MsPerRootPx,
                    next.Control1, next.Control2, next.Shape);
                plan.Current = next;
                plan.Next++;

                if (carried)
                    pointer.ModifiersAlreadyDown();

                pointer.LoiterOnArrival(next.To, radius, forMs);
                _loiterOnPlanned[who] = next.Rect;
            }
            else
            {
                _loiterOnPlanned.Remove(who);
                pointer.Loiter(centre, radius, forMs, modifiers);
            }

            if (Modifier.Any(modifiers))
                _loiterKeys[who] = modifiers;

            return true;
        }));
        b.SaveMethod("ExileInput2.StopLoitering", (string who) => Gated(() =>
        {
            PointerFor(who).StopLoitering();
            return true;
        }));
        b.SaveMethod("ExileInput2.Loitering", (string who) => Gated(() => PointerFor(who).Loitering));

        // The caller's last move: its wait for a click's button, and its own length, in milliseconds; then its swoop's
        // stretch of time (1 for none) and how far it overshot, in pixels (0 for none). See MousePointer.
        b.SaveMethod("ExileInput2.LastMoveTimes",
            (string who) => Gated(() => new[]
            {
                (float)PointerFor(who).LastMoveDelayMs, PointerFor(who).LastMoveTravelMs, PointerFor(who).LastMoveShape.DurationScale,
                PointerFor(who).LastMoveShape.Overshoot.Length(),
            }));

        // How a caller's planned queues ended, newest last, for its dump. See PlanEndsSaid.
        b.SaveMethod("ExileInput2.PlanEndsSaid", (string who) => Gated(() => PlanEndsSaid(who)));

        // A queue of clicks asked for ahead of time, drawn on screen when show is set. Empty points drop it. See PlanClicks.
        b.SaveMethod("ExileInput2.PlanClicks",
            (string who, RectangleF[] rects, Vector2[] points, bool[] rights, Keys[][] modifiers, int[] shortestMoveMs, bool show) =>
                Gated(() => PlanClicks(who, rects, points, rights, modifiers, shortestMoveMs, show)));
        // Clicks added to the end of a caller's queue rather than the queue planned again. See ExtendPlan.
        b.SaveMethod("ExileInput2.ExtendPlan",
            (string who, RectangleF[] rects, Vector2[] points, bool[] rights, Keys[][] modifiers, int[] shortestMoveMs) =>
                Gated(() => ExtendPlan(who, rects, points, rights, modifiers, shortestMoveMs)));
        b.SaveMethod("ExileInput2.Drift", (string who) => Gated(() => Drift(who)));
        b.SaveMethod("ExileInput2.Aiming", (string who) => Gated(() => Aiming(who)));
        b.SaveMethod("ExileInput2.Expected", (string who) => Gated(() => Expected(who)));
        b.SaveMethod("ExileInput2.Forget", (string who) => Gated(() => Forget(who)));

        // Per caller, over the shared default. The tolerance that suits a stash tab is not the one
        // that suits an inventory cell, and a single figure would be wrong for one of them - so the
        // setting is the default and a caller that knows better says so.
        b.SaveMethod("ExileInput2.SetTolerance", (string who, int px) => Gated(() => SetTolerance(who, px)));
        b.SaveMethod("ExileInput2.SetShortestMove", (string who, int ms) => Gated(() => SetShortestMove(who, ms)));
        b.SaveMethod("ExileInput2.SetMoveTimePerRootPixel", (string who, int ms) => Gated(() => SetMoveTimePerRootPixel(who, ms)));

        // For one move and click that must land whatever the player's hand does: the player's mouse stops nothing for
        // this long, and with block it is dropped as well. See IgnorePlayerMouse.
        b.SaveMethod("ExileInput2.IgnorePlayerMouse", (string who, int ms, bool block) => Gated(() => IgnorePlayerMouse(who, ms, block)));

        // For this claim, the player's own key presses - any but these - stop the caller, as moving the mouse does.
        // See StopOnPlayerKeys.
        b.SaveMethod("ExileInput2.StopOnPlayerKeys", (string who, Keys[] except) => Gated(() => StopOnPlayerKeys(who, except)));

        // For a pickup that must not be walked away from: the player's held keys let go and their key presses dropped for
        // this long, 0 to end it, and optionally the held keys pressed again at the end; a press of cancelKey ends it
        // early. See BlockPlayerKeys.
        b.SaveMethod("ExileInput2.BlockPlayerKeys",
            (string who, int ms, bool restoreHeldKeys, Keys cancelKey) => Gated(() => BlockPlayerKeys(who, ms, restoreHeldKeys, cancelKey)));

        // Whether the player ended the last key block with its cancel key, cleared by the read. See KeyWatch.
        b.SaveMethod("ExileInput2.KeyBlockCancelled", (string who) => Gated(() => !string.IsNullOrWhiteSpace(who) && KeyWatch.TakeBlockCancelled()));

        // Pressing.
        b.SaveMethod("ExileInput2.Click",
            (string who, bool ctrl, bool shift, bool right) => Gated(() => Click(who, Modifier.Of(ctrl, shift), right)));
        b.SaveMethod("ExileInput2.ClickKeys",
            (string who, Keys[] modifiers, bool right) => Gated(() => Click(who, modifiers, right)));

        b.SaveMethod("ExileInput2.Drag",
            (string who, Vector2 from, Vector2 to) => Gated(() => Drag(who, from, to)));
        b.SaveMethod("ExileInput2.Dragging", (string who) => Gated(() => Dragging(who)));
        b.SaveMethod("ExileInput2.DragMissed", (string who) => Gated(() => Missed(who)));
        b.SaveMethod("ExileInput2.DragTrace", (string who) => Gated(() => Trace(who)));

        b.SaveMethod("ExileInput2.Tap", (string who, Keys key) => Gated(() => Tap(who, key)));
        b.SaveMethod("ExileInput2.Hold", (string who, Keys key) => Gated(() => Hold(who, key)));
        b.SaveMethod("ExileInput2.Let", (string who, Keys key) => Gated(() => Let(who, key)));
        b.SaveMethod("ExileInput2.ReleaseKeys", (string who) => Gated(() => LetGo(who)));

        return true;
    }

    /// <summary>
    /// Takes the cursor, if the game is in a state where driving it is reasonable at all.
    ///
    /// A caller that was stopped stays stopped until it says so out loud by releasing. Without
    /// that, a machine polling Take every tick resumes the instant the player's hand comes to rest,
    /// and the player has to fight it rather than simply take the mouse back once.
    /// </summary>
    /// <summary>
    /// Claims the cursor at a priority. A holder that took it at a lower priority is stopped with TakenOver, as if the
    /// player had moved the mouse, and must release before it can take it again; one at the same or higher priority keeps
    /// it and this is refused. An ordinary Take is priority 0. For a caller whose one move cannot wait, such as picking
    /// an item up before somebody else does.
    /// </summary>
    private bool TakeWithPriority(string who, int priority)
    {
        if (_lease.Holder is { } holder && !string.Equals(holder, who, StringComparison.OrdinalIgnoreCase))
        {
            if (priority <= _lease.HolderPriority)
                return false;

            PointerFor(holder).Stop(StopReason.TakenOver);
            Abandon(holder, StopReason.TakenOver);
        }

        return Claim(who, priority);
    }

    private bool Claim(string who, int priority = 0)
    {
        if (Stop(who) != StopReason.None)
            return false;

        if (Blocked(who) is var blocked and not StopReason.None)
        {
            PointerFor(who).Stop(blocked, _blockedDetail);
            return false;
        }

        // Whether this is a new claim or the same caller polling, because Take is documented as
        // safe to call every tick and a pointer wiped every tick would notice nothing at all.
        var already = _lease.Held(who);

        if (!_lease.Take(who, priority))
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

    /// <summary>
    /// Whether the game is in a state where synthetic input has any business happening, for this caller: focus inside a
    /// panel it named with AllowFocusUnder does not count as typed input.
    /// </summary>
    private StopReason Blocked(string who)
    {
        try
        {
            if (!GameController.Window.IsForeground())
                return StopReason.GameLostFocus;

            // Chat, a stash search box, anything with a caret in it. Keys sent while one of those
            // has focus are typed into it, and a click is the player dismissing it.
            if (GameController.IngameState.FocusedInputElement is { } focused && !FocusAllowed(who, focused))
            {
                _blockedDetail = FocusedSaid(focused);
                return StopReason.GameIsTakingInput;
            }
        }
        catch
        {
            // A read that throws is the game mid-transition, which is not a moment to be clicking
            // in either.
            return StopReason.GameLostFocus;
        }

        return StopReason.None;
    }

    /// <summary>
    /// Sets the panels, by element address, inside which focus is not typed input for this caller; none clears them. For
    /// a panel the game focuses part of with no text to type into: the reforging bench reports the box around its reforge
    /// button as taking typed input after each reforge, with chat closed and no caret (2026-10-12 12:33:48), and every
    /// reforge stopped the run for a moment. Focus anywhere else still stops it.
    /// </summary>
    private bool AllowFocusUnder(string who, long[] roots)
    {
        if (roots is { Length: > 0 })
            _focusAllowedUnder[who] = roots;
        else
            _focusAllowedUnder.Remove(who);

        return true;
    }

    /// <summary>Each caller's panels inside which focus is not typed input. See AllowFocusUnder.</summary>
    private readonly Dictionary<string, long[]> _focusAllowedUnder = new();

    /// <summary>Whether the focused element is, or lies inside, a panel this caller named. See AllowFocusUnder.</summary>
    private bool FocusAllowed(string who, ExileCore2.PoEMemory.Element focused)
    {
        if (who == null || !_focusAllowedUnder.TryGetValue(who, out var roots))
            return false;

        var at = focused;

        // Bounded: the element tree is shallow, and a cycle in a read gone wrong must not hang the tick.
        for (var depth = 0; at != null && depth < 64; depth++)
        {
            if (Array.IndexOf(roots, at.Address) >= 0)
                return true;

            at = at.Parent;
        }

        return false;
    }

    /// <summary>
    /// The modifiers each caller's loiter holds down for the click it waits on. Taken over by the next move asking for the
    /// same ones, as a click's are; let go by a move asking for others, by the loiter ending on its own - its time up, or
    /// StopLoitering - and, as every held key is, by the cursor being given up or the caller stopped. See EndLoiterKeys.
    /// </summary>
    private readonly Dictionary<string, Keys[]> _loiterKeys = new();

    /// <summary>
    /// Each caller's planned click a loiter travelled to along the plan, by its target: the move asked for it next is that
    /// click, made from wherever the loiter has got to. See the Loiter bridge method and Move.
    /// </summary>
    private readonly Dictionary<string, RectangleF> _loiterOnPlanned = new();

    /// <summary>
    /// Ends what a loiter holds as the next thing asks for these modifiers: true when they are the same, and still down
    /// for that move to keep; else they are let go and false. False with nothing held.
    /// </summary>
    private bool EndLoiterKeys(string who, Keys[] next)
    {
        if (who == null || !_loiterKeys.Remove(who, out var held))
            return false;

        if (held.Length == next.Length && held.All(k => Array.IndexOf(next, k) >= 0))
            return true;

        KeysFor(who).ReleaseAll();
        PointerFor(who).ModifiersLetGo();
        return false;
    }

    /// <summary>What Blocked last saw, for the stop's detail: the element taking typed input. See FocusedSaid.</summary>
    private string _blockedDetail = "";

    /// <summary>
    /// The element taking typed input, in one line, for a stop's detail: its address, place, size, visibility, text, and
    /// its parent's address and text - enough to tell chat from a field some panel focuses for a moment.
    /// </summary>
    private static string FocusedSaid(ExileCore2.PoEMemory.Element focused)
    {
        try
        {
            var rect = focused.GetClientRectCache;
            var parent = focused.Parent;

            return $"the game reported typed input going to element {focused.Address:X} at ({rect.X:0},{rect.Y:0} " +
                   $"{rect.Width:0}x{rect.Height:0}), visible {focused.IsVisible}, text \"{focused.Text}\", under " +
                   (parent == null ? "no parent" : $"{parent.Address:X}, text \"{parent.Text}\"");
        }
        catch
        {
            return "the game reported typed input going to an element that could not be read";
        }
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

        // A journey waits while a click's button is still down. See Buttons.Press.
        pointer.MoveDelayMs ??= () => Buttons.MsUntilPressUp;
        pointer.ModifierLeadMs ??= () => HumanTiming.Ms(Settings.UsedModifierTiming);

        // The feel settings as the seed moves them; the rest as set. See ExileInput2Settings.Varied.
        pointer.SpreadPercent = Settings.UsedSpreadPercent;
        // A caller's own shortest move in place of the menu's, where it asked for one, moved by the seed as the menu's
        // is. See SetShortestMove.
        pointer.MinTravelMs = _shortestMoves.TryGetValue(who, out var shortest)
            ? shortest * ExileInput2Settings.SeedFactor(Settings.Seed, 1)
            : Settings.UsedMinTravelMs;
        pointer.TravelMsPerRootPx = _moveTimesPerRootPixel.TryGetValue(who, out var perRootPixel)
            ? perRootPixel * ExileInput2Settings.SeedFactor(Settings.Seed, 2)
            : Settings.UsedTravelMsPerRootPx;
        pointer.ArcPercent = Settings.UsedArcPercent;
        pointer.MostMoveTimePerFrameMs = Settings.MostMoveTimePerFrameMs.Value;
        pointer.StayPut = Settings.StayPut.Value;
        pointer.OnModifier ??= key => KeysFor(who).Hold(key);
        pointer.TolerancePx = _tolerances.TryGetValue(who, out var px) ? px : Settings.TolerancePx.Value;
        pointer.StopTolerancePx = Settings.StopTolerancePx.Value;
        pointer.ArriveTimeoutMs = Settings.ArriveTimeoutMs.Value;

        // **A drag is not a human-looking journey and must not be one.**
        //
        // The wander that makes a click look natural - a spread around the target, a fifteen per
        // cent arc, twelve pixels of arrival tolerance - is fatal on a scroll bar twenty seven
        // pixels wide: the press lands beside the thumb as often as on it, and the arc carries the
        // cursor off the bar halfway through the pull. Straight, tight, and on the point, for as
        // long as the drag lasts.
        if (_drags.ContainsKey(who))
        {
            pointer.SpreadPercent = 0f;
            pointer.ArcPercent = 0f;
            pointer.TolerancePx = 2;
        }

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
        var carried = EndLoiterKeys(who, modifiers ?? Modifier.None) | CarryModifiers(who, modifiers);

        // **The planned click a loiter has already travelled to**: from wherever the loiter has got to, onto the target,
        // with the plan going on from the click after it. See _loiterOnPlanned.
        if (_loiterOnPlanned.Remove(who, out var loitered) && loitered == rect &&
            _plans.TryGetValue(who, out var held) && held.Current is { } current && current.Rect == rect)
        {
            pointer.Aim(rect, modifiers ?? Modifier.None);

            if (carried)
                pointer.ModifiersAlreadyDown();

            return true;
        }

        // The queue's next click, where it was planned by its rectangle: the point, journey and lead planned for it. Any
        // other rectangle drops the queue.
        if (_plans.TryGetValue(who, out var plan) && plan.Next < plan.Items.Count && !plan.Items[plan.Next].PointGiven &&
            plan.Items[plan.Next].Rect == rect)
        {
            var planned = plan.Items[plan.Next];

            pointer.AimAlong(rect, planned.To, modifiers ?? Modifier.None, planned.Drift,
                planned.LeadMs > 0 ? planned.LeadMs : HumanTiming.Ms(Settings.UsedModifierTiming), planned.MinMs, planned.MsPerRootPx,
                planned.Control1, planned.Control2, planned.Shape);
            plan.Current = planned;
            plan.Next++;

            if (carried)
                pointer.ModifiersAlreadyDown();

            return true;
        }

        if (_plans.Remove(who, out var dropped))
            NotePlanEnd(who, dropped, dropped.Next < dropped.Items.Count
                ? $"dropped: a move to the rectangle ({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) where the next planned " +
                  (dropped.Items[dropped.Next].PointGiven
                      ? $"was the point ({dropped.Items[dropped.Next].To.X:0},{dropped.Items[dropped.Next].To.Y:0})"
                      : $"was ({dropped.Items[dropped.Next].Rect.X:0},{dropped.Items[dropped.Next].Rect.Y:0} " +
                        $"{dropped.Items[dropped.Next].Rect.Width:0}x{dropped.Items[dropped.Next].Rect.Height:0})")
                : "used up: a move after its last click");

        pointer.Aim(rect, modifiers ?? Modifier.None);

        if (carried)
            pointer.ModifiersAlreadyDown();

        return true;
    }

    /// <summary>
    /// Whether a move asking for these modifiers takes them over from the click before it, still holding them: then the
    /// click does not let them go and the move does not press them again. See Stroke.HandOver.
    /// </summary>
    private bool CarryModifiers(string who, Keys[] modifiers)
    {
        var stroke = StrokeFor(who);

        if (modifiers == null || !stroke.HoldingAfterClick(modifiers))
            return false;

        stroke.HandOver();
        return true;
    }

    /// <summary>
    /// Presses at one point, travels to another, and lets go there.
    ///
    /// **One method rather than a press and a release the caller pairs up**, and that is the whole
    /// safety argument. A consumer that could hold the button would eventually crash, hot reload or
    /// simply forget between the two calls, and what it would leave behind is a left button held
    /// down in a live game - the worst thing anything here can do. Owning the gesture means the
    /// button is only ever down inside a tick loop that releases it on every way out: the player
    /// touching the mouse, the window losing focus, the lease going stale, the plugin unloading.
    ///
    /// Written for a scroll bar, which is what a drag is actually needed for. The combinations
    /// window shows a handful of a remnant's nineteen options and the one worth taking is routinely
    /// out of sight; its thumb is the only way to bring it into view without a mouse wheel, which
    /// this plugin has no way to send.
    ///
    /// The journey to each end is the ordinary one - the same wandering approach and the same
    /// arrival check - so a drag looks like two moves with a button held between them rather than
    /// like a machine dragging something.
    /// </summary>
    private bool Drag(string who, Vector2 from, Vector2 to)
    {
        if (!_lease.Held(who))
            return false;

        // Nothing clever if one is already running: a second drag from the same caller means the
        // first is not going to finish, so it is dropped with its button.
        if (_drags.ContainsKey(who))
        {
            Buttons.LetGo();
            _drags.Remove(who);
        }

        PointerFor(who).Place(from, Modifier.None);
        _missed[who] = false;
        _drags[who] = (to, 0, DateTime.UtcNow);

        return true;
    }

    /// <summary>Whether a drag is still in flight, so a caller can wait for it.</summary>
    private bool Dragging(string who) => _drags.ContainsKey(who);

    /// <summary>
    /// The drag, advanced one tick: press on arrival at the first point, let go at the second.
    /// </summary>
    private void StepDrag(string who, MousePointer pointer)
    {
        if (!_drags.TryGetValue(who, out var drag))
            return;

        switch (drag.Phase)
        {
            // On the thumb - or somewhere else, which is worth finding out before pressing.
            //
            // **A press is a commitment and the coordinates are a belief.** Everything about where
            // the thumb is came from reading rectangles out of the interface, and those have been
            // wrong twice already in this window alone - once the whole track, once the panel. A
            // press on the wrong thing is a click on the wrong thing, and in a rewards window that
            // is a reward taken.
            //
            // So the game is asked what is under the cursor first. UIHover is its own answer rather
            // than another rectangle of ours, and if what it names does not contain the point that
            // was aimed at, nothing is pressed and the drag is abandoned for the caller to report.
            //
            // On the thumb. Press, and stay still for a moment.
            //
            // **A press and a move in the same instant is a click that happens to end elsewhere.**
            // An interface decides a drag has begun by seeing the button go down and stay down while
            // the cursor moves, and sending both in one tick gives it nothing to see. The dwell is
            // what turns two events into a gesture.
            case 0 when pointer.Arrived():
                _trace[who] = $"arrived at ({Cursor().X:0},{Cursor().Y:0})";

                if (!Under(pointer.Expected))
                {
                    _missed[who] = true;
                    _drags.Remove(who);

                    return;
                }

                Buttons.Hold(false);
                _trace[who] += $", pressed at ({Cursor().X:0},{Cursor().Y:0})" +
                               $" holding {Buttons.Holding}";
                _drags[who] = (drag.To, 1, DateTime.UtcNow);

                return;

            case 1 when DateTime.UtcNow - drag.At >= TimeSpan.FromMilliseconds(DwellMs):
                pointer.Place(drag.To, Modifier.None);
                _trace[who] += $", set off from ({Cursor().X:0},{Cursor().Y:0}) " +
                               $"for ({drag.To.X:0},{drag.To.Y:0})";
                _drags[who] = (drag.To, 2, DateTime.UtcNow);

                return;

            // Arrived with the button still down. Hold again before letting go, so the drop lands
            // where the drag ended rather than while it is still being read as movement.
            case 2 when pointer.Arrived():
                _trace[who] += $", got to ({Cursor().X:0},{Cursor().Y:0})";
                _drags[who] = (drag.To, 3, DateTime.UtcNow);

                return;

            case 3 when DateTime.UtcNow - drag.At >= TimeSpan.FromMilliseconds(DwellMs):
                Buttons.LetGo();
                _trace[who] += $", let go at ({Cursor().X:0},{Cursor().Y:0})";
                _drags.Remove(who);

                return;
        }
    }

    /// <summary>How long the button stays down before moving, and before coming up. See StepDrag.</summary>
    private const int DwellMs = 90;

    /// <summary>
    /// Whether the game says the thing under the cursor covers the point that was aimed at.
    ///
    /// Its answer, not ours - the whole value of the check is that it does not come from the same
    /// reading of the interface that produced the aim in the first place.
    /// </summary>
    private bool Under(Vector2 point)
    {
        var hover = GameController?.IngameState?.UIHover;

        try
        {
            // **Nothing under the cursor is a pass, not a failure, and that is the game's doing.**
            //
            // The check began as "the hover must name something covering the point" - the strongest
            // form, and one a scroll bar cannot satisfy: parked on the thumb, UIHover reads no
            // address at all. The game simply does not report bars the way it reports rewards and
            // buttons, so the strict rule refuses every drag there and always would.
            //
            // Inverted, it still does the job it was written for. What it exists to prevent is a
            // press landing on something ELSE - a reward option, a button - and those do report,
            // with a rectangle that will not contain the point being aimed at. So a named element
            // that does not cover the aim is a refusal, and silence is consent.
            if (hover == null || hover.Address == 0)
                return true;

            var at = hover.GetClientRectCache;

            if (at.Width <= 0f || at.Height <= 0f)
                return true;

            return point.X >= at.Left && point.X <= at.Right &&
                   point.Y >= at.Top && point.Y <= at.Bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the last drag was abandoned because the cursor was not on the target.</summary>
    private bool Missed(string who) => _missed.TryGetValue(who, out var missed) && missed;

    /// <summary>
    /// Where the cursor actually was at each step of the last drag.
    ///
    /// **Six complete drags moved nothing, and everything about them was inferred.** The press was
    /// believed to land on the thumb, the travel was believed to happen while the button was down,
    /// and the release was believed to be thirty pixels lower - none of it observed. The cursor's
    /// real position at each step says which of those is not true: a release at the same point as
    /// the press means the travel never happened while held, and a press somewhere other than the
    /// aim means the move before it did not finish.
    /// </summary>
    private string Trace(string who) => _trace.TryGetValue(who, out var text) ? text : "no drag yet";

    private readonly Dictionary<string, string> _trace = new();

    /// <summary>Where the operating system says the cursor is, which is the only unarguable answer.</summary>
    private Vector2 Cursor()
    {
        var at = GameController?.IngameState;

        return at == null ? Vector2.Zero : new Vector2(at.MousePosX, at.MousePosY);
    }

    private readonly Dictionary<string, bool> _missed = new();

    /// <summary>What each caller is dragging, if anything. See Drag.</summary>
    private readonly Dictionary<string, (Vector2 To, int Phase, DateTime At)> _drags = new();

    private bool MoveTo(string who, Vector2 client, Keys[] modifiers)
    {
        if (!_lease.Held(who))
            return false;

        var carried = EndLoiterKeys(who, modifiers ?? Modifier.None) | CarryModifiers(who, modifiers);
        var pointer = PointerFor(who);

        // The queue's next point: the journey and lead drawn when it was planned, so the move made is the one drawn.
        // Anywhere else - a re-aim, a retry - drops the queue, and the caller plans again if it wants it drawn.
        if (_plans.TryGetValue(who, out var plan) && plan.Next < plan.Items.Count && plan.Items[plan.Next].PointGiven &&
            plan.Items[plan.Next].To == client)
        {
            var planned = plan.Items[plan.Next];

            pointer.PlaceAlong(client, modifiers ?? Modifier.None, planned.Drift,
                planned.LeadMs > 0 ? planned.LeadMs : HumanTiming.Ms(Settings.UsedModifierTiming), planned.MinMs, planned.MsPerRootPx,
                planned.Control1, planned.Control2, planned.Shape);
            plan.Current = planned;
            plan.Next++;
        }
        else
        {
            if (_plans.Remove(who, out var dropped))
                NotePlanEnd(who, dropped, dropped.Next < dropped.Items.Count
                    ? $"dropped: a move to the point ({client.X:0},{client.Y:0}) where the next planned " +
                      (dropped.Items[dropped.Next].PointGiven
                          ? $"was ({dropped.Items[dropped.Next].To.X:0},{dropped.Items[dropped.Next].To.Y:0})"
                          : $"was the rectangle ({dropped.Items[dropped.Next].Rect.X:0},{dropped.Items[dropped.Next].Rect.Y:0} " +
                            $"{dropped.Items[dropped.Next].Rect.Width:0}x{dropped.Items[dropped.Next].Rect.Height:0})")
                    : "used up: a move after its last click");

            pointer.Place(client, modifiers ?? Modifier.None);
        }

        if (carried)
            pointer.ModifiersAlreadyDown();

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

    /// <summary>
    /// Sets this caller's Shortest Move in place of the menu's, in milliseconds. Travel adds it to every move's time, near
    /// or far, so a caller sets it for the moves it is meant for. Kept per caller until changed, as a tolerance is; a
    /// negative value goes back to the menu's. Refused without the lease or above
    /// 1000.
    /// </summary>
    private bool SetShortestMove(string who, int ms)
    {
        if (!_lease.Held(who) || ms > 1000)
            return false;

        if (ms < 0)
            _shortestMoves.Remove(who);
        else
            _shortestMoves[who] = ms;

        return true;
    }

    /// <summary>Each caller's own Shortest Move in milliseconds, where one was asked for. See SetShortestMove.</summary>
    private readonly Dictionary<string, int> _shortestMoves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A caller's own Move Time Per Root Pixel in place of the menu's, as SetShortestMove is for Shortest Move. Zero
    /// teleports; negative goes back to the menu's. Up to 60, the menu's own range.
    /// </summary>
    private bool SetMoveTimePerRootPixel(string who, int ms)
    {
        if (!_lease.Held(who) || ms > 60)
            return false;

        if (ms < 0)
            _moveTimesPerRootPixel.Remove(who);
        else
            _moveTimesPerRootPixel[who] = ms;

        return true;
    }

    private readonly Dictionary<string, int> _moveTimesPerRootPixel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Until the caller releases the cursor or is stopped, a key the player presses - any but those given - stops it
    /// with PlayerPressedAKey, as moving the mouse does. Keys this plugin sends do not count. For a run the player should
    /// be able to cancel by moving with the keyboard. Cleared on release, so each claim asks again. Needs the cursor
    /// taken. See KeyWatch.
    /// </summary>
    private bool StopOnPlayerKeys(string who, Keys[] except)
    {
        if (!_lease.Held(who))
            return false;

        var keys = new HashSet<int>();

        foreach (var key in except ?? [])
            keys.Add((int)(key & Keys.KeyCode));

        _keyStops[who] = keys;
        KeyWatch.Start();
        KeyWatch.Drain();

        return true;
    }

    /// <summary>
    /// For up to KeyWatch.MostBlockMs: every key the player holds is let go, so a walk on WASD stops, and their key-downs
    /// are dropped before the game sees them; key-ups and this plugin's own keys pass. 0 ends a block early. Does not need
    /// the cursor, as a pickup's walk outlasts the click: the block runs to its time unless ended. Mouse buttons are not
    /// let go. With restoreHeldKeys, the letters, digits and Space held at the start and still held at the end are pressed
    /// again when the block ends. See KeyWatch.NoteHeld. A press of cancelKey, if not Keys.None, ends the key block and the
    /// mouse block at once and is itself dropped; KeyBlockCancelled reports it.
    /// </summary>
    private bool BlockPlayerKeys(string who, int ms, bool restoreHeldKeys, Keys cancelKey)
    {
        if (string.IsNullOrWhiteSpace(who))
            return false;

        if (ms > 0)
        {
            // Let go of what the player is holding: a held W keeps the character walking whatever arrives after it.
            // The Windows keys (0x5B, 0x5C) are left alone: a let-go with nothing pressed between opens the Start menu.
            for (var key = 0x07; key <= 0xFE; key++)
            {
                if (key is not (0x5B or 0x5C) && (GetAsyncKeyState(key) & 0x8000) != 0)
                {
                    if (restoreHeldKeys)
                        KeyWatch.NoteHeld(key);

                    // With the key's scan code, so the game takes it as the key the player holds. See PhysicalKeys.
                    Safe(() => PhysicalKeys.Up(key));
                }
            }
        }

        KeyWatch.Block(ms, (int)(cancelKey & Keys.KeyCode));

        return true;
    }

    private static void Safe(Action act)
    {
        try
        {
            act();
        }
        catch
        {
            // A key that would not come up is no worse than one that was not pressed.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>Callers stopped by the player's keys, with the keys that do not stop them. See StopOnPlayerKeys.</summary>
    private readonly Dictionary<string, HashSet<int>> _keyStops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops a caller's key stop, and the watch with the last one.</summary>
    private void ForgetKeyStop(string who)
    {
        if (who != null)
            _keyStops.Remove(who);

        if (_keyStops.Count == 0)
            KeyWatch.Stop();
    }

    /// <summary>
    /// For up to MouseBlock.MostMs, the player's mouse movement does not stop this caller, and with block the player's
    /// movement, presses and wheel are dropped before the game sees them. Ends early when the caller releases or is
    /// stopped. Needs the cursor taken. See MouseBlock.
    /// </summary>
    private bool IgnorePlayerMouse(string who, int ms, bool block)
    {
        if (!_lease.Held(who))
            return false;

        ms = Math.Clamp(ms, 0, MouseBlock.MostMs);
        PointerFor(who).IgnorePlayerFor(ms);

        if (block)
            MouseBlock.Block(ms);
        else
            MouseBlock.Stop();

        return true;
    }

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

        // A planned click's button hold and modifier release, as drawn. See ClickPlan.
        if (_plans.TryGetValue(who, out var plan) && plan.Current is { } planned)
        {
            stroke.UseNext(planned.HoldMs, planned.ReleaseMs);
            plan.MarkDone();
            plan.Current = null;
        }

        if (Gesture.StraightIn(modifiers, pointer.ModifiersReady, Buttons.IsDown))
            return Pressed(pointer, stroke.StraightIn(right, modifiers));

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
    /// **A plain click armed to go out the moment the cursor is put on its target**, from the input thread, rather than
    /// when the caller next asks: an instant loot pickup sent its move and then waited for its next frame to click, and a
    /// HUD stall of 300 to 500ms after a drop held the click that long. Left or right, no modifiers. Stays armed through a
    /// fresh move, so a caller re-aiming at a label that slid keeps the click; CancelClickOnArrival disarms it, and
    /// letting go of the cursor does. False without the cursor.
    ///
    /// The click goes out on this pointer's own record of having put the cursor there - no read of the game - so a
    /// caller wanting the game's word on what is under it has to have it before the cursor arrives.
    /// </summary>
    private bool ClickOnArrival(string who, bool right)
    {
        if (!_lease.Held(who))
            return false;

        _clicksOnArrival[who] = right;
        _clickedOnArrival.Remove(who);
        return true;
    }

    /// <summary>Disarms a click on arrival. True when one was armed and had not gone out. See ClickOnArrival.</summary>
    private bool CancelClickOnArrival(string who) => who != null && _clicksOnArrival.Remove(who);

    /// <summary>Whether an armed click went out on arrival, read once. See ClickOnArrival.</summary>
    private bool ClickedOnArrival(string who) => who != null && _clickedOnArrival.Remove(who);

    /// <summary>Makes the click armed for this caller once its cursor is on target and its last click's button is up.</summary>
    private void ClickIfArrived(string who, MousePointer pointer)
    {
        if (!_clicksOnArrival.TryGetValue(who, out var right) || !pointer.PutOnTarget || Buttons.PressHeld)
            return;

        _clicksOnArrival.Remove(who);

        if (Click(who, Modifier.None, right))
            _clickedOnArrival.Add(who);
    }

    /// <summary>Clicks armed for arrival, by caller, and whether each went left or right. See ClickOnArrival.</summary>
    private readonly Dictionary<string, bool> _clicksOnArrival = new();

    /// <summary>Callers whose armed click went out, until they read it. See ClickedOnArrival.</summary>
    private readonly HashSet<string> _clickedOnArrival = new();

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
                HoldMs = () => HumanTiming.Ms(Settings.UsedClickHold),
                ModifierMs = () => HumanTiming.Ms(Settings.UsedModifierTiming),

                // The keys coming up is the end of the gesture, so it is also the moment the rest
                // of the bookkeeping about them stops being true. Wired here rather than inside
                // the stroke because a stroke that reached into the pointer would be two things.
                OnLetGo = () =>
                {
                    // **Not the keys a loiter holds for the click it waits on.** See _loiterKeys and KeepHeld.
                    if (_loiterKeys.TryGetValue(who, out var kept))
                    {
                        var keys = KeysFor(who);

                        foreach (var key in keys.Down.ToList())
                            if (Array.IndexOf(kept, key) < 0)
                                keys.Let(key);

                        return;
                    }

                    KeysFor(who).ReleaseAll();
                    PointerFor(who).ModifiersLetGo();
                },
                KeepHeld = () => _loiterKeys.TryGetValue(who, out var kept) ? kept : null,
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

        // A click whose button is still down keeps its modifiers until the button is up and a moment after, as it would
        // have had the caller kept the cursor; letting go of ctrl first would end the click as a plain one.
        if (Buttons.PressHeld)
        {
            var keys = KeysFor(who);

            Buttons.AfterPress(HumanTiming.Ms(Settings.UsedModifierTiming), () =>
            {
                keys.ReleaseAll();
                Buttons.Release();
            });
        }
        else
        {
            KeysFor(who).ReleaseAll();
            Buttons.Release();
        }

        PointerFor(who).IgnorePlayerFor(0);

        // A blocked mouse stays blocked until a click's button is up: moving it with the button down is a drag.
        if (Buttons.PressHeld && MouseBlock.Blocking)
            MouseBlock.Block(Buttons.MsUntilPressUp);
        else
            MouseBlock.Stop();

        ForgetKeyStop(who);
        FadePlan(who, "the caller handed the cursor back");
        _clicksOnArrival.Remove(who);
        _loiterKeys.Remove(who);
        _loiterOnPlanned.Remove(who);
        _lease.Release(who);
        return true;
    }

    public override void Tick()
    {
        lock (InputGate)
            TickGated();
    }

    private void TickGated()
    {
        _lease.Expiry = TimeSpan.FromSeconds(Settings.ClaimExpirySeconds.Value);

        // What the input thread last caught, said here: logging from another thread is not this host's to promise.
        if (System.Threading.Interlocked.Exchange(ref _moverFault, null) is { } fault)
            LogError($"ExileInput2: the input thread caught {fault}", 10f);

        // A click's button comes up when its hold is over, whoever holds the cursor now. See Buttons.Press.
        Buttons.Step();

        // The seed changed in the menu. See ExileInput2Settings.Seed.
        if (Settings.Seed != _seedInUse)
        {
            PerlinNoise.UseSeed(Settings.Seed);
            _seedInUse = Settings.Seed;
        }

        // The journey, advanced for whoever holds the cursor. One place doing this is the reason
        // the plugin exists rather than a library: a passive helper would need every consumer to
        // remember to step its own movement, and the one that forgot would simply never arrive.
        if (_lease.Holder is { } driving)
        {
            var pointer = PointerFor(driving);

            // Moved on by the input thread while it runs; by this step otherwise. See ExileInput2Settings.RunInputTimingOnOwnThread.
            pointer.MovedElsewhere = MoverRunning;

            // The window going away is as good a reason to stop as the player taking the mouse
            // back: a click sent while the game is not in front lands in whatever is.
            if (Blocked(driving) is var blocked and not StopReason.None)
                pointer.Stop(blocked, _blockedDetail);

            // A loiter that ended on its own - its time up, or stopped - lets go of what it held. See _loiterKeys.
            if (_loiterKeys.ContainsKey(driving) && !pointer.Loitering)
                EndLoiterKeys(driving, Modifier.None);

            // A key of the player's, to a holder that asked to be stopped by keys. See StopOnPlayerKeys.
            if (_keyStops.TryGetValue(driving, out var except))
            {
                foreach (var key in KeyWatch.Drain())
                {
                    if (!except.Contains(key))
                    {
                        pointer.Stop(StopReason.PlayerPressedAKey, $"the player pressed {(Keys)key}");
                        break;
                    }
                }
            }

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

                // And the drag, which is a press with a journey inside it. See Drag.
                StepDrag(driving, pointer);

                // A click armed for arrival, when the input thread is not there to make it. See ClickOnArrival.
                ClickIfArrived(driving, pointer);
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
            Buttons.EndPress();
            StrokeFor(_held).Abandon();
            KeysFor(_held).ReleaseAll();
            Buttons.Release();
            _drags.Remove(_held);
            FadePlan(_held, "the caller's claim on the cursor lapsed");
            MouseBlock.Stop();
            ForgetKeyStop(_held);
            _clicksOnArrival.Remove(_held);
            _loiterKeys.Remove(_held);
            _loiterOnPlanned.Remove(_held);
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
        Buttons.EndPress();
        StrokeFor(who).Abandon();
        KeysFor(who).ReleaseAll();
        Buttons.Release();
        _drags.Remove(who);
        _loiterKeys.Remove(who);
        _loiterOnPlanned.Remove(who);
        FadePlan(who, $"stopped ({reason})");
        PointerFor(who).IgnorePlayerFor(0);
        MouseBlock.Stop();
        ForgetKeyStop(who);
        _clicksOnArrival.Remove(who);
        _lease.Release(who);
        _held = null;
    }

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

    /// <summary>
    /// A queue of clicks at these points, in order, each a right click where rights says so and all with these modifiers,
    /// planned now and drawn on screen while show is set. Each move's shortest move is shortestMoveMs's for it, moved by
    /// the seed as SetShortestMove's is; -1, or none given, is the menu's own. The caller still asks for each move and
    /// click; a move to the next point uses its planned journey and timing, whatever is set by then, and anything else
    /// drops the queue. Empty points drop it now. Needs the cursor taken. See ClickPlan.
    /// </summary>
    private bool PlanClicks(string who, RectangleF[] rects, Vector2[] points, bool[] rights, Keys[][] modifiers, int[] shortestMoveMs,
        bool show)
    {
        if (!_lease.Held(who))
            return false;

        rects ??= [];
        points ??= [];

        if (rects.Length == 0 && points.Length == 0)
        {
            if (_plans.Remove(who, out var emptied))
                NotePlanEnd(who, emptied, "dropped by the caller");

            return true;
        }

        // A queue replaced: its clicks already made go on fading out, the rest give way to the new one. See ClickPlan.Draw.
        //
        // **The new queue's first journey continues the last one's curve** while the cursor is still on the click that
        // journey went to - within its creep - as a queue added to does: it leaves the way the last one came in rather
        // than setting off straight for its target. A caller plans afresh where it waits on the game - the reforging
        // bench's button, the reforged tablet - and every such hand-over turned a sharp corner (the player, 2026-10-20;
        // dump 22:39:28, single-click plans replacing each other between the long ones). See ClickPlan.Make's previous.
        Vector2? continuing = null;

        if (_plans.TryGetValue(who, out var replaced))
        {
            NotePlanEnd(who, replaced, $"replaced by a new plan of {Math.Max(rects.Length, points.Length)}");

            if (replaced.Show)
                _fading.Add((replaced, DateTime.UtcNow, true));

            var arrived = replaced.Current ?? (replaced.Next > 0 && replaced.Next <= replaced.Items.Count ? replaced.Items[replaced.Next - 1] : null);

            if (arrived != null && Vector2.Distance(arrived.To, Cursor()) <= ClickPlan.CreepPx + 2f &&
                Vector2.Distance(arrived.From, arrived.To) > 1f)
                continuing = arrived.From;
        }

        // **Modifiers already down for the first click are drawn as carried, not pressed again**: a loiter holding ctrl
        // for the ctrl+click it waits on, or the last click still holding the same ones. Planned as fresh, the first click
        // was drawn with ctrl going down at the reforging bench's slot, where the move takes the held ctrl over. See
        // EndLoiterKeys and CarryModifiers.
        var first = modifiers is { Length: > 0 } && modifiers[0] is { Length: > 0 } wanted ? wanted : null;
        var alreadyDown = first == null ? null
            : _loiterKeys.TryGetValue(who, out var loitering) && loitering.Length == first.Length && loitering.All(k => Array.IndexOf(first, k) >= 0)
                ? first
                : StrokeFor(who).HoldingAfterClick(first) ? first : null;

        _plans[who] = ClickPlan.Make(PointerFor(who), Cursor(), rects, points, rights ?? [], modifiers ?? [], show,
            () => HumanTiming.Ms(Settings.UsedClickHold), () => HumanTiming.Ms(Settings.UsedModifierTiming), ShortestOf(shortestMoveMs),
            continuing, alreadyDown);

        return true;
    }

    /// <summary>Each click's shortest move from a caller's list: its own, or ExileInput2's where it gives -1 or none.</summary>
    private Func<int, float> ShortestOf(int[] shortestMoveMs) =>
        i => shortestMoveMs != null && i < shortestMoveMs.Length && shortestMoveMs[i] >= 0
            ? shortestMoveMs[i] * ExileInput2Settings.SeedFactor(Settings.Seed, ExileInput2Settings.ShortestMoveIndex)
            : Settings.UsedMinTravelMs;

    /// <summary>
    /// Adds clicks to the end of a caller's queue, as PlanClicks would plan them, from where its last click lands: the
    /// queue goes on rather than being planned again, and what is drawn of it stays as it was. The first added click
    /// continues the last one's curve and carries over its modifiers where it holds the same. False with no queue to add
    /// to, or without the cursor.
    /// </summary>
    private bool ExtendPlan(string who, RectangleF[] rects, Vector2[] points, bool[] rights, Keys[][] modifiers, int[] shortestMoveMs)
    {
        if (!_lease.Held(who) || !_plans.TryGetValue(who, out var plan) || plan.Items.Count == 0)
            return false;

        rects ??= [];
        points ??= [];

        if (rects.Length == 0 && points.Length == 0)
            return true;

        var last = plan.Items[^1];
        var added = ClickPlan.Make(PointerFor(who), last.To, rects, points, rights ?? [], modifiers ?? [], plan.Show,
            () => HumanTiming.Ms(Settings.UsedClickHold), () => HumanTiming.Ms(Settings.UsedModifierTiming), ShortestOf(shortestMoveMs),
            last.From, last.Modifiers);

        plan.Items.AddRange(added.Items);
        return true;
    }

    /// <summary>Each caller's planned clicks, while it holds the cursor. See PlanClicks.</summary>
    private readonly Dictionary<string, ClickPlan> _plans = new();

    /// <summary>
    /// Takes a caller's queue away when it gives up the cursor or is stopped - the player moving the mouse, the action
    /// key ending a run - and lets what was left of it fade out over FadeFor rather than vanish. See Render.
    /// </summary>
    private void FadePlan(string who, string why)
    {
        if (who == null || !_plans.Remove(who, out var plan))
            return;

        NotePlanEnd(who, plan, $"faded out: {why}");

        if (plan.Show)
            _fading.Add((plan, DateTime.UtcNow, false));
    }

    /// <summary>
    /// Keeps how a caller's queue ended - replaced, dropped by a move elsewhere, used up, or faded out as the caller gave
    /// up the cursor - with how far through it was, for the caller's dump. The last PlanEndsKept. See PlanEndsSaid.
    /// </summary>
    private void NotePlanEnd(string who, ClickPlan plan, string how)
    {
        _planEnds.Add((who, $"{DateTime.Now:HH:mm:ss.fff} {how}; {plan.Next} of {plan.Items.Count} moved to" +
                            (plan.Current != null ? ", the last not yet clicked" : "")));

        if (_planEnds.Count > PlanEndsKept)
            _planEnds.RemoveAt(0);
    }

    /// <summary>How a caller's queues ended, newest last. See NotePlanEnd.</summary>
    private string[] PlanEndsSaid(string who) => _planEnds.Where(e => e.Who == who).Select(e => e.Said).ToArray();

    private readonly List<(string Who, string Said)> _planEnds = new();

    /// <summary>How many plan endings are kept, for every caller together. Chosen.</summary>
    private const int PlanEndsKept = 60;

    /// <summary>Queues given up, fading, with when. See FadePlan.</summary>
    private readonly List<(ClickPlan Plan, DateTime Since, bool DoneOnly)> _fading = new();

    private static readonly TimeSpan FadeFor = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The queue of the caller holding the cursor, where it asked for it to be shown, and any given up, fading. See
    /// ClickPlan.Draw.
    /// </summary>
    public override void Render()
    {
        lock (InputGate)
            RenderGated();
    }

    private void RenderGated()
    {
        var now = DateTime.UtcNow;

        _fading.RemoveAll(f => now - f.Since >= FadeFor);

        // A queue given up fades as a whole; one replaced draws only its clicks already made, which fade by their own time.
        var colours = new ClickPlan.Colours(Settings.PlanCurrentColour.Value, Settings.PlanLaterColour.Value, Settings.PlanDoneColour.Value);
        var marks = new ClickPlan.Marks(Settings.PlanShowButtons.Value, Settings.PlanShowModifiers.Value, Settings.PlanShowNumbers.Value,
            Settings.PlanFadeOut.Value);

        // With Fade Out off, nothing given up is drawn; the list is kept, so turning it on mid-fade shows what is left.
        if (marks.Fade)
        {
            foreach (var (plan, since, doneOnly) in _fading)
                plan.Draw(Graphics, colours, marks, doneOnly ? 1f : 1f - (float)((now - since).TotalMilliseconds / FadeFor.TotalMilliseconds), doneOnly);
        }

        if (_lease.Holder is { } holder && _plans.TryGetValue(holder, out var current) && current.Show)
        {
            var pointer = PointerFor(holder);

            current.Draw(Graphics, colours, marks, trail: new ClickPlan.CursorTrail(pointer.Trail(), pointer.JourneyProgress));
        }
    }

    /// <summary>
    /// Held by everything that touches the pointers, the buttons, the leases and the plans: every bridge method, Tick,
    /// Render and the input thread. Those are the HUD's thread and the input thread, so one lock keeps them from running
    /// at once; it is held for a step, never across a wait. See MoverLoop.
    /// </summary>
    private static readonly object InputGate = new();

    /// <summary>A bridge method's body, run under InputGate. See InputGate.</summary>
    private static T Gated<T>(Func<T> body)
    {
        lock (InputGate)
            return body();
    }

    private static void Gated(Action body)
    {
        lock (InputGate)
            body();
    }

    /// <summary>
    /// **The input thread: the cursor moved, and a click's button and modifiers let up, between the HUD's frames.**
    ///
    /// Everything ExileInput2 does ran in Tick, which the host calls from inside its render loop, on the thread that then
    /// presents the overlay - and the overlay waited there 150 to 650ms at a time while the game took long over its
    /// frames (a trace of the HUD, 2026-10-12: 14 of 15 stalls over 150ms in IDXGISwapChain.Present). The cursor froze
    /// for each and then jumped or crept. Moved from here every MoverStepMs, it carries on to its target through one.
    ///
    /// Only what reads nothing of the game's runs here: the journey (MousePointer.MoveAlong) and the buttons' timed
    /// releases (Buttons.Step). The checks that read the game - the player's mouse, the window, the player's keys - and
    /// the clicks and the start of each journey stay on the HUD's frames. Everything here catches its own exceptions, as
    /// one escaping a thread of its own ends the whole process; and the thread is stopped on unload, before anything
    /// else, or a reloaded plugin's old copy would go on moving the cursor.
    /// </summary>
    private System.Threading.Thread _mover;

    private volatile bool _moverStopping;

    /// <summary>The last exception the input thread caught, for Tick to log. See MoverLoop.</summary>
    private string _moverFault;

    /// <summary>How often the input thread steps, in milliseconds: well under a frame, so a journey moves smoothly.</summary>
    private const int MoverStepMs = 1;

    /// <summary>Whether the input thread is moving the cursor: switched on, and running.</summary>
    private bool MoverRunning => Settings.RunInputTimingOnOwnThread.Value && _mover is { IsAlive: true } && !_moverStopping;

    private void StartMover()
    {
        if (_mover != null)
            return;

        _moverStopping = false;
        _mover = new System.Threading.Thread(MoverLoop)
        {
            IsBackground = true,
            Name = "ExileInput2 input",
            Priority = System.Threading.ThreadPriority.AboveNormal,
        };
        _mover.Start();
    }

    private void StopMover()
    {
        _moverStopping = true;
        _mover?.Join(500);
        _mover = null;
    }

    private void MoverLoop()
    {
        // A millisecond's sleep needs the system timer at a millisecond; asked for while the thread runs, and given back.
        var fine = false;

        try
        {
            fine = TimeBeginPeriod(1) == 0;
        }
        catch (Exception)
        {
            // Without it a sleep is the system's default tick, about 15ms - still steadier than the HUD's stalls.
        }

        try
        {
            while (!_moverStopping)
            {
                System.Threading.Thread.Sleep(MoverStepMs);

                try
                {
                    lock (InputGate)
                    {
                        if (_moverStopping || !Settings.RunInputTimingOnOwnThread.Value)
                            continue;

                        Buttons.Step();

                        if (_lease.Holder is { } driving && _pointers.TryGetValue(driving, out var pointer) && pointer.MovedElsewhere)
                        {
                            pointer.MoveAlong();
                            ClickIfArrived(driving, pointer);
                        }
                    }
                }
                catch (Exception e)
                {
                    _moverFault = $"{e.GetType().Name}: {e.Message}";
                }
            }
        }
        finally
        {
            if (fine)
                TimeEndPeriod(1);
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    public override void OnUnload()
    {
        // First, so nothing moves the cursor from a plugin being unloaded. See MoverLoop.
        StopMover();

        AppDomain.CurrentDomain.ProcessExit -= LetGoOfEverything;
        AppDomain.CurrentDomain.UnhandledException -= LetGoOfEverything;

        foreach (var stroke in _strokes.Values)
            stroke.Abandon();

        foreach (var keys in _keys.Values)
            keys.ReleaseAll();

        HeldKeys.ReleaseEverything();
        MouseBlock.Stop();
        KeyWatch.Stop();
        KeyWatch.Block(0);
        _lease.Break();
    }
}

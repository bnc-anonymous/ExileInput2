using ExileCore2;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2;

/// <summary>
/// Where the cursor is being sent, and whether it has got there.
///
/// One per state machine rather than a static, because each keeps its own expectation and its own
/// tolerance - forty pixels suits a stash tab, twelve suits an inventory cell, and a shared figure
/// would be wrong for one of them.
///
/// Everything here is in client space, which is what every call site already has: element rects
/// come off GetClientRectCache and the game reports its own cursor as MousePosX/MousePosY. Only the
/// actual move needs screen coordinates, and that translation happens once, here.
///
/// That translation is the reason this exists at all. Of the five machines that moved a cursor,
/// three added the window origin before calling SetCursorPos and two did not - and both then
/// compared against the game's client-space cursor, so the comparisons agreed while the moves did
/// not. The two that skipped it aim correctly only while the window sits at the top left of the
/// screen, which fullscreen guarantees and windowed play does not. Nobody noticed because nobody
/// plays windowed.
/// </summary>
internal sealed class MousePointer
{
    private readonly GameController _gc;
    private readonly Random _rng;

    public MousePointer(GameController gc, Random rng = null)
    {
        _gc = gc;
        _rng = rng ?? new Random();

        // Each pointer starts somewhere of its own along the noise, so two sessions do not open with the same moves.
        // From the generator handed in, so a test's seeded generator still gives it the same start every run.
        _drift = (float)(_rng.NextDouble() * 256d);
        _journey = (float)(_rng.NextDouble() * 256d);
    }

    /// <summary>
    /// Where the cursor is, and how to put it somewhere - both replaceable.
    ///
    /// So that the wiring can be tested and not just the sums. The rect bound was written, given a
    /// table of its own, reported as fixed and was never once reached: Move calls Place directly,
    /// Place resets the bound, and only Aim set it. Every case passed, because every case called
    /// the arithmetic rather than the pointer.
    /// </summary>
    internal Func<Vector2> Cursor { get; set; }

    internal Action<Vector2> Send { get; set; }

    /// <summary>What time it is, handed on to each journey so a test can advance one.</summary>
    internal Func<TimeSpan> Clock { get; set; }

    /// <summary>
    /// How much of a target to aim within, as a percentage of its size.
    ///
    /// Nought is dead centre every time, which is both conspicuous and brittle - a rect read a
    /// pixel off centre is still hit anywhere inside it. A hundred is the whole rect including its
    /// edges, which will miss where the rect is drawn larger than the thing it represents.
    /// </summary>
    public float SpreadPercent { get; set; } = 60f;

    /// <summary>How near the cursor must be to where it was sent before it counts as arrived.</summary>
    public int TolerancePx { get; set; } = 12;

    /// <summary>The shortest a journey may take, however near the target.</summary>
    public float MinTravelMs { get; set; } = 60f;

    /// <summary>Milliseconds added per square root of a pixel travelled. Nought teleports.</summary>
    public float TravelMsPerRootPx { get; set; } = 8f;

    /// <summary>How far the path bows off the straight line, as a percentage of its length.</summary>
    public float ArcPercent { get; set; } = 15f;

    /// <summary>How far the cursor may sit from anywhere this pointer put it before that reads as
    /// the player having taken the mouse back.</summary>
    public int StopTolerancePx { get; set; } = 10;

    /// <summary>How long an outstanding move may go unconfirmed before it is given up on.</summary>
    public float ArriveTimeoutMs { get; set; } = 1500f;

    /// <summary>Why this pointer stopped, or None while it is working.</summary>
    public StopReason Stopped { get; private set; }

    /// <summary>
    /// The points the cursor was sent to lately, oldest first, with when.
    ///
    /// Many rather than a single point because the game reports its cursor a frame or several
    /// behind where it was put - mid-journey the two are never equal, and comparing against the
    /// latest send alone would read this pointer's own movement as the player's. Every position the
    /// game can legitimately be reporting is one this pointer sent recently, so the question is
    /// whether the cursor is near ANY of them.
    ///
    /// Kept by time, not by count. The last twelve sends were a fifth of a second at 60 fps but 73ms
    /// at the 165 the HUD ran at, and a report a few game frames late fell out of them: three runs
    /// stopped on 2026-10-10 with the cursor 13, 17 and 24 px from the nearest kept send and nobody
    /// touching the mouse. See StopCases 10.
    /// </summary>
    private readonly List<(Vector2 Point, TimeSpan At)> _recent = new();

    /// <summary>How far back a send still counts as somewhere the game may be reporting.</summary>
    private static readonly TimeSpan RecentFor = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The sends always kept, however old: an idle pointer's cursor stays where it was last put, and a
    /// teleport sends only once.
    /// </summary>
    private const int RecentSendsAtLeast = 12;

    /// <summary>When the outstanding move was asked for, for the timeout.</summary>
    private TimeSpan _placedAt;

    private TimeSpan Now() => Clock?.Invoke() ?? TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void Remember(Vector2 client)
    {
        var now = Now();

        _recent.Add((client, now));

        var stale = 0;

        while (stale < _recent.Count - RecentSendsAtLeast && now - _recent[stale].At > RecentFor)
            stale++;

        _recent.RemoveRange(0, stale);
    }

    /// <summary>
    /// Whether the cursor is somewhere this pointer never put it - which means the player moved it.
    ///
    /// This is the whole safety model, so it is deliberately the cheap end of clever: no velocity,
    /// no heuristics about intent, just "is the cursor anywhere I left it". A player who nudges the
    /// mouse gets everything to stop, which is what a hand reaching for the mouse means every time.
    /// </summary>
    public bool Interfered()
    {
        if (_recent.Count == 0)
            return false;

        var here = Where();
        var tolerance = (float)StopTolerancePx * StopTolerancePx;

        // Every send kept counts: those within RecentFor, and always the last RecentSendsAtLeast. See Remember.
        //
        // **"The mouse was moved" is a conclusion, and it is sometimes the wrong one.** The cursor being somewhere
        // this pointer never put it is the only evidence there is. A hand on the mouse is one explanation; the game's
        // report lagging past RecentFor is another, which is what RecentFor's length is for.
        foreach (var (point, _) in _recent)
        {
            if (Vector2.DistanceSquared(here, point) <= tolerance)
                return false;
        }

        return true;
    }

    /// <summary>Whether an asked-for move has gone unconfirmed for longer than it should have.</summary>
    public bool Expired() =>
        Awaiting && ArriveTimeoutMs > 0 && (Now() - _placedAt).TotalMilliseconds > ArriveTimeoutMs;

    /// <summary>
    /// Gives up the cursor and stays given up, until the caller acknowledges it.
    ///
    /// Sticky on purpose. A caller polling every tick would otherwise carry straight on the moment
    /// the player's hand came to rest, which is the opposite of what taking the mouse back means.
    /// </summary>
    public void Stop(StopReason reason)
    {
        Stopped = reason;
        Forget();
    }

    /// <summary>Clears the stop, for a caller that has noticed it and is starting over.</summary>
    public void Resume()
    {
        Stopped = StopReason.None;
        _recent.Clear();
    }

    /// <summary>The journey in progress, or null when the cursor is where it was put.</summary>
    private Travel _travel;

    /// <summary>What to press partway through, and where.</summary>
    private Keys[] _modifiers = [];
    private float _modifierAt;
    private bool _modifiersDown;

    /// <summary>Told when the journey presses its modifier, so somebody can release it later.</summary>
    public Action<Keys> OnModifier { get; set; }

    /// <summary>
    /// Whether the modifiers this journey pressed are still down.
    ///
    /// Must be told when they are let go, or it goes on saying yes to a click that has none - which
    /// is a bare click, and a bare right click on a currency item uses it rather than moving it.
    /// It said yes for the whole session once, because pressing set it and nothing cleared it.
    /// </summary>
    public bool ModifiersReady => _modifiersDown;

    /// <summary>Told that whatever the journey pressed has been released.</summary>
    public void ModifiersLetGo() => _modifiersDown = false;

    /// <summary>Where the cursor was last sent, in client space.</summary>
    public Vector2 Expected { get; private set; }

    /// <summary>Whether a move has been asked for and not yet confirmed.</summary>
    public bool Awaiting { get; private set; }

    /// <summary>How far off the target a landing may be and still count, for the current move.</summary>
    internal float Accepts => Math.Min(TolerancePx, _bound);

    /// <summary>
    /// A point to aim at inside a rect, wandering about its centre.
    ///
    /// Perlin noise rather than a fresh random number, and the difference is what happens over many
    /// clicks rather than on any one of them. Random draws are unrelated, so four hundred clicks at
    /// one slot scatter uniformly across it - a cloud no hand has ever made, produced identically
    /// every session. Noise sampled along a slowly advancing line gives points near their
    /// neighbours: the aim rests somewhere, drifts, settles somewhere else, which is what a hand on
    /// a mouse actually does.
    ///
    /// The drift advances by an uneven step so the wander is not periodic either. Perlin repeats
    /// over its permutation table, and marching along it at a constant rate would eventually trace
    /// the same path at the same pace.
    ///
    /// Clamped at ninety-five per cent rather than a hundred so that even at the widest setting the
    /// point stays off the boundary, where a rect that is a pixel generous starts landing outside
    /// the thing it describes.
    /// </summary>
    public Vector2 PointIn(RectangleF rect)
    {
        _drift += DriftStep + (float)_rng.NextDouble() * DriftStep;
        return PointIn(rect, SpreadPercent, _drift);
    }

    /// <summary>The same, without needing a pointer or a game to ask it.</summary>
    internal static Vector2 PointIn(RectangleF rect, float spreadPercent, float drift)
    {
        var spread = Math.Clamp(spreadPercent, 0f, 95f) / 100f;

        // Two samples far apart on the second axis, so the horizontal and vertical wander are not
        // the same walk. Sampled from one line each, they would move together and the aim would
        // slide along a diagonal.
        var x = PerlinNoise.Full(drift, 0f);
        var y = PerlinNoise.Full(0f, drift + 512f);

        return new Vector2(
            rect.X + rect.Width * (0.5f + x * 0.5f * spread),
            rect.Y + rect.Height * (0.5f + y * 0.5f * spread));
    }

    /// <summary>
    /// How far along the noise each aim moves.
    ///
    /// Small, and it has to be. Perlin noise decorrelates over about one unit, so a step near that
    /// size gives points with almost nothing to do with each other - which is uniform randomness
    /// again, arrived at expensively. A first attempt at a third of a unit came out barely smoother
    /// than a random draw, and it took measuring to see it.
    ///
    /// A tenth of a unit puts consecutive clicks a couple of pixels apart, and four hundred of them
    /// still walk far enough along the noise to cover the target rather than hovering on one spot.
    /// </summary>
    private const float DriftStep = 0.08f;

    private float _drift;

    /// <summary>
    /// Where on the noise the next journey sits, advanced once per journey.
    ///
    /// Its own line rather than the aim's, and stepped faster. They are different habits: where a
    /// hand comes to rest on a target shifts click by click, while how it swings between two places
    /// varies more from one reach to the next. Sharing a line would tie them together, so a click
    /// landing left would always be one that curved the same way.
    /// </summary>
    private float _journey;

    private const float JourneyStep = 0.19f;

    /// <summary>
    /// How far a point sits from the nearest edge of the rect it was chosen inside.
    ///
    /// The whole of the rect bound, in one place with nothing else in it, because the arithmetic
    /// is the part that can be wrong. Taking the minimum across both axes and not each separately
    /// is the bit worth pinning: a tolerance is a radius, so a wide short row has to bind on its
    /// height or a landing within tolerance sideways is still out of the row vertically.
    /// </summary>
    internal static float BoundFor(RectangleF rect, Vector2 target) =>
        Math.Max(TightestPx, Math.Min(
            Math.Min(target.X - rect.Left, rect.Right - target.X),
            Math.Min(target.Y - rect.Top, rect.Bottom - target.Y)));

    /// <summary>
    /// How near the target must be reached when the target came from a rect - never further than
    /// its own edge.
    ///
    /// A tolerance is a guess about how exactly the game reports its cursor, and it is set per
    /// machine by whoever wrote that machine. Forty pixels was a reasonable guess for a stash tab
    /// and a bad one for a currency exchange row, which is fifty-four pixels tall: a cursor forty
    /// pixels from a point thirteen pixels inside the row is outside the row, and a click there
    /// hits whatever is underneath. That is the click that missed the "I want" box.
    ///
    /// Rather than tuning eight numbers against the smallest thing each machine might click, bound
    /// the tolerance by the rect being aimed at. Then within-tolerance implies inside-the-rect by
    /// construction, and it stays true for whatever gets clicked next.
    ///
    /// float.MaxValue when the caller gave a bare point, because there is no rect to be inside of.
    /// </summary>
    private float _bound = float.MaxValue;

    /// <summary>
    /// The least the rect may tighten the tolerance to.
    ///
    /// A rect a few pixels tall would otherwise demand a landing exact to a pixel, and if the game
    /// ever rounds its readback that is a move that can never arrive - the caller waits out its
    /// timeout and stops, which is a worse outcome than the loose click this is here to prevent.
    /// </summary>
    internal const float TightestPx = 3f;

    /// <summary>
    /// Whether to leave the cursor alone when it is already on the thing being aimed at.
    ///
    /// A hand that is already resting on a button does not lift off and land again on it before
    /// pressing. Twitching forty pixels to a different spot inside the same row is a movement with
    /// no purpose behind it, which is a worse tell than not moving at all - and it costs a journey
    /// each time for nothing.
    ///
    /// The click still happens, modifiers and all; only the travel is skipped.
    /// </summary>
    public bool StayPut { get; set; } = true;

    /// <summary>
    /// Whether a point is inside a rect with enough room to be worth staying at.
    ///
    /// Inset rather than the bare rect. A rect is often drawn a shade larger than the thing it
    /// stands for, and the aim spread exists to keep away from exactly that margin - so staying on
    /// a pixel the aim would never have chosen would trade a micro-movement for a click on the
    /// boundary. Three pixels in, the same figure the bound floors at.
    /// </summary>
    internal static bool Holds(RectangleF rect, Vector2 point) =>
        point.X >= rect.Left + TightestPx && point.X <= rect.Right - TightestPx &&
        point.Y >= rect.Top + TightestPx && point.Y <= rect.Bottom - TightestPx;

    /// <summary>
    /// Aims at a rect: picks a point inside it, sends the cursor there, and returns the point.
    ///
    /// The one way a rect is aimed at. It used not to be - Move picked its own point and called
    /// Place, so this was dead code and the bound it sets was never applied to anything.
    /// </summary>
    /// <summary>The rect this has already spent its one stay-put on.</summary>
    private RectangleF? _stayedAt;

    /// <summary>
    /// Records that the caller pressed something, so the next aim at the same place may stay put.
    ///
    /// The difference between a retry and a repetition, and the pointer cannot tell them apart on
    /// its own: both look like the same rectangle twice. A click in between is what distinguishes
    /// them, and the caller is the only one who knows a click happened.
    /// </summary>
    public void Clicked() => _stayedAt = null;

    private static bool Near(RectangleF a, RectangleF b) =>
        Math.Abs(a.X - b.X) < 1f && Math.Abs(a.Y - b.Y) < 1f &&
        Math.Abs(a.Width - b.Width) < 1f && Math.Abs(a.Height - b.Height) < 1f;

    public Vector2 Aim(RectangleF rect, params Keys[] modifiers)
    {
        var here = Where();

        // At most one stay-put per target, and then only until something else is aimed at OR the
        // caller actually clicks.
        //
        // Because a caller that aims at the same rect twice WITHOUT clicking is a caller whose
        // first go did not work, and the answer to that cannot be to do nothing again. The
        // collector asks the game whether the show-stash button is lit before pressing it - a press
        // that misses that panel walks the character out of the hideout - and when it is not lit it
        // aims again. With the cursor already inside the rect that second aim moved nothing, tested
        // the same pixel, and failed for the same reason, four times, then gave up and started
        // over. Twenty seconds of warnings without the cursor moving once.
        //
        // But a caller that aims, CLICKS, and aims again has not retried anything. It has done the
        // thing and is doing it once more, which is what applying a currency to an item four
        // hundred times looks like - and jumping to a fresh pixel between every one of them is the
        // cursor flitting about a slot it should be resting on. Clicked clears the record, so the
        // rule now reads: a retry moves, a repetition does not.
        //
        // Staying put is for a hand already resting where it means to click. It is not an argument
        // for staying somewhere that has just been shown not to work.
        if (_stayedAt is not { } last || !Near(last, rect))
            _stayedAt = null;

        // Already on it: keep the cursor where it is and treat that as the arrival. Expected has
        // to be the actual position rather than the middle of the rect, or the drift check spends
        // its timeout measuring the distance to a place nothing intends to go.
        if (StayPut && _stayedAt == null && Holds(rect, here))
        {
            _stayedAt = rect;

            Expected = here;
            Awaiting = true;
            _travel = null;
            _placedAt = Now();

            // Nothing is sent, so nothing would otherwise be on record - and a pointer with an
            // empty record cannot tell the player's movement from its own first move.
            Remember(here);

            // Nothing to overlap the press with, so it happens now and gets whatever time the
            // caller takes to notice the arrival - which is at least a tick.
            _modifiers = modifiers ?? [];
            _modifiersDown = false;
            PressModifiers();

            _bound = BoundFor(rect, here);
            return here;
        }

        var target = PointIn(rect);

        Place(target, modifiers);

        // After Place, which resets it: the bound belongs to this target, not the last one.
        _bound = BoundFor(rect, target);

        return target;
    }

    /// <summary>
    /// Sends the cursor to a client-space point and records that it is on its way.
    ///
    /// The window origin is added here and nowhere else. A caller that has already added it is
    /// aiming at a point twice as far from the top left as it meant to, which on a 4K monitor is
    /// off the screen entirely.
    /// </summary>
    public void Place(Vector2 client, params Keys[] modifiers)
    {
        // Where the cursor is now goes on the record before anything is sent, because for the next
        // frame or several that is what the game will still be reporting. Without it the very
        // first move of a session reads its own starting point as the player having moved the
        // mouse, and stops before it has gone anywhere.
        var from = Where();
        Remember(from);

        Expected = client;
        Awaiting = true;
        _bound = float.MaxValue;
        _placedAt = Now();

        // Advanced once per journey, and by an uneven step so the wander is not periodic - the same
        // reason the aim's own line is stepped unevenly.
        _journey += JourneyStep + (float)_rng.NextDouble() * JourneyStep;

        _modifiers = modifiers ?? [];
        _modifierAt = Travel.ModifierAt(_journey);
        _modifiersDown = false;

        // Nought means teleport, which is what this did before travel existed and is worth keeping
        // - it is the setting to reach for when something is going wrong and the question is
        // whether the path is why.
        if (TravelMsPerRootPx <= 0)
        {
            _travel = null;
            PressModifiers();
            Put(client);
            return;
        }

        _travel = new Travel(from, client, MinTravelMs, TravelMsPerRootPx, ArcPercent, _journey, Clock);
    }

    /// <summary>
    /// Advances the journey. Called every tick by whoever owns this pointer.
    ///
    /// The modifier goes down partway rather than on arrival, which is the whole point of the
    /// journey existing: it overlaps the press with the approach the way a hand does, and it gives
    /// the key the length of the approach to register rather than making the pointer wait on the
    /// target while it confirms.
    /// </summary>
    public void Step()
    {
        if (Stopped != StopReason.None)
            return;

        // Before advancing, not after: a step that has already moved the cursor has overwritten the
        // evidence of where the player put it.
        if (Interfered())
        {
            Stop(StopReason.PlayerMovedTheMouse);
            return;
        }

        if (Expired())
        {
            Stop(StopReason.MoveNeverLanded);
            return;
        }

        if (_travel == null)
            return;

        if (!_modifiersDown && _travel.Progress >= _modifierAt)
            PressModifiers();

        Put(_travel.Now());

        if (_travel.Done)
            _travel = null;
    }

    /// <summary>
    /// Presses the journey's modifiers, and records that it did - only when there were any.
    ///
    /// The record is what a click asks before letting go of the caller's held keys (Gesture.ReleaseBeforePush), and it
    /// used to be set whether or not there was anything to press. Every journey then read as having pressed a modifier,
    /// so a plain click after any move let go of a shift the caller was holding to keep a currency on the cursor - and
    /// the bare click that followed picked the item up instead of applying the currency to it.
    /// </summary>
    private void PressModifiers()
    {
        _modifiersDown = _modifiers.Length > 0;

        foreach (var key in _modifiers)
            OnModifier?.Invoke(key);
    }

    /// <summary>Where the cursor is now, in client space.</summary>
    private Vector2 Where()
    {
        if (Cursor != null)
            return Cursor();

        try
        {
            return new Vector2(_gc.IngameState.MousePosX, _gc.IngameState.MousePosY);
        }
        catch
        {
            return Expected;
        }
    }

    /// <summary>The one place the window origin is added.</summary>
    private void Put(Vector2 client)
    {
        Remember(client);

        if (Send != null)
        {
            Send(client);
            return;
        }

        var origin = _gc.Window.GetWindowRectangle().TopLeft;
        Input.SetCursorPos(new Vector2(origin.X + client.X, origin.Y + client.Y));
    }

    /// <summary>How far the cursor is from where it was sent, in client-space pixels.</summary>
    public float Drift() => Vector2.Distance(Where(), Expected);

    /// <summary>
    /// Whether the cursor has reached where it was sent.
    ///
    /// Clears the waiting flag once it has, so a caller can ask on every tick and act on the first
    /// tick that answers yes. Asking before the game has caught up compares against where the
    /// cursor used to be - which is why a click sent in the same breath as the move lands wherever
    /// the pointer was resting, and why every first attempt used to fail and every retry work.
    /// </summary>
    public bool Arrived()
    {
        if (!Awaiting)
            return true;

        // Still travelling is not "did not arrive". The drift is large on purpose for as long as
        // the journey is running, and a caller asking every tick would otherwise read its own
        // movement as a failure to move.
        if (_travel != null)
            return false;

        if (Drift() > Accepts)
            return false;

        Awaiting = false;
        return true;
    }

    /// <summary>Forgets any outstanding move, for a machine giving up on what it was doing.</summary>
    public void Forget()
    {
        Awaiting = false;
        _travel = null;
        _modifiersDown = false;
    }

    /// <summary>
    /// Starts a session with no memory of where this pointer has put the cursor.
    ///
    /// The ring of recent sends answers one question - "is the cursor anywhere I left it" - and
    /// that question only means something inside a single session. Carried across one, it means the
    /// opposite of what it says: the player moves the mouse between two sessions, as anybody would,
    /// and the first step of the new one finds the cursor nowhere the ring remembers and calls it
    /// interference. The plugin then stops itself on its own first step and blames the player.
    ///
    /// So a claim starts clean. Note this does NOT clear a stop - acknowledging one is
    /// <see cref="Resume"/>, and it stays the caller's job to do that out loud.
    /// </summary>
    public void Fresh()
    {
        Forget();
        _recent.Clear();
    }
}

using ExileCore2;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>How far back sends are kept for StopDetail only, never for the decision.</summary>
    private static readonly TimeSpan ReportedFor = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What the last stop saw, in one line, for a caller to record: for a hand on the mouse, where the cursor was last put
    /// and how long ago, where it was found and how far off, and whether this pointer itself sent it there in the last
    /// ReportedFor - which would make it the game's report lagging, not a hand; for a move that never landed, where it
    /// was sent, where the game read it, and how near it had to be. Empty before any stop. Never drawn: a caller asks
    /// for it. False stops right after an explosive opened the reward window were reported on 2026-10-10 and 11 with
    /// nothing kept to say which they were.
    /// </summary>
    public string StopDetail { get; private set; } = "";

    /// <summary>When the outstanding move was asked for, for the timeout.</summary>
    private TimeSpan _placedAt;

    /// <summary>
    /// Until when the player's mouse does not stop this pointer, on Now's clock. Zero when it always does. See
    /// IgnorePlayerFor.
    /// </summary>
    private TimeSpan _playerIgnoredUntil;

    /// <summary>
    /// Lets this pointer's moves go on through the player's mouse movement for this long, for a caller that has asked
    /// for one move and click to land however the player's hand moves. Zero or less ends it.
    /// </summary>
    public void IgnorePlayerFor(int ms) => _playerIgnoredUntil = ms > 0 ? Now() + TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    private TimeSpan Now() => Clock?.Invoke() ?? TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void Remember(Vector2 client)
    {
        var now = Now();

        _recent.Add((client, now));

        var stale = 0;

        while (stale < _recent.Count - RecentSendsAtLeast && now - _recent[stale].At > ReportedFor)
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
        var now = Now();

        RememberReport(here, now);
        var tolerance = (float)StopTolerancePx * StopTolerancePx;
        var counted = Math.Max(0, _recent.Count - RecentSendsAtLeast);
        var nearest = float.MaxValue;
        var nearestAt = TimeSpan.Zero;
        var was = Vector2.Zero;

        // The sends that count: those within RecentFor, and always the last RecentSendsAtLeast. Older ones are kept for
        // StopDetail only. See Remember.
        //
        // **"The mouse was moved" is a conclusion, and it is sometimes the wrong one.** The cursor being somewhere
        // this pointer never put it is the only evidence there is. A hand on the mouse is one explanation; the game's
        // report lagging past RecentFor is another, which is what RecentFor's length is for.
        //
        // **Near the path between two sends, not only near a send.** The game reported the cursor at (620,278), on the
        // straight line from (637,286) to (540,244) - two consecutive points this pointer put it - and 19 px from either,
        // with nobody touching the mouse; the run stopped and a second reward window went unanswered (2026-10-11
        // 18:23). Why the game reports a point between two sends is not established; what is established is that a
        // point on the pointer's own path is the pointer's own. A send counts as the end of the segment from the one
        // before it. See StopCases 14.
        for (var i = 0; i < _recent.Count; i++)
        {
            var (point, at) = _recent[i];

            if (i < counted && now - at > RecentFor)
                continue;

            var gap = i == 0
                ? Vector2.DistanceSquared(here, point)
                : DistanceSquaredToSegment(here, _recent[i - 1].Point, point);

            if (gap <= tolerance)
                return false;

            if (gap < nearest)
            {
                nearest = gap;
                nearestAt = at;
                was = point;
            }
        }

        var own = -1d;

        for (var i = _recent.Count - 1; i >= 0; i--)
        {
            var gap = i == 0
                ? Vector2.DistanceSquared(here, _recent[i].Point)
                : DistanceSquaredToSegment(here, _recent[i - 1].Point, _recent[i].Point);

            if (gap <= tolerance)
            {
                own = (now - _recent[i].At).TotalMilliseconds;
                break;
            }
        }

        StopDetail = $"the cursor found at ({here.X:0},{here.Y:0}), {MathF.Sqrt(nearest):0} px from the nearest path it was " +
                     $"put along, ending ({was.X:0},{was.Y:0}) {(now - nearestAt).TotalMilliseconds:0}ms ago; last sent to " +
                     $"({Expected.X:0},{Expected.Y:0}), {(_travel != null ? "still on its way" : "arrived")}; " +
                     (own >= 0 ? $"this pointer put it there itself {own:0}ms ago, so the game's report lagged"
                               : $"not anywhere this pointer put it in the last {ReportedFor.TotalSeconds:0}s") +
                     $"; sent, newest first: {Trail(_recent.Select(r => (r.Point, r.At)), now)}" +
                     $"; the game's reports, newest first: {Trail(_reports, now)}";

        return true;
    }

    /// <summary>The squared distance from a point to the segment between a and b.</summary>
    private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var along = b - a;
        var length = along.LengthSquared();

        if (length <= 0f)
            return Vector2.DistanceSquared(point, a);

        var t = Math.Clamp(Vector2.Dot(point - a, along) / length, 0f, 1f);

        return Vector2.DistanceSquared(point, a + along * t);
    }

    /// <summary>
    /// Where the game reported the cursor at each check, for the last ReportedFor, for StopDetail only: with the sends
    /// beside them, a stop says whether the game's report was trailing this pointer's path or somewhere else.
    /// </summary>
    private readonly List<(Vector2 Point, TimeSpan At)> _reports = new();

    private void RememberReport(Vector2 here, TimeSpan now)
    {
        _reports.Add((here, now));

        var stale = 0;

        while (stale < _reports.Count && now - _reports[stale].At > ReportedFor)
            stale++;

        _reports.RemoveRange(0, stale);
    }

    /// <summary>Up to 16 points, newest first, as (x,y)-ms ago, for StopDetail.</summary>
    private static string Trail(IEnumerable<(Vector2 Point, TimeSpan At)> points, TimeSpan now) =>
        string.Join(" ", points.Reverse().Take(16).Select(p => $"({p.Point.X:0},{p.Point.Y:0})-{(now - p.At).TotalMilliseconds:0}"));

    /// <summary>Whether an asked-for move has gone unconfirmed for longer than it should have.</summary>
    public bool Expired() =>
        Awaiting && ArriveTimeoutMs > 0 && (Now() - _placedAt).TotalMilliseconds > ArriveTimeoutMs;

    /// <summary>
    /// Gives up the cursor and stays given up, until the caller acknowledges it.
    ///
    /// Sticky on purpose. A caller polling every tick would otherwise carry straight on the moment
    /// the player's hand came to rest, which is the opposite of what taking the mouse back means.
    /// </summary>
    /// <summary>Stops with what was seen, for StopDetail. See Stop.</summary>
    public void Stop(StopReason reason, string detail)
    {
        StopDetail = detail;
        Stop(reason);
    }

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

    /// <summary>
    /// What to press on the way, and how long before arrival: a draw from HumanTiming per journey, so the modifier goes
    /// down 50 to 100 ms before the cursor lands - mid-move on a longer journey, at the start of one shorter than that.
    /// It was a fraction of the way along, 55 to 85%, which on a 100 ms hop meant 15 to 45 ms before the click.
    /// </summary>
    private Keys[] _modifiers = [];
    private int _modifierLeadMs;
    private bool _modifiersDown;

    /// <summary>
    /// How long a journey must wait before setting off, in milliseconds: while a click's button is still down, so the
    /// cursor does not move with it held, which the game would read as a drag. Asked at each Place; none when unset.
    /// </summary>
    public Func<int> MoveDelayMs { get; set; }

    /// <summary>One draw of how long before arrival a journey's modifier goes down. The Modifier Timing settings.</summary>
    public Func<int> ModifierLeadMs { get; set; }

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

    /// <summary>
    /// Told that this journey's modifiers are already down, held over from the click before it, so it presses nothing.
    /// See Stroke.HandOver.
    /// </summary>
    public void ModifiersAlreadyDown() => _modifiersDown = _modifiers.Length > 0;

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
    ///
    /// **The drift is the habit; each click scatters about it.** On its own the drift moved a couple of pixels a
    /// click, so a button pressed once a reforge was struck in nearly the same spot every time (2026-10-10). So the
    /// spot is the drift, at half its old reach, plus a spot of this target's own (see HabitOf), a little short of the
    /// centre on the side the cursor comes from; and each click lands off it by a bell curve, longer along the
    /// approach than across it. Everything stays within the spread, as the drift alone did: the arrival tolerance is
    /// bounded by the aim's distance from the edge (see BoundFor). The fractions are chosen, not measured.
    /// </summary>
    public Vector2 PointIn(RectangleF rect, Vector2? from = null)
    {
        _drift += DriftStep + (float)_rng.NextDouble() * DriftStep;
        return PointIn(rect, SpreadPercent, _drift, from, HabitOf(rect), _rng);
    }

    /// <summary>
    /// The same, without needing a pointer or a game to ask it. With no source of scatter, the spot alone: the drift and
    /// the target's habit, and the undershoot when the cursor's place is given.
    /// </summary>
    internal static Vector2 PointIn(RectangleF rect, float spreadPercent, float drift, Vector2? from = null,
        Vector2 habit = default, Random scatter = null)
    {
        var spread = Math.Clamp(spreadPercent, 0f, 95f) / 100f;
        var half = new Vector2(rect.Width / 2f, rect.Height / 2f);

        if (spread <= 0f || half.X <= 0f || half.Y <= 0f)
            return rect.Center;

        // Where on the rect, from -1 to 1 each way across its half-size. Two samples far apart on the second axis, so
        // the horizontal and vertical wander are not the same walk: from one line each, they would move together and
        // the aim would slide along a diagonal.
        var u = PerlinNoise.Full(drift, 0f) * HabitWander + habit.X;
        var v = PerlinNoise.Full(0f, drift + 512f) * HabitWander + habit.Y;

        // Along the approach and across it, in pixels; nought with the cursor on the centre or no place given.
        var towards = from is { } at ? rect.Center - at : Vector2.Zero;
        var along = towards.Length() > 1f ? towards / towards.Length() : Vector2.Zero;
        var across = new Vector2(-along.Y, along.X);

        // How far the rect reaches along a direction, in pixels, at the spread.
        float Reach(Vector2 direction) => (Math.Abs(direction.X) * half.X + Math.Abs(direction.Y) * half.Y) * spread;

        var spot = new Vector2(u * spread, v * spread) - along * Undershoot * Reach(along) / half;

        // A click scattered outside the spread is drawn again rather than pushed onto its edge, which drew a line of
        // clicks along it (picture of 2026-10-10). The spot is always well inside: the drift and the habit reach
        // HabitWander + HabitOffset of the spread and the undershoot Undershoot, under one between them. Clamped only
        // should every draw miss, which a scatter this narrow next to the spread all but never does.
        var landed = spot;

        for (var draw = 0; scatter != null && draw < ScatterDraws; draw++)
        {
            var offset = along == Vector2.Zero
                ? new Vector2(Normal(scatter) * ScatterAcross * half.X * spread, Normal(scatter) * ScatterAcross * half.Y * spread)
                : along * Normal(scatter) * ScatterAlong * Reach(along) + across * Normal(scatter) * ScatterAcross * Reach(across);

            landed = spot + offset / half;

            if (Math.Abs(landed.X) <= spread && Math.Abs(landed.Y) <= spread)
                break;
        }

        u = Math.Clamp(landed.X, -spread, spread);
        v = Math.Clamp(landed.Y, -spread, spread);

        return new Vector2(rect.X + half.X * (1f + u), rect.Y + half.Y * (1f + v));
    }

    /// <summary>How many times a click scattered outside the spread is drawn again before it is clamped onto the edge.</summary>
    private const int ScatterDraws = 16;

    /// <summary>The drift's reach, as a share of the spread. Half of it: the scatter and the target's habit take the rest.</summary>
    private const float HabitWander = 0.5f;

    /// <summary>How far short of the centre a click lands on average, as a share of the rect's reach along the approach.</summary>
    private const float Undershoot = 0.1f;

    /// <summary>The scatter's standard deviation along the approach, as a share of the rect's reach that way.</summary>
    private const float ScatterAlong = 0.3f;

    /// <summary>The scatter's standard deviation across the approach, as a share of the rect's reach that way.</summary>
    private const float ScatterAcross = 0.18f;

    /// <summary>How far a target's own spot may sit from the centre each way, as a share of the spread.</summary>
    private const float HabitOffset = 0.25f;

    /// <summary>A standard normal draw, by Box-Muller.</summary>
    private static float Normal(Random random)
    {
        var a = 1.0 - random.NextDouble();
        var b = random.NextDouble();

        return (float)(Math.Sqrt(-2.0 * Math.Log(a)) * Math.Cos(2.0 * Math.PI * b));
    }

    /// <summary>
    /// Each target's own spot, by its rect to the nearest 8 pixels: up to HabitOffset of the spread from the centre each
    /// way, drawn the first time it is aimed at and kept for the session. A hand that presses one button over and over
    /// favours a place on it, and a different place on the next button.
    /// </summary>
    private Vector2 HabitOf(RectangleF rect)
    {
        var key = ((int)MathF.Round(rect.X / 8f), (int)MathF.Round(rect.Y / 8f), (int)MathF.Round(rect.Width / 8f),
            (int)MathF.Round(rect.Height / 8f));

        if (_habits.TryGetValue(key, out var kept))
            return kept;

        // Bounded: a session aims at many rects, most once.
        if (_habits.Count >= MostHabits)
            _habits.Clear();

        return _habits[key] = new Vector2(((float)_rng.NextDouble() * 2f - 1f) * HabitOffset,
            ((float)_rng.NextDouble() * 2f - 1f) * HabitOffset);
    }

    private readonly Dictionary<(int, int, int, int), Vector2> _habits = new();

    private const int MostHabits = 256;

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
            _planned = false;
            _loiter = null;

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

        var target = PointIn(rect, here);

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
    public void Place(Vector2 client, params Keys[] modifiers) =>
        PlaceAlong(client, modifiers, NextJourney(), ModifierLeadMs?.Invoke() ?? HumanTiming.Ms(_rng));

    /// <summary>
    /// Where on the noise the next journey sits: advanced once per journey, and by an uneven step so the wander is not
    /// periodic - the same reason the aim's own line is stepped unevenly. Taken ahead of time by a planned queue of
    /// clicks, so the journeys it draws are the ones made. See ClickPlan.
    /// </summary>
    public float NextJourney()
    {
        _journey += JourneyStep + (float)_rng.NextDouble() * JourneyStep;
        return _journey;
    }

    /// <summary>
    /// Place, with the journey's drift and its modifier's lead given rather than drawn: for a planned click, so the
    /// journey made is the one drawn. See ClickPlan.
    /// </summary>
    /// <summary>Aim, at a point planned ahead with its journey's drift, lead and timing given. See ClickPlan.</summary>
    /// <summary>A draw from this pointer's random source, from nought to one. See ClickPlan.Make.</summary>
    internal double Chance() => _rng.NextDouble();

    /// <summary>
    /// The game window's size, the extent of the client space points are given in, or zero when it cannot be read - with
    /// no game, as in the tests. See ClickPlan.Make, which keeps its curves inside it.
    /// </summary>
    internal Vector2 ClientSize
    {
        get
        {
            try
            {
                var window = _gc?.Window.GetWindowRectangle();

                return window is { Width: > 0f, Height: > 0f } rect ? new Vector2(rect.Width, rect.Height) : Vector2.Zero;
            }
            catch
            {
                return Vector2.Zero;
            }
        }
    }

    public void AimAlong(RectangleF rect, Vector2 target, Keys[] modifiers, float drift, int modifierLeadMs, float minMs,
        float msPerRootPx, Vector2? control1 = null, Vector2? control2 = null, Travel.Flourish? shape = null)
    {
        _stayedAt = null;
        PlaceAlong(target, modifiers, drift, modifierLeadMs, minMs, msPerRootPx, control1, control2, shape);

        // After Place, which resets it: the bound belongs to this target, not the last one.
        _bound = BoundFor(rect, target);
    }

    public void PlaceAlong(Vector2 client, Keys[] modifiers, float drift, int modifierLeadMs, float? minMs = null,
        float? msPerRootPx = null, Vector2? control1 = null, Vector2? control2 = null, Travel.Flourish? shape = null)
    {
        var shortest = minMs ?? MinTravelMs;
        var perRootPx = msPerRootPx ?? TravelMsPerRootPx;

        // A move asked for ends any loitering. See Loiter.
        _loiter = null;

        // A planned journey - drift and timing given - counts as arrived once it has put the cursor on its point. See
        // Arrived.
        var afterPlanned = _planned;

        _planned = minMs != null;

        // Where the cursor is now goes on the record before anything is sent, because for the next
        // frame or several that is what the game will still be reporting. Without it the very
        // first move of a session reads its own starting point as the player having moved the
        // mouse, and stops before it has gone anywhere.
        var reported = Where();
        Remember(reported);

        // **A planned journey after a planned one sets off from where the last one put the cursor**, not from the game's
        // report of it. A planned journey is arrived once it has put the cursor down, before the report catches up, so
        // the report read here can trail the cursor - and a journey set off from it first snapped the cursor back there,
        // a jump the player saw as a teleport now and then (2026-10-11). Only within LastPutCounts of that put, and only
        // after a planned journey: anything else, the report is the answer.
        var from = _planned && afterPlanned && _lastPut is { } put && Now() - _lastPutAt < LastPutCounts ? put : reported;

        Expected = client;
        Awaiting = true;
        _bound = float.MaxValue;
        _placedAt = Now();

        _modifiers = modifiers ?? [];
        _modifierLeadMs = modifierLeadMs;
        _modifiersDown = false;

        var delayMs = Math.Max(0, MoveDelayMs?.Invoke() ?? 0);

        LastMoveDelayMs = delayMs;
        LastMoveShape = shape ?? Travel.Flourish.None;

        // Nought means teleport, which is what this did before travel existed and is worth keeping
        // - it is the setting to reach for when something is going wrong and the question is
        // whether the path is why. With a button still down it waits as a journey of no length.
        if (perRootPx <= 0)
        {
            PressModifiers();

            if (delayMs > 0)
            {
                _travel = new Travel(from, client, 1f, 0f, 0f, drift, Clock, delayMs);
                LastMoveTravelMs = _travel.Ms;
                return;
            }

            _travel = null;
            LastMoveTravelMs = 0f;
            Put(client);
            return;
        }

        _travel = new Travel(from, client, shortest, perRootPx, ArcPercent, drift, Clock, delayMs);

        // A planned journey glides along the queue's curve. See Travel.Glide.
        if (control1 is { } c1 && control2 is { } c2)
            _travel.Glide(c1, c2, ClickPlan.CreepPx);

        if (shape is { } flourish)
            _travel.Shape(flourish);

        LastMoveTravelMs = _travel.Ms;
    }

    /// <summary>
    /// Advances the journey. Called every tick by whoever owns this pointer.
    ///
    /// The modifier goes down partway rather than on arrival, which is the whole point of the
    /// journey existing: it overlaps the press with the approach the way a hand does, and it gives
    /// the key the length of the approach to register rather than making the pointer wait on the
    /// target while it confirms.
    /// </summary>
    /// <summary>
    /// Whether the journey is over with the cursor put on the point it was sent to, by this pointer's own record - no read
    /// of the game, so it can be asked between the HUD's frames. Not Arrived, which for an unplanned move waits for the
    /// game to report the cursor there. See ExileInput2's click on arrival.
    /// </summary>
    public bool PutOnTarget => Stopped == StopReason.None && _travel == null && _lastPut is { } put &&
                               Vector2.DistanceSquared(put, Expected) < 0.25f;

    /// <summary>
    /// Whether the journey is moved by something other than Step - ExileInput2's input thread - so Step only checks.
    /// See MoveAlong.
    /// </summary>
    public bool MovedElsewhere { get; set; }

    /// <summary>
    /// The checks a step makes - the player taking the mouse, a move that never lands - and, unless MovedElsewhere, the
    /// journey moved on. Called every tick by whoever owns this pointer.
    /// </summary>
    public void Step()
    {
        if (Stopped != StopReason.None)
            return;

        // Before advancing, not after: a step that has already moved the cursor has overwritten the
        // evidence of where the player put it. Not while the player is being ignored. See IgnorePlayerFor.
        if (Now() >= _playerIgnoredUntil && Interfered())
        {
            Stop(StopReason.PlayerMovedTheMouse);
            return;
        }

        if (Expired())
        {
            var found = Where();

            StopDetail = $"sent to ({Expected.X:0},{Expected.Y:0}) {(Now() - _placedAt).TotalMilliseconds:0}ms ago, the cursor read at " +
                         $"({found.X:0},{found.Y:0}), {Vector2.Distance(found, Expected):0.#} px off where {Accepts:0.#} px counts as arrived" +
                         (_travel != null ? ", still on its way" : "");
            Stop(StopReason.MoveNeverLanded);
            return;
        }

        if (!MovedElsewhere)
            MoveAlong();
    }

    /// <summary>
    /// Moves the journey on and puts the cursor where it now is, pressing the journey's modifiers when their lead comes.
    /// Reads nothing of the game's, so it can be called between the HUD's frames. See MovedElsewhere.
    /// </summary>
    public void MoveAlong()
    {
        if (Stopped != StopReason.None)
            return;

        // No journey: a loiter, if one is in hand, goes on. See Loiter.
        if (_travel == null)
        {
            LoiterAlong();
            return;
        }

        // By the time stepped through, so a stall carries on rather than jumping: the most a step may move through is Most
        // Move Time Per Frame, the same every step - drawn afresh, 80 to 100% of a 25ms cap, ordinary frames over it moved
        // at a different pace each and the cursor stuttered (2026-10-11). 0 is no cap. See Travel.Advance.
        //
        // **Planned journeys only.** In a map the HUD stalled for 250 to 500ms every few seconds, with no garbage
        // collection in any of them (dump 2026-10-12 00:25:22), and capped, an autoloot move advanced 50ms a stall: it
        // froze and crept and took several times its length. A move racing to an item is better caught up in one jump.
        // The cap was for the hideout's planned runs, where a stall's jump read as a teleport across the inventory.
        _travel.Advance(_planned && MostMoveTimePerFrameMs > 0 ? TimeSpan.FromMilliseconds(MostMoveTimePerFrameMs) : TimeSpan.MaxValue);

        if (!_modifiersDown && _travel.RemainingMs <= _modifierLeadMs)
            PressModifiers();

        // Not put again where it already is: stepped every millisecond, most steps of a wait or a slow end move it by
        // nothing, and each put is a call to Windows and an entry in the sends the player's mouse is judged against.
        var at = _travel.Now();

        if (_travel.Done || _lastPut is not { } last || Vector2.DistanceSquared(last, at) >= 0.0625f)
            Put(at);

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
        _lastPut = client;
        _lastPutAt = Now();

        _trail.Add((_lastPutAt, client));

        while (_trail.Count > 0 && _lastPutAt - _trail[0].At > TrailFor)
            _trail.RemoveAt(0);

        if (Send != null)
        {
            Send(client);
            return;
        }

        var origin = _gc.Window.GetWindowRectangle().TopLeft;
        var screen = new Vector2(origin.X + client.X, origin.Y + client.Y);

        MouseBlock.OwnMoveTo((int)screen.X, (int)screen.Y);
        Input.SetCursorPos(screen);
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

        // **A planned journey has arrived when it has put the cursor on its point**, not when the game reports it there:
        // the report trails the cursor by a frame or more, and with a 3px tolerance waiting on it held every deposited
        // item 60 to 75ms after the cursor stopped (dump 2026-10-11 21:34:04). The click goes where the cursor is.
        if (!_planned && Drift() > Accepts)
            return false;

        Awaiting = false;
        return true;
    }

    /// <summary>The most of a journey's time one step moves through, in milliseconds. See Travel.Advance.</summary>
    public float MostMoveTimePerFrameMs { get; set; } = 50f;

    /// <summary>Whether the journey in hand was planned ahead. See Arrived.</summary>
    private bool _planned;

    /// <summary>Where this pointer put the cursor over the last TrailFor, oldest first. See Trail.</summary>
    private readonly List<(TimeSpan At, Vector2 Where)> _trail = [];

    /// <summary>How long a put stays in the trail drawn behind the cursor. Chosen.</summary>
    internal static readonly TimeSpan TrailFor = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// Where this pointer put the cursor over the last TrailFor, oldest first, each with its age as a share of TrailFor:
    /// for the planned-click drawing's trail. See ClickPlan.Draw.
    /// </summary>
    internal List<(Vector2 Where, float Age)> Trail()
    {
        var now = Now();

        return _trail.Where(x => now - x.At <= TrailFor)
            .Select(x => (x.Where, (float)((now - x.At).TotalMilliseconds / TrailFor.TotalMilliseconds))).ToList();
    }

    /// <summary>How far through its journey the cursor is, from nought to one; one with no journey going.</summary>
    internal float JourneyProgress => _travel?.Progress ?? 1f;

    /// <summary>Where this pointer last put the cursor, and when. See PlaceAlong.</summary>
    private Vector2? _lastPut;

    private TimeSpan _lastPutAt;

    /// <summary>How recent the last put must be for a planned journey to set off from it rather than the report.</summary>
    private static readonly TimeSpan LastPutCounts = TimeSpan.FromSeconds(1);

    /// <summary>The last journey's wait for a click's button before setting off, in milliseconds.</summary>
    public int LastMoveDelayMs { get; private set; }

    /// <summary>The last journey's own length in time, its wait not counted, in milliseconds; 0 for a teleport.</summary>
    public float LastMoveTravelMs { get; private set; }

    /// <summary>The last move's flourishes - a swoop's stretch, an overshoot - or none. For the caller's log.</summary>
    public Travel.Flourish LastMoveShape { get; private set; } = Travel.Flourish.None;

    /// <summary>Forgets any outstanding move, for a machine giving up on what it was doing.</summary>
    public void Forget()
    {
        Awaiting = false;
        _travel = null;
        _loiter = null;
        _modifiersDown = false;
    }

    /// <summary>
    /// Takes the cursor to a point and keeps it moving round it, idle: a circle, a figure of eight or a wander, drawn at
    /// random, about radius across, each loop LoiterLoopMs or so, starting from the point and easing in. For a caller
    /// waiting on the game with nothing to click yet - the reforging bench working on the tablet that will appear there.
    /// Every point goes through Put, so the player's own mouse is still told apart from it.
    ///
    /// Ends after forMs, with the cursor left where it has got to, or with none until something ends it: the next move
    /// asked for, StopLoitering, a stop, or the cursor given up.
    /// </summary>
    public void Loiter(Vector2 centre, float radius, int forMs = 0)
    {
        Place(centre);

        var shape = (LoiterShape)_rng.Next(3);

        _loiter = new LoiterState(centre, Math.Max(2f, radius * (0.7f + 0.6f * (float)_rng.NextDouble())), shape,
            LoiterLoopMs * (0.75f + 0.5f * (float)_rng.NextDouble()), (float)(_rng.NextDouble() * Math.PI * 2),
            _rng.NextDouble() < 0.5 ? -1f : 1f, (float)_rng.NextDouble() * 100f, null,
            forMs > 0 ? Now() + TimeSpan.FromMilliseconds(forMs) : null);
    }

    /// <summary>Whether a loiter is going: asked for and not yet ended. See Loiter.</summary>
    public bool Loitering => _loiter != null;

    /// <summary>Ends a loiter, the cursor left where it has got to. Nothing when none is going. See Loiter.</summary>
    public void StopLoitering() => _loiter = null;

    /// <summary>The shapes Loiter traces.</summary>
    private enum LoiterShape
    {
        Circle,
        FigureOfEight,
        Wander,
    }

    /// <summary>A loiter in hand: where, how big, which shape, how long a loop, turned how far and which way, and since when.</summary>
    private sealed record LoiterState(Vector2 Centre, float Radius, LoiterShape Shape, float LoopMs, float Angle, float Turn,
        float Seed, TimeSpan? Since, TimeSpan? Ends);

    private LoiterState _loiter;

    /// <summary>About how long one loop of a loiter takes, in milliseconds. Chosen.</summary>
    private const float LoiterLoopMs = 1400f;

    /// <summary>How long a loiter takes to grow to its full size from the point, in milliseconds. Chosen.</summary>
    private const float LoiterEaseMs = 500f;

    /// <summary>
    /// Puts the cursor where the loiter has got to, once the way to its point is over: the shape's place at this moment,
    /// nought at its start so it sets off from the point, grown in over LoiterEaseMs. See Loiter.
    /// </summary>
    private void LoiterAlong()
    {
        if (_loiter is not { } loiter)
            return;

        var now = Now();

        if (loiter.Ends is { } ends && now >= ends)
        {
            _loiter = null;
            return;
        }

        if (loiter.Since is not { } since)
        {
            // Arrived at the point, by this pointer's own put: nothing for the caller to confirm, and no move to expire.
            _loiter = loiter with { Since = now };
            Awaiting = false;
            return;
        }

        var ms = (float)(now - since).TotalMilliseconds;
        var t = ms / loiter.LoopMs * MathF.PI * 2f * loiter.Turn;
        var grown = Math.Clamp(ms / LoiterEaseMs, 0f, 1f);

        grown = grown * grown * (3f - 2f * grown);

        var offset = loiter.Shape switch
        {
            LoiterShape.Circle => new Vector2(MathF.Cos(t) - 1f, MathF.Sin(t)) * 0.5f,
            LoiterShape.FigureOfEight => new Vector2(MathF.Sin(t), MathF.Sin(t) * MathF.Cos(t)),
            _ => new Vector2(
                PerlinNoise.Full(loiter.Seed, ms / loiter.LoopMs * 2f) - PerlinNoise.Full(loiter.Seed, 0f),
                PerlinNoise.Full(loiter.Seed + 37f, ms / loiter.LoopMs * 2f) - PerlinNoise.Full(loiter.Seed + 37f, 0f)) * 0.6f,
        };

        var (sin, cos) = MathF.SinCos(loiter.Angle);
        var turned = new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
        var at = loiter.Centre + turned * loiter.Radius * grown;

        if (_lastPut is not { } last || Vector2.DistanceSquared(last, at) >= 0.0625f)
            Put(at);
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
        _playerIgnoredUntil = TimeSpan.Zero;
    }
}

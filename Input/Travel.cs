using System;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2;

/// <summary>
/// One journey of the cursor, from wherever it was to wherever it is going.
///
/// A teleport is not a fast movement, it is the absence of one - and it is the single most obvious
/// thing about automated input, because a human hand cannot do it. Everything else here is detail
/// on top of that.
///
/// Three things shape the path, and each is answering a different way a straight constant-speed
/// line reads wrong:
///
/// **Duration grows with distance, but slowly.** A hand crossing the screen does not take ten times
/// as long as one crossing a tenth of it - reaching is mostly aiming, and aiming costs about the
/// same either way. The square root is the cheap approximation of that; Fitts would have the log of
/// the distance over the target size, and the difference between the two is far smaller than the
/// jitter around either.
///
/// **The path bows.** A wrist and elbow rotate, so the hand travels an arc; only a mouse dragged
/// along a ruler goes straight. The bow is a fraction of the distance and its direction is chosen
/// per journey, so two moves between the same points do not trace the same line.
///
/// **The speed is not constant.** Movement accelerates away from rest and decelerates into the
/// target, which is what smoothstep does. A linear ramp arrives at full speed and stops dead, which
/// looks worse than a teleport rather than better - it is a thing no physical object does.
/// </summary>
internal sealed class Travel
{
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private readonly Vector2 _from;
    private readonly Vector2 _to;
    private readonly Vector2 _bow;
    private readonly float _ms;

    /// <summary>
    /// What time it is, for driving a journey by hand.
    ///
    /// A journey is a few hundred milliseconds of cursor moving too fast to watch, so the only way
    /// to examine one is to advance it yourself. Same reading-not-interval shape as the lease.
    /// </summary>
    private readonly Func<TimeSpan> _now;

    /// <summary>The injected clock's reading when the journey was made; unused with the stopwatch.</summary>
    private readonly TimeSpan _madeAt;

    /// <summary>How long after it was made the journey sets off. See MousePointer.MoveDelayMs.</summary>
    private readonly float _delayMs;

    /// <summary>Time since the journey was made, by the clock.</summary>
    private TimeSpan Real => _now != null ? _now() - _madeAt : _since.Elapsed;

    /// <summary>
    /// Time since the journey set off, negative while it waits to: by the clock, or once the pointer steps it, the time
    /// it has stepped through, no more than MostStep a step. See Advance.
    /// </summary>
    private TimeSpan Elapsed => (_stepped ? _steppedThrough : Real) - TimeSpan.FromMilliseconds(_delayMs);

    private bool _stepped;
    private TimeSpan _steppedThrough;
    private TimeSpan _lastReal;

    /// <summary>
    /// The most of the journey one step moves through. **A stall is not a jump**: the whole process stopped for 82.8ms of
    /// garbage collection in one window (dump 2026-10-11 22:06:31), and by the clock alone the next step put the cursor
    /// most of a 130ms journey further on at once - a teleport, as the player saw it. Stepped through at this at most,
    /// the journey picks up where it was and takes that much longer. Chosen: a step is 6 to 16ms at the HUD's rates.
    /// </summary>
    internal static readonly TimeSpan MostStep = TimeSpan.FromMilliseconds(25);

    /// <summary>Moves the journey on by the time since the last step, MostStep at most. Called by the pointer each step.</summary>
    public void Advance()
    {
        var real = Real;
        var step = real - _lastReal;

        _lastReal = real;
        _stepped = true;

        if (step > MostStep)
            step = MostStep;

        if (step > TimeSpan.Zero)
            _steppedThrough += step;
    }

    /// <param name="drift">
    /// Where on the noise this journey sits. Advanced a little per journey by whoever owns the
    /// pointer, so consecutive reaches bow SIMILARLY rather than independently - see the comment on
    /// the bow itself.
    /// </param>
    /// <param name="delayMs">How long the journey waits before setting off: while a click's button is still down.</param>
    public Travel(Vector2 from, Vector2 to, float minMs, float msPerRootPx, float arcPercent,
        float drift, Func<TimeSpan> now = null, float delayMs = 0f)
    {
        _now = now;
        _madeAt = now?.Invoke() ?? TimeSpan.Zero;
        _delayMs = Math.Max(0f, delayMs);

        _from = from;
        _to = to;

        var line = to - from;
        var distance = line.Length();

        // Give or take DurationSpread, from the noise: a run of equal hops otherwise kept the beat of a metronome. Sampled
        // faster than the bow so neighbouring journeys differ, and well away from it so a longer journey is not always a
        // more bowed one.
        _ms = Math.Max(1f, (minMs + (float)Math.Sqrt(distance) * msPerRootPx) *
                           (1f + DurationSpread * PerlinNoise.Full(drift * 2.5f + 2048f, 0.5f)));

        // Perpendicular to the line, a fraction of its length, to one side or the other. Scaled by
        // the distance rather than fixed, or a short hop would bow as far as a long sweep and look
        // like a flick rather than a reach.
        //
        // How far, and which side, comes from the noise rather than from a fresh random number. A
        // hand reaching between the same two places over and over does not curve left, then hard
        // right, then left again - it takes much the same path each time and that path wanders over
        // minutes. Independent draws produce a journey unrelated to the one before it, which is the
        // same tell the aim had before it drifted.
        if (distance > 1f)
        {
            var perpendicular = new Vector2(-line.Y, line.X) / distance;
            var reach = distance * arcPercent / 100f;

            _bow = perpendicular * reach * PerlinNoise.Full(drift, 0f);
        }
    }

    /// <summary>How far a journey's duration may stray either way from its distance's own, as a fraction. Chosen.</summary>
    internal const float DurationSpread = 0.15f;

    /// <summary>
    /// The power the journey's time is raised to before the easing, under one: the first half of the time covers more
    /// than half the way, and the slowing into the target takes longer than the speeding away. 0.85 puts about 58% of
    /// the way at half time and the peak speed at about four tenths of it. Chosen, as a hand's reach is commonly
    /// described, not measured.
    /// </summary>
    internal const float ApproachSkew = 0.85f;

    /// <summary>How long this journey was given, in milliseconds.</summary>
    public float Ms => _ms;

    /// <summary>How far through the journey, from nought to one.</summary>
    public float Progress => ProgressAt((float)Elapsed.TotalMilliseconds);

    public bool Done => Progress >= 1f;

    /// <summary>How long is left of the journey, in milliseconds, its wait to set off included.</summary>
    public float RemainingMs => Math.Max(0f, _ms - (float)Elapsed.TotalMilliseconds);

    /// <summary>How far through the journey a given elapsed time is.</summary>
    public float ProgressAt(float ms) => Math.Clamp(ms / _ms, 0f, 1f);

    /// <summary>Where the cursor should be this instant.</summary>
    public Vector2 Now()
    {
        if (!_glide)
            return At(Progress);

        var elapsed = (float)Elapsed.TotalMilliseconds;

        // Waiting on a click's button: creeping along the start of the way, CreepPx at most by the time it is up.
        if (elapsed < 0f)
            return Curve(_delayMs > 0f ? _creep * Math.Clamp(1f + elapsed / _delayMs, 0f, 1f) : 0f);

        return Curve(_creep + (1f - _creep) * Eased(ProgressAt(elapsed)));
    }

    /// <summary>
    /// Makes this a gliding journey, for a click planned in a queue: along a cubic curve through these two control
    /// points - set so the way into one click already heads for the next, and the queue reads as one curve - creeping
    /// up to creepPx along it while a click's button is still down, and arriving still moving at EndSpeed of its pace
    /// rather than at rest. A click a little in motion is what a hand makes; the game takes it as a click, not a drag.
    /// </summary>
    public void Glide(Vector2 control1, Vector2 control2, float creepPx)
    {
        _glide = true;
        _control1 = control1;
        _control2 = control2;

        // The curve's own parameter does not run evenly along it - near a control point it covers more ground - so
        // the share crept is found by distance along the curve, not as creepPx over the length.
        _creep = 0f;

        if (Vector2.Distance(_from, _to) <= 1f || creepPx <= 0f)
            return;

        float low = 0f, high = MostCreep;

        if (Vector2.Distance(_from, Curve(high)) <= creepPx)
        {
            _creep = high;
            return;
        }

        for (var i = 0; i < 20; i++)
        {
            var mid = (low + high) / 2f;

            if (Vector2.Distance(_from, Curve(mid)) < creepPx)
                low = mid;
            else
                high = mid;
        }

        _creep = low;
    }

    private bool _glide;
    private Vector2 _control1, _control2;

    /// <summary>The share of the way crept while waiting on a click's button. See Glide.</summary>
    private float _creep;

    /// <summary>The most of a journey crept while waiting: on a short hop CreepPx would be much of the way.</summary>
    private const float MostCreep = 0.15f;

    /// <summary>
    /// A gliding journey's speed into its click, as a share of its pace blended into the easing: smoothstep alone
    /// arrives at rest. 0.2, so it arrives still moving at about the speed the next journey creeps at. Chosen, not
    /// measured.
    /// </summary>
    internal const float EndSpeed = 0.2f;

    /// <summary>How far along the way a fraction of the time is: smoothstep over skewed time, and for a gliding journey
    /// blended with the time itself so it is not at rest at either end. See ApproachSkew and EndSpeed.</summary>
    private float Eased(float t)
    {
        var u = MathF.Pow(Math.Clamp(t, 0f, 1f), ApproachSkew);
        var smooth = u * u * (3f - 2f * u);

        return _glide ? (1f - EndSpeed) * smooth + EndSpeed * u : smooth;
    }

    /// <summary>
    /// The point a share of the way along the path: the cubic curve of a gliding journey, else the quadratic bow.
    /// </summary>
    private Vector2 Curve(float s)
    {
        s = Math.Clamp(s, 0f, 1f);

        if (_glide)
        {
            var r = 1f - s;

            return r * r * r * _from + 3f * r * r * s * _control1 + 3f * r * s * s * _control2 + s * s * s * _to;
        }

        // A quadratic bezier through a control point off the midpoint, which is the cheapest curve
        // that starts and ends exactly where it must. The bow is doubled because a bezier only
        // reaches half way to its control point.
        var control = (_from + _to) / 2f + _bow * 2f;

        var a = Vector2.Lerp(_from, control, s);
        var b = Vector2.Lerp(control, _to, s);

        return Vector2.Lerp(a, b, s);
    }

    /// <summary>
    /// Where the cursor should be a given fraction of the way through.
    ///
    /// Split out from Now so the path can be examined without waiting for it. A journey is the one
    /// thing here that cannot be checked by looking at it - it is a few hundred milliseconds of
    /// cursor somewhere off screen - so the alternative to driving it by hand is not testing it.
    /// </summary>
    public Vector2 At(float t) => Curve(Eased(t));

}

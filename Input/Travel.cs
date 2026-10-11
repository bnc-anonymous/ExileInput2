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

    /// <summary>Time since the journey set off: negative while it waits to.</summary>
    private TimeSpan Elapsed => (_now != null ? _now() - _madeAt : _since.Elapsed) - TimeSpan.FromMilliseconds(_delayMs);

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
    public Vector2 Now() => At(Progress);

    /// <summary>
    /// Where the cursor should be a given fraction of the way through.
    ///
    /// Split out from Now so the path can be examined without waiting for it. A journey is the one
    /// thing here that cannot be checked by looking at it - it is a few hundred milliseconds of
    /// cursor somewhere off screen - so the alternative to driving it by hand is not testing it.
    /// </summary>
    public Vector2 At(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        // Smoothstep over skewed time: still at both ends, quickest before the middle, slowest into the target. See
        // ApproachSkew.
        var u = MathF.Pow(t, ApproachSkew);
        var eased = u * u * (3f - 2f * u);

        // A quadratic bezier through a control point off the midpoint, which is the cheapest curve
        // that starts and ends exactly where it must. The bow is doubled because a bezier only
        // reaches half way to its control point.
        var control = (_from + _to) / 2f + _bow * 2f;

        var a = Vector2.Lerp(_from, control, eased);
        var b = Vector2.Lerp(control, _to, eased);

        return Vector2.Lerp(a, b, eased);
    }

}

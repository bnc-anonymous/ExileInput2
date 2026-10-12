using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2.Tests;

/// <param name="Distance">How far the journey goes, in pixels.</param>
/// <param name="Arc">The bow setting, as a percentage of the distance.</param>
internal sealed record TravelCase(string Name, float Distance, float Arc, string Reason)
{
    public float MinMs { get; init; } = 60f;
    public float MsPerRootPx { get; init; } = 8f;

    /// <summary>How long the journey must take, where the case pins it.</summary>
    public float? WantMs { get; init; }

    /// <summary>Whether the path must leave the straight line between the two points.</summary>
    public bool? WantBowed { get; init; }

    /// <summary>Whether the first tenth of the time must cover less than a tenth of the way.</summary>
    public bool WantEased { get; init; }

    /// <summary>Whether one journey's duration should differ from the next one's, the same distance apart.</summary>
    public bool WantVaried { get; init; }

    /// <summary>Whether the first half of the time should cover more than half the way: slower into the target than away.</summary>
    public bool WantFrontLoaded { get; init; }
}

/// <summary>
/// The shape of a single journey of the cursor.
///
/// This is the one part of the plugin that cannot be checked by looking at it. It is a few hundred
/// milliseconds of cursor moving too fast to follow, and the failure it exists to prevent is not a
/// wrong click but a right click that arrived in a way no hand could produce. So the alternative to
/// driving it by hand here is not testing it at all.
///
/// Every case is a property rather than a fixture: the bow direction is chosen per journey, so
/// pinning coordinates would pin the random number generator instead of the movement.
///
/// Case 1 is the one that matters most and looks the most trivial. Whatever the curve does in
/// between, the journey has to start exactly where the cursor is and end exactly where it was
/// aimed, or every tolerance in the plugin is measuring against a target the movement never
/// intended to reach.
/// </summary>
internal static class TravelCases
{
    /// <summary>The furthest a path strays from the straight line, which is its bow made visible.</summary>
    private static float Stray(Travel travel)
    {
        var straightFrom = travel.At(0f);
        var straightTo = travel.At(1f);
        var worst = 0f;

        for (var t = 0.05f; t < 1f; t += 0.05f)
        {
            var along = Vector2.Lerp(straightFrom, straightTo, t);
            worst = Math.Max(worst, Vector2.Distance(travel.At(t), along));
        }

        return worst;
    }

    /// <summary>The average change between consecutive values.</summary>
    private static float Steps(List<float> values)
    {
        var total = 0f;

        for (var i = 1; i < values.Count; i++)
            total += Math.Abs(values[i] - values[i - 1]);

        return values.Count > 1 ? total / (values.Count - 1) : 0f;
    }

    private const int Journeys = 200;

    internal static readonly TravelCase[] All =
    [
        new("1  Starts where the cursor is and ends exactly on the target", 500f, 15f,
            "the curve is free to do what it likes in between, but not at the ends. A bezier only " +
            "reaches half way to its control point, which is why the bow is doubled - get that " +
            "wrong and every journey stops short by a fraction of its own length, which would look " +
            "exactly like a tolerance problem and be nothing of the kind")
        {
            WantBowed = true,
        },

        new("2  Duration grows with distance, but slowly", 10000f, 15f,
            "a hand crossing the screen does not take ten times as long as one crossing a tenth of " +
            "it, because reaching is mostly aiming and aiming costs about the same either way. A " +
            "hundred pixels comes to 140ms and ten thousand to 860ms - a hundredfold in distance " +
            "for a sixfold in time. Fitts would use the log of distance over target size; the " +
            "difference between that and a square root is far smaller than the jitter around either")
        {
            WantMs = 860f,
        },

        new("3  A short hop still takes a moment", 100f, 15f,
            "140ms, of which 60 is the floor. A teleport is not a fast movement, it is the absence " +
            "of one, and it is the single most obvious thing about automated input")
        {
            WantMs = 140f,
        },

        new("4  A journey to where the cursor already is does not take nought", 0f, 15f,
            "the floor with nothing added. Worth pinning because a zero-length move is the case a " +
            "square root is most likely to be strange about, and because a machine that re-aims at " +
            "the thing it is already on should not be the one place the cursor jumps")
        {
            WantMs = 60f,
            WantBowed = false,
        },

        new("5  Turning the bow off makes the path straight", 500f, 0f,
            "the setting to reach for when something is going wrong and the question is whether " +
            "the path is why. It has to actually be straight for that answer to mean anything")
        {
            WantBowed = false,
        },

        new("6  Speed is not constant", 500f, 15f,
            "movement accelerates away from rest and decelerates into the target, which is what " +
            "smoothstep does. A linear ramp arrives at full speed and stops dead - a thing no " +
            "physical object does, and one that looks worse than a teleport rather than better")
        {
            WantEased = true,
            WantBowed = true,
        },

        new("10 One journey does not take exactly as long as the last", 500f, 15f,
            "a hand repeating a reach does not repeat its timing to the millisecond. With every hop " +
            "between neighbouring tablets the same length and the same duration, a run of currency uses " +
            "had the beat of a metronome. The duration wanders with the noise, as the bow does")
        {
            WantVaried = true,
        },

        new("11 Slower into the target than away from it", 500f, 15f,
            "a reach speeds up quickly and spends longer homing in. Smoothstep is symmetric, the same " +
            "acceleration away as deceleration in, and every hop arrived with the same even settle")
        {
            WantFrontLoaded = true,
            WantEased = true,
        },

        new("7  Nought per root pixel teleports, deliberately", 500f, 15f,
            "the escape hatch, and the behaviour every machine had before any of this existed. It " +
            "is checked here so that turning it on is a supported answer rather than a setting " +
            "nobody has tried since the movement went in")
        {
            MsPerRootPx = 0f,
            MinMs = 0f,
            WantMs = 1f,
        },
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- the shape of a journey ------------------------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            var rng = new Random(20260818);
            var from = new Vector2(200f, 300f);
            var to = from + new Vector2(c.Distance, 0f);

            var endsExact = true;
            var startsExact = true;
            var bowed = false;
            var eased = true;
            var ms = 0f;
            var durations = new List<float>();
            var halfWay = 1f;
            var worstOff = 0f;
            var drift = 0f;

            for (var j = 0; j < Journeys; j++)
            {
                // A journey's place on the noise, advanced unevenly as the pointer does it.
                drift += 0.19f + (float)rng.NextDouble() * 0.19f;

                var travel = new Travel(from, to, c.MinMs, c.MsPerRootPx, c.Arc, drift);
                ms = travel.Ms;
                durations.Add(ms);
                halfWay = Math.Min(halfWay, (travel.At(0.5f).X - from.X) / Math.Max(1f, c.Distance));

                if (Vector2.Distance(travel.At(0f), from) > 0.001f)
                    startsExact = false;

                if (Vector2.Distance(travel.At(1f), to) > 0.001f)
                    endsExact = false;

                // How far the path strays from the straight line, sampled along it. The journey
                // runs along X here, so the stray is simply the Y difference.
                for (var s = 1; s < 10; s++)
                {
                    var off = Math.Abs(travel.At(s / 10f).Y - from.Y);

                    worstOff = Math.Max(worstOff, off);

                    if (off > 1f)
                        bowed = true;
                }

                // Smoothstep: a tenth of the time covers well under a tenth of the way. Measured
                // along X, which is the axis the journey actually travels.
                var tenth = (travel.At(0.1f).X - from.X) / Math.Max(1f, c.Distance);

                if (tenth >= 0.1f)
                    eased = false;
            }

            // A duration is the distance's own give or take the journey's spread, never further. See Travel.DurationSpread.
            var shortest = durations.Min();
            var longest = durations.Max();
            var within = c.WantMs == null || durations.All(x =>
                x >= c.WantMs.Value * (1f - Travel.DurationSpread) - 0.5f && x <= c.WantMs.Value * (1f + Travel.DurationSpread) + 0.5f);
            var varied = longest - shortest >= 0.1f * durations.Average() && Steps(durations) >= 0.01f * durations.Average();
            var frontLoaded = halfWay > 0.55f;

            var ok = startsExact && endsExact
                     && within
                     && (!c.WantVaried || varied)
                     && (!c.WantFrontLoaded || frontLoaded)
                     && (c.WantBowed == null || bowed == c.WantBowed.Value)
                     && (!c.WantEased || eased);

            if (!ok)
                failed++;

            var wants = new List<string> { "both ends exact" };

            if (c.WantMs != null)
                wants.Add($"{c.WantMs:0}ms, give or take {Travel.DurationSpread * 100f:0}%");
            if (c.WantVaried)
                wants.Add("durations differing journey to journey by 10% or more overall");
            if (c.WantFrontLoaded)
                wants.Add("over half the way covered in half the time");
            if (c.WantBowed == true)
                wants.Add("a bowed path");
            if (c.WantBowed == false)
                wants.Add("a straight path");
            if (c.WantEased)
                wants.Add("a tenth of the time covering under a tenth of the way");

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        given {c.Distance:n0}px, bow {c.Arc:0}%, " +
                              $"{c.MinMs:0}ms floor + {c.MsPerRootPx:0}ms per root px");
            Console.WriteLine($"        want  {string.Join("; ", wants)}");
            Console.WriteLine($"        got   {shortest:0}-{longest:0}ms, {halfWay * 100f:0}% of the way at half time, straying up to {worstOff:0.#}px off the " +
                              $"straight line over {Journeys} journeys, " +
                              $"{(startsExact && endsExact ? "both ends exact" : "AN END ADRIFT")}" +
                              $"{(eased ? "" : ", NOT EASED")}");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        // Not a case of its own because it is about the timing, not about a journey: how long a button is held, how long
        // before arrival a modifier goes down and how long after the button it comes up are each one draw of this. See
        // HumanTiming.
        var rngAt = new Random(20260818);
        var draws = Enumerable.Range(0, 10000).Select(_ => HumanTiming.Ms(rngAt)).ToArray();
        var mean = draws.Average();
        var commonest = draws.GroupBy(d => d / 10).OrderByDescending(g => g.Count()).First().Key * 10;
        var atOk = draws.Min() >= HumanTiming.Least && draws.Max() <= HumanTiming.Most &&
                   mean is > 74d and < 79.5d && commonest is >= 70 and < 90;

        if (!atOk)
            failed++;

        Console.WriteLine($"{(atOk ? "  ok  " : "FAIL  ")}8  A click's timings fall between 50 and 100ms, most often near 80");
        Console.WriteLine("        want  every draw from 50 to 100ms; the mean near 76.7, a triangle's; the commonest ten near 80");
        Console.WriteLine($"        got   {draws.Min()} to {draws.Max()}ms, mean {mean:0.0}, commonest {commonest}-{commonest + 9}ms");
        Console.WriteLine("        why   the player's own ctrl+clicks, recorded: the button held 62 to 110ms, ctrl down 47 to");
        Console.WriteLine("              141ms before the click. A click held for no time, as before, is a thing no hand does");
        Console.WriteLine();

        // A journey asked for while a click's button is still down waits for it: moving with the button held is a drag.
        var clock = TimeSpan.Zero;
        var waiting = new Travel(new Vector2(100f, 100f), new Vector2(400f, 300f), 60f, 8f, 0f, 0f, () => clock, 80f);
        clock = TimeSpan.FromMilliseconds(40);
        var stillAt40 = waiting.Progress == 0f && waiting.Now() == new Vector2(100f, 100f);
        var remainingAt40 = waiting.RemainingMs;
        clock = TimeSpan.FromMilliseconds(100);
        var movingAt100 = waiting.Progress > 0f;
        clock = TimeSpan.FromMilliseconds(80 + waiting.Ms + 1);
        var doneAfter = waiting.Done;
        var waitOk = stillAt40 && movingAt100 && doneAfter && Math.Abs(remainingAt40 - (waiting.Ms + 40f)) < 1f;

        if (!waitOk)
            failed++;

        Console.WriteLine($"{(waitOk ? "  ok  " : "FAIL  ")}9  A journey asked for with a click's button down waits for the button");
        Console.WriteLine("        want  at the start point until 80ms, moving after, done at 80ms plus its length");
        Console.WriteLine($"        got   still at 40ms {stillAt40}, moving at 100ms {movingAt100}, done after {doneAfter}, " +
                          $"remaining at 40ms {remainingAt40:0}, want {waiting.Ms + 40f:0}");
        Console.WriteLine("        why   the button of a click is held 50 to 100ms and the next move is often asked for at once;");
        Console.WriteLine("              moving with the button held is a drag");
        Console.WriteLine();

        // A planned queue of ctrl+clicks: the first presses ctrl with a lead of its own, the rest carry it; each journey
        // starts where the last landed and ends on its point; and a journey made from a planned drift follows the path
        // drawn for it. See ClickPlan.
        var planned = new MousePointer(null, new Random(20261011)) { MinTravelMs = 60f, TravelMsPerRootPx = 6f, ArcPercent = 7f };
        var planPoints = new[] { new Vector2(300f, 200f), new Vector2(360f, 210f), new Vector2(420f, 260f) };
        var ctrlOnly = Modifier.Of(true, false, false);
        var plan = ClickPlan.Make(planned, new Vector2(100f, 100f), [], planPoints, [false, true, false],
            [ctrlOnly, ctrlOnly, ctrlOnly], true, () => 64, () => 72, i => i == 0 ? 40f : 67f);
        var items = plan.Items;
        var leadsOk = items[0].LeadMs == 72 && !items[0].Carried && items.Skip(1).All(p => p.Carried && p.LeadMs == 0);
        var chainOk = items[0].From == new Vector2(100f, 100f) && items[1].From == planPoints[0] && items[2].From == planPoints[1];
        var endsOk = items.All(p => Vector2.Distance(p.Path.At(1f), p.To) < 0.01f);
        var again = new Travel(items[1].From, items[1].To, 67f, 6f, 7f, items[1].Drift);
        again.Glide(items[1].Control1, items[1].Control2, ClickPlan.CreepPx);
        again.Shape(items[1].Shape);
        var sameOk = Enumerable.Range(0, 11).All(k => Vector2.Distance(again.At(k / 10f), items[1].Path.At(k / 10f)) < 0.01f);
        var timedOk = items[0].MinMs == 40f && items[1].MinMs == 67f && items[2].MinMs == 67f;
        var planOk = leadsOk && chainOk && endsOk && sameOk && timedOk && items[1].Right && !items[0].Right;

        if (!planOk)
            failed++;

        Console.WriteLine($"{(planOk ? "  ok  " : "FAIL  ")}10 A planned queue of ctrl+clicks presses ctrl once and draws the journeys it makes");
        Console.WriteLine("        want  the first with a lead, the rest carried; each from the last point to its own; the same path again");
        Console.WriteLine($"        got   leads {string.Join("/", items.Select(p => p.LeadMs))}, carried " +
                          $"{string.Join("/", items.Select(p => p.Carried))}, chained {chainOk}, ends on its points {endsOk}, " +
                          $"made the same {sameOk}, each move's own shortest move {timedOk}");
        Console.WriteLine("        why   the plan is drawn on screen before it is made, so a journey made from it has to be the one");
        Console.WriteLine("              drawn; and a batch of ctrl+clicks holds ctrl through, as the player does");
        Console.WriteLine();

        // The glide: the way into a click heads where the way out leaves, the cursor is still moving as it reaches a
        // click, and while the click before still holds its button it creeps no more than CreepPx. See Travel.Glide.
        var into = items[0].Path.At(1f) - items[0].Path.At(0.97f);
        var outOf = items[1].Path.At(0.03f) - items[1].Path.At(0f);
        var turn = MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(into), Vector2.Normalize(outOf)), -1f, 1f)) * 180f / MathF.PI;
        var lastStep = Vector2.Distance(items[1].Path.At(1f), items[1].Path.At(0.98f));
        var held = TimeSpan.Zero;
        var creeping = new Travel(items[1].From, items[1].To, 67f, 6f, 7f, items[1].Drift, () => held, 80f);
        creeping.Glide(items[1].Control1, items[1].Control2, ClickPlan.CreepPx);
        var startsAt = creeping.Now();
        held = TimeSpan.FromMilliseconds(79);
        var crept = Vector2.Distance(startsAt, creeping.Now());
        var glideOk = turn < 35f && lastStep > 0.1f && crept > ClickPlan.CreepPx * 0.8f && crept <= ClickPlan.CreepPx + 0.05f;

        if (!glideOk)
            failed++;

        Console.WriteLine($"{(glideOk ? "  ok  " : "FAIL  ")}11 A planned queue glides through its clicks rather than stopping at each");
        Console.WriteLine($"        want  under 35 degrees between the way in and the way out; still moving at the click; at most {ClickPlan.CreepPx}px crept");
        Console.WriteLine($"        got   {turn:0.#} degrees, {lastStep:0.##}px in the last 2% of the way, {crept:0.##}px crept while the button was down");
        Console.WriteLine("        why   each journey ended at rest and the next began from rest, a corner and a stop at every click;");
        Console.WriteLine("              a hand slows through a click, and with ctrl held a little movement during it is not a drag");
        Console.WriteLine();

        // A planned journey after a planned one sets off from where the last put the cursor, even with the game's report
        // of it lagging far behind: no snap back to the stale report. See MousePointer.PlaceAlong.
        var now = TimeSpan.Zero;
        var sends = new List<Vector2>();
        var start = new Vector2(100f, 100f);

        // The game's report of the cursor, six sends behind it, as a report trailing by several frames is.
        Vector2 Reported() => sends.Count > 6 ? sends[^7] : start;

        var lagging = new MousePointer(null, new Random(7)) { Cursor = Reported, Send = p => sends.Add(p), Clock = () => now };
        var firstTo = new Vector2(200f, 150f);
        var secondTo = new Vector2(260f, 150f);

        lagging.PlaceAlong(firstTo, Modifier.None, 1f, 60, 60f, 6f, new Vector2(140f, 120f), new Vector2(180f, 145f));

        for (var k = 0; k < 40; k++)
        {
            now += TimeSpan.FromMilliseconds(8);
            lagging.Step();
        }

        var firstLanded = sends.Count > 0 && Vector2.Distance(sends[^1], firstTo) < 0.5f;
        var before = sends.Count;
        var stale = Reported();

        lagging.PlaceAlong(secondTo, Modifier.None, 2f, 60, 60f, 6f, new Vector2(220f, 150f), new Vector2(245f, 150f));
        now += TimeSpan.FromMilliseconds(8);
        lagging.Step();

        var nextSend = sends.Count > before ? sends[before] : stale;
        var noSnap = sends.Count > before && Vector2.Distance(nextSend, firstTo) < 10f;
        var lagOk = firstLanded && noSnap && lagging.Stopped == StopReason.None && Vector2.Distance(stale, firstTo) > 10f;

        if (!lagOk)
            failed++;

        Console.WriteLine($"{(lagOk ? "  ok  " : "FAIL  ")}12 A planned journey after a planned one sets off from the last put, not the lagging report");
        Console.WriteLine("        want  the first landed; the second's first step near where the first left the cursor");
        Console.WriteLine($"        got   first landed {firstLanded}; second's first step ({nextSend.X:0},{nextSend.Y:0}), " +
                          $"{Vector2.Distance(nextSend, firstTo):0.#}px from the last put, the report trailing at ({stale.X:0},{stale.Y:0})");
        Console.WriteLine("        why   a planned journey is arrived before the game reports it, so the report can trail the cursor;");
        Console.WriteLine("              setting off from it snapped the cursor back, a jump the player saw as a teleport");
        Console.WriteLine();

        // A stall mid-journey - the process stopped for garbage collection - moves the journey on by the step's most, not
        // by the whole stall.
        var mostStep = TimeSpan.FromMilliseconds(25);
        var stallClock = TimeSpan.Zero;
        var stalled = new Travel(new Vector2(0f, 0f), new Vector2(300f, 0f), 60f, 6f, 0f, 3f, () => stallClock);

        for (var k = 0; k < 4; k++)
        {
            stallClock += TimeSpan.FromMilliseconds(8);
            stalled.Advance(mostStep);
        }

        var beforeStall = stalled.Progress;
        stallClock += TimeSpan.FromMilliseconds(100);
        stalled.Advance(mostStep);
        var afterStall = stalled.Progress;
        var stallMs = (afterStall - beforeStall) * stalled.Ms;
        var stallOk = stallMs > 0f && stallMs <= (float)mostStep.TotalMilliseconds + 0.5f;

        if (!stallOk)
            failed++;

        Console.WriteLine($"{(stallOk ? "  ok  " : "FAIL  ")}13 A 100ms stall moves a journey on by {mostStep.TotalMilliseconds:0}ms at most, not 100");
        Console.WriteLine($"        want  the step over the stall worth {mostStep.TotalMilliseconds:0}ms of the journey or less");
        Console.WriteLine($"        got   {stallMs:0.#}ms of a {stalled.Ms:0}ms journey");
        Console.WriteLine("        why   by the clock alone a stall put the cursor most of a journey further on in one step: a teleport");
        Console.WriteLine();

        // The bow, journey to journey. A hand reaching between the same two places repeatedly takes
        // much the same path each time, and that path shifts over minutes - it does not curve left,
        // then hard right, then left again. Measured as how far the bow moves between consecutive
        // journeys, against what independent draws would give.
        var bowFrom = new Vector2(200f, 200f);
        var bowTo = new Vector2(900f, 500f);

        var drifted = new List<float>();
        var drawn = new List<float>();
        var bowRng = new Random(20260821);
        var bowDrift = 0f;

        for (var i = 0; i < 400; i++)
        {
            bowDrift += 0.19f + (float)bowRng.NextDouble() * 0.19f;

            // The furthest the path strays from the straight line, which is the bow made visible.
            drifted.Add(Stray(new Travel(bowFrom, bowTo, 60f, 8f, 15f, bowDrift)));
            drawn.Add((float)(bowRng.NextDouble() * 2 - 1));
        }

        var driftedStep = Steps(drifted);
        var drawnStep = Steps(drawn.Select(d => d * drifted.Max()).ToList());
        var driftedSpan = drifted.Max() - drifted.Min();

        // Smooth, and not stuck: neighbours close together, but the run as a whole covering several
        // times a single step's worth of ground. Comparing the span to the largest bow instead was
        // asking the wrong question - a wander that never reaches the extremes is the point of it,
        // not a failure of it.
        var bowOk = driftedStep < drawnStep / 3f && driftedSpan > driftedStep * 3f;

        if (!bowOk)
            failed++;

        Console.WriteLine($"{(bowOk ? "  ok  " : "FAIL  ")}9  The path bows like a habit, not a coin toss");
        Console.WriteLine("        given 400 journeys between the same two points");
        Console.WriteLine("        want  each path near the one before it, while still varying over the run");
        Console.WriteLine($"        got   {driftedStep:0.0}px of change between neighbours against " +
                          $"{drawnStep:0.0}px for independent draws, over a {driftedSpan:0.0}px range");
        Console.WriteLine("        why   somebody reaching for the same slot four hundred times has muscle");
        Console.WriteLine("              memory. Independent draws give a journey unrelated to the one before");
        Console.WriteLine("              it, which is the same tell the aim had before it drifted");
        Console.WriteLine();

        // A short hop between two long journeys stays near its own line: the curve's controls reach no further than a
        // third of the hop, whatever the journeys either side. See ClickPlan.Make.
        var reforging = new MousePointer(null, new Random(20261012)) { MinTravelMs = 60f, TravelMsPerRootPx = 6f, ArcPercent = 7f };
        var cyclePoints = new[] { new Vector2(887f, 540f), new Vector2(1722f, 813f), new Vector2(1785f, 807f), new Vector2(900f, 300f) };
        var cycle = ClickPlan.Make(reforging, new Vector2(880f, 520f), [], cyclePoints, [false, false, false, false],
            [ctrlOnly, ctrlOnly, ctrlOnly, Modifier.None], false, () => 64, () => 72, _ => 67f).Items;
        var hop = cycle[2];
        var hopLine = hop.To - hop.From;
        var hopLength = hopLine.Length();
        var widest = Enumerable.Range(0, 41).Max(k =>
        {
            var at = hop.Path.At(k / 40f) - hop.From;
            return MathF.Abs(at.X * hopLine.Y - at.Y * hopLine.X) / hopLength;
        });
        var hopOk = widest <= hopLength * 0.5f;

        if (!hopOk)
            failed++;

        Console.WriteLine($"{(hopOk ? "  ok  " : "FAIL  ")}14 A short hop between two long journeys keeps close to its own line");
        Console.WriteLine($"        want  at most {hopLength * 0.5f:0}px off the line of a {hopLength:0}px hop");
        Console.WriteLine($"        got   {widest:0.#}px");
        Console.WriteLine("        why   the curve through a queue took each control's reach from the journeys either side, so");
        Console.WriteLine("              a 63px hop after an 870px one swung out in an arc several times its own length");
        Console.WriteLine();

        // An overshoot runs past the target and back, and the journey is not arrived until it is back on it: whatever
        // waits on arrival, Arrived Within included, sees the cursor on its target. See Travel.Shape.
        var overNow = TimeSpan.Zero;
        var overSends = new List<Vector2>();
        var overStart = new Vector2(100f, 400f);
        var overTo = new Vector2(500f, 400f);
        var overshooting = new MousePointer(null, new Random(11))
        {
            Cursor = () => overSends.Count > 0 ? overSends[^1] : overStart, Send = q => overSends.Add(q), Clock = () => overNow,
        };
        var overshoot = new Travel.Flourish(new Vector2(16f, 0f), 90f, 1f);

        overshooting.PlaceAlong(overTo, Modifier.None, 3f, 60, 60f, 6f, new Vector2(230f, 400f), new Vector2(370f, 400f), overshoot);

        var arrivedEarly = false;
        var furthest = 0f;
        var arrivedAt = -1;

        for (var k = 0; k < 120 && arrivedAt < 0; k++)
        {
            overNow += TimeSpan.FromMilliseconds(8);
            overshooting.Step();

            if (overSends.Count > 0)
                furthest = Math.Max(furthest, overSends[^1].X - overTo.X);

            if (overshooting.Arrived())
            {
                arrivedAt = k;
                arrivedEarly = Vector2.Distance(overSends[^1], overTo) > 0.5f;
            }
        }

        var overOk = arrivedAt >= 0 && !arrivedEarly && furthest > 12f && furthest <= 16.5f &&
                     overshooting.Stopped == StopReason.None;

        if (!overOk)
            failed++;

        Console.WriteLine($"{(overOk ? "  ok  " : "FAIL  ")}15 An overshoot runs past and back, and arrives only once back on the target");
        Console.WriteLine("        want  up to 16px past the target; arrived only with the cursor on it; not taken for the player");
        Console.WriteLine($"        got   {furthest:0.#}px past, arrived after {arrivedAt + 1} steps, " +
                          $"{(arrivedEarly ? "off the target" : "on the target")}, stopped {overshooting.Stopped}");
        Console.WriteLine("        why   a click made at the far end of an overshoot would land off its point, and an arrival");
        Console.WriteLine("              counted there would let one through");
        Console.WriteLine();

        // A swoop's journey is given the time its longer way would have by the duration's rule. See ClickPlan.Make.
        var swoopPlain = new Travel(new Vector2(0f, 0f), new Vector2(300f, 0f), 60f, 6f, 0f, 5f);
        var swooping = new Travel(new Vector2(0f, 0f), new Vector2(300f, 0f), 60f, 6f, 0f, 5f);

        swooping.Shape(new Travel.Flourish(Vector2.Zero, 0f, 1.3f));

        var swoopOk = MathF.Abs(swooping.Ms - swoopPlain.Ms * 1.3f) < 0.01f;

        if (!swoopOk)
            failed++;

        Console.WriteLine($"{(swoopOk ? "  ok  " : "FAIL  ")}16 A swoop's journey takes longer by its stretch");
        Console.WriteLine($"        want  {swoopPlain.Ms * 1.3f:0}ms, 1.3 times {swoopPlain.Ms:0}ms");
        Console.WriteLine($"        got   {swooping.Ms:0}ms");
        Console.WriteLine("        why   the same time for a much longer way whips the cursor round it");
        Console.WriteLine();

        // About one planned journey in twenty swoops, and about one in twenty overshoots. See ClickPlan.Make.
        var rolling = new MousePointer(null, new Random(5)) { MinTravelMs = 60f, TravelMsPerRootPx = 6f, ArcPercent = 7f };
        var zigzag = Enumerable.Range(0, 2000).Select(k => new Vector2(k % 2 == 0 ? 200f : 500f, 300f + (k % 7) * 20f)).ToArray();
        var rolled = ClickPlan.Make(rolling, new Vector2(500f, 300f), [], zigzag, new bool[zigzag.Length],
            new System.Windows.Forms.Keys[zigzag.Length][], false, () => 64, () => 72, _ => 67f).Items;
        var swoops = rolled.Count(x => x.Shape.DurationScale > 1f) / (float)rolled.Count;
        var overshoots = rolled.Count(x => x.Shape.Overshoot != Vector2.Zero) / (float)rolled.Count;
        var rateOk = swoops is > 0.035f and < 0.065f && overshoots is > 0.035f and < 0.065f;

        if (!rateOk)
            failed++;

        Console.WriteLine($"{(rateOk ? "  ok  " : "FAIL  ")}17 About one planned journey in twenty swoops, and one in twenty overshoots");
        Console.WriteLine("        given 2000 journeys of about 300px");
        Console.WriteLine("        want  each between 3.5 and 6.5%");
        Console.WriteLine($"        got   {swoops:P1} swooped, {overshoots:P1} overshot");
        Console.WriteLine("        why   each is a flourish now and then, not a habit");
        Console.WriteLine();

        // Clicks added to a queue's end carry over its modifier and continue its curve. See ExileInput2.ExtendPlan.
        var extending = new MousePointer(null, new Random(3)) { MinTravelMs = 60f, TravelMsPerRootPx = 6f, ArcPercent = 7f };
        var firstPart = ClickPlan.Make(extending, new Vector2(100f, 100f), [], [new Vector2(200f, 100f), new Vector2(300f, 100f)],
            [false, false], [ctrlOnly, ctrlOnly], false, () => 64, () => 72, _ => 67f).Items;
        var addedPart = ClickPlan.Make(extending, firstPart[^1].To, [], [new Vector2(400f, 100f)], [false], [ctrlOnly], false,
            () => 64, () => 72, _ => 67f, firstPart[^1].From, firstPart[^1].Modifiers).Items;
        var joinIn = firstPart[^1].Path.At(1f) - firstPart[^1].Path.At(0.97f);
        var joinOut = addedPart[0].Path.At(0.03f) - addedPart[0].Path.At(0f);
        var joinTurn = MathF.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(joinIn), Vector2.Normalize(joinOut)), -1f, 1f)) * 180f / MathF.PI;
        var extendOk = addedPart[0].Carried && addedPart[0].LeadMs == 0 && addedPart[0].From == firstPart[^1].To && joinTurn < 35f;

        if (!extendOk)
            failed++;

        Console.WriteLine($"{(extendOk ? "  ok  " : "FAIL  ")}18 A click added to a queue's end carries its modifier and continues its curve");
        Console.WriteLine("        want  carried, no lead of its own, from the queue's last point, under 35 degrees at the join");
        Console.WriteLine($"        got   carried {addedPart[0].Carried}, lead {addedPart[0].LeadMs}, from the last point " +
                          $"{addedPart[0].From == firstPart[^1].To}, {joinTurn:0.#} degrees");
        Console.WriteLine("        why   a queue added to rather than planned again goes on as one, ctrl held through");
        Console.WriteLine();

        // With the journey moved elsewhere - the input thread - a step checks and does not move, and MoveAlong moves it
        // on with no step at all, as it does through a HUD stall. See MousePointer.MovedElsewhere.
        var elseNow = TimeSpan.Zero;
        var elseSends = new List<Vector2>();
        var elsewhere = new MousePointer(null, new Random(13))
        {
            Cursor = () => elseSends.Count > 0 ? elseSends[^1] : new Vector2(100f, 100f), Send = q => elseSends.Add(q),
            Clock = () => elseNow, MovedElsewhere = true,
        };

        elsewhere.PlaceAlong(new Vector2(400f, 100f), Modifier.None, 4f, 60, 60f, 6f, new Vector2(200f, 100f), new Vector2(300f, 100f));
        elseNow += TimeSpan.FromMilliseconds(40);
        elsewhere.Step();

        var sentByStep = elseSends.Count;

        for (var k = 0; k < 60; k++)
        {
            elseNow += TimeSpan.FromMilliseconds(5);
            elsewhere.MoveAlong();
        }

        var elseOk = sentByStep == 0 && elseSends.Count > 10 && Vector2.Distance(elseSends[^1], new Vector2(400f, 100f)) < 0.5f &&
                     elsewhere.Stopped == StopReason.None;

        if (!elseOk)
            failed++;

        Console.WriteLine($"{(elseOk ? "  ok  " : "FAIL  ")}19 A journey moved elsewhere is moved by MoveAlong alone, not by a step");
        Console.WriteLine("        want  no move from the step; the journey carried to its end by MoveAlong, with no step between");
        Console.WriteLine($"        got   {sentByStep} put(s) by the step, {elseSends.Count} by MoveAlong, ending at " +
                          $"({(elseSends.Count > 0 ? elseSends[^1].X : 0):0},{(elseSends.Count > 0 ? elseSends[^1].Y : 0):0})");
        Console.WriteLine("        why   the HUD calls Tick from inside its render loop, which waits on the overlay's Present while the");
        Console.WriteLine("              game is slow; a journey stepped only there froze through each stall and then jumped");
        Console.WriteLine();

        // PutOnTarget, which a click on arrival waits for, is false while the journey runs and true once it has put the
        // cursor on its point - with no report from the game at all. See MousePointer.PutOnTarget.
        var onNow = TimeSpan.Zero;
        var onTarget = new MousePointer(null, new Random(17))
        {
            Cursor = () => new Vector2(100f, 100f), Send = _ => { }, Clock = () => onNow, MovedElsewhere = true,
        };

        onTarget.Place(new Vector2(300f, 200f), Modifier.None);
        var beforeOn = onTarget.PutOnTarget;
        var midwayOn = false;

        for (var k = 0; k < 200 && !onTarget.PutOnTarget; k++)
        {
            onNow += TimeSpan.FromMilliseconds(2);
            onTarget.MoveAlong();

            if (k == 5)
                midwayOn = onTarget.PutOnTarget;
        }

        var onOk = !beforeOn && !midwayOn && onTarget.PutOnTarget;

        if (!onOk)
            failed++;

        Console.WriteLine($"{(onOk ? "  ok  " : "FAIL  ")}20 A pointer is on target only once its journey has put the cursor there");
        Console.WriteLine("        want  false before and part way, true at the end, with the game's report never moving");
        Console.WriteLine($"        got   before {beforeOn}, part way {midwayOn}, at the end {onTarget.PutOnTarget}");
        Console.WriteLine("        why   a click armed for arrival goes out from the input thread, which cannot read the game");
        Console.WriteLine();

        Console.WriteLine($"{All.Length + 2 - failed}/{All.Length + 2} match");
        return failed;
    }
}

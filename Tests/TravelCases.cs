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

        Console.WriteLine($"{All.Length + 2 - failed}/{All.Length + 2} match");
        return failed;
    }
}

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
            var worstOff = 0f;
            var drift = 0f;

            for (var j = 0; j < Journeys; j++)
            {
                // A journey's place on the noise, advanced unevenly as the pointer does it.
                drift += 0.19f + (float)rng.NextDouble() * 0.19f;

                var travel = new Travel(from, to, c.MinMs, c.MsPerRootPx, c.Arc, drift);
                ms = travel.Ms;

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

            var ok = startsExact && endsExact
                     && (c.WantMs == null || Math.Abs(ms - c.WantMs.Value) < 0.5f)
                     && (c.WantBowed == null || bowed == c.WantBowed.Value)
                     && (!c.WantEased || eased);

            if (!ok)
                failed++;

            var wants = new List<string> { "both ends exact" };

            if (c.WantMs != null)
                wants.Add($"{c.WantMs:0}ms");
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
            Console.WriteLine($"        got   {ms:0}ms, straying up to {worstOff:0.#}px off the " +
                              $"straight line over {Journeys} journeys, " +
                              $"{(startsExact && endsExact ? "both ends exact" : "AN END ADRIFT")}" +
                              $"{(eased ? "" : ", NOT EASED")}");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        // Not a case of its own because it is about the pair, not about a journey: the modifier
        // must go down after the approach has started and before it ends, or the gesture is
        // sequential again and the whole point of pressing it in flight is lost.
        var rngAt = new Random(20260818);
        var atDrift = 0f;

        var ats = Enumerable.Range(0, 1000).Select(_ =>
        {
            atDrift += 0.19f + (float)rngAt.NextDouble() * 0.19f;
            return Travel.ModifierAt(atDrift);
        }).ToArray();
        var atOk = ats.Min() > 0.5f && ats.Max() < 0.9f;

        if (!atOk)
            failed++;

        Console.WriteLine($"{(atOk ? "  ok  " : "FAIL  ")}8  The modifier goes down in the last third");
        Console.WriteLine("        want  after the approach has started and before it lands");
        Console.WriteLine($"        got   {ats.Min():0.###} to {ats.Max():0.###} of the way");
        Console.WriteLine("        why   reaching for control before deciding where to click is not what a hand");
        Console.WriteLine("              does, and pressing it on arrival is the tell this exists to remove - it");
        Console.WriteLine("              makes the gesture strictly one thing at a time. It is also the reliable");
        Console.WriteLine("              choice: the key needs confirming before the click, and confirming it");
        Console.WriteLine("              during the approach costs nothing where confirming it on arrival costs");
        Console.WriteLine("              up to 200ms of polling with the pointer sitting still on the target");
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

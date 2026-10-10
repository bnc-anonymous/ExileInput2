using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace ExileInput2.Tests;

/// <param name="Tolerance">What the machine asked for, before the rect gets a say.</param>
/// <param name="WantContained">
/// Whether every landing this would accept is inside the rect. False is a real answer here rather
/// than a failure - see case 5.
/// </param>
internal sealed record AimCase(
    string Name, float Width, float Height, float Spread, int Tolerance, bool WantContained,
    string Reason)
{
    /// <summary>What the tolerance must come out as, where the case pins it exactly.</summary>
    public float? WantAccepts { get; init; }

    /// <summary>An upper bound on it, where the spread makes the exact figure a range.</summary>
    public float? AcceptsAtMost { get; init; }

    /// <summary>
    /// How far outside the rect a landing may fall, where the floor has taken over.
    ///
    /// Always under the floor itself, and the point of stating it per case: the guarantee is not
    /// "inside the rect" but "inside the rect, or under three pixels out", and a case that says
    /// containment fails should have to say by how much.
    /// </summary>
    public float? OvershootAtMost { get; init; }
}

/// <summary>
/// How near the cursor must land, once the rect being aimed at has had its say.
///
/// A tolerance is a guess about how exactly the game reports its cursor back, and until recently
/// each machine set its own with nothing checking it against what it was clicking. Forty pixels is
/// a fair guess for a stash tab and a bad one for a currency exchange row, which is fifty-four
/// pixels tall - case 1 is that row, and it is the click that landed underneath the "I want" box.
///
/// The rule is that the rect may tighten the tolerance and never loosen it (case 2), so a machine
/// keeps its own judgement about how far behind the game reports its cursor while losing the
/// ability to accept a landing outside the thing it aimed at.
///
/// Cases 5 and 6 are where that stops being true, and it is deliberate. Once the aim point is
/// nearer the edge than the three pixel floor, the floor takes over and containment is no longer
/// guaranteed - the alternative was demanding a landing exact to a pixel, and if the game ever
/// rounds its readback that is a move which can never arrive, so the caller waits out its timeout
/// and stops. A stopped machine is a worse failure than a click half a pixel low.
///
/// So the guarantee is not "inside the rect" but "inside the rect, or under three pixels out", and
/// the first clause holds outright at any sensible spread. Stated this way because the first
/// version of case 6 asserted the stronger thing, on the reasoning that a rect of ordinary size
/// could never reach the floor - which confuses the size of the rect with the aim point's distance
/// from its edge, and a wide spread makes those two very different numbers.
/// </summary>
internal static class AimCases
{
    /// <summary>How far apart consecutive points are, on average.</summary>
    private static float Average(List<Vector2> points)
    {
        var total = 0f;

        for (var i = 1; i < points.Count; i++)
            total += Vector2.Distance(points[i - 1], points[i]);

        return points.Count > 1 ? total / (points.Count - 1) : 0f;
    }

    /// <summary>How much of the target the whole run covered, vertically.</summary>
    private static float Span(List<Vector2> points)
    {
        var low = float.MaxValue;
        var high = float.MinValue;

        foreach (var p in points)
        {
            low = Math.Min(low, p.Y);
            high = Math.Max(high, p.Y);
        }

        return high - low;
    }

    private const int Draws = 2000;

    internal static readonly AimCase[] All =
    [
        new("1  A currency exchange row, which is what missed", 300f, 54f, 50f, 40, true,
            "the machine asked for forty pixels on a row fifty-four tall. At the default spread " +
            "the aim point sits between 13.5 and 40.5 pixels from the top edge, so a landing " +
            "accepted forty pixels away is outside the row and the click hits whatever is under " +
            "it. The rect brings that down to at most half the row's height")
        {
            AcceptsAtMost = 27f,
        },

        new("2  A stash tab, where the machine's own figure still stands", 200f, 200f, 50f, 40, true,
            "the point of tightening rather than replacing. Forty pixels was set by somebody who " +
            "had thought about how far behind the game reports its cursor, and on a target this " +
            "size nothing about the rect argues with it")
        {
            WantAccepts = 40f,
        },

        new("3  Dead centre, where the bound is exactly half the shorter side",
            300f, 54f, 0f, 40, true,
            "no spread means no range, so this is the arithmetic with the randomness taken out: " +
            "twenty-seven pixels, half of fifty-four - and not a hundred and fifty, which is what " +
            "taking each axis separately would have allowed")
        {
            WantAccepts = 27f,
        },

        new("4  A wide, short row binds on its height and not its width",
            600f, 20f, 50f, 40, true,
            "the reason the two axes are collapsed with a minimum rather than checked apart. A " +
            "tolerance is a radius: a landing within it sideways can be well outside vertically, " +
            "so the tighter of the two has to govern both")
        {
            AcceptsAtMost = 10f,
        },

        new("5  Something a few pixels tall, where the floor gives up on containment",
            100f, 5f, 50f, 12, false,
            "and it says so. Half of five is 2.5, under the three pixel floor, so the floor wins " +
            "and a landing it accepts can be a fraction outside. Deliberate: a rect this thin " +
            "would otherwise demand a landing exact to a pixel, and a move that can never arrive " +
            "times the caller out and stops it, which is worse than a click half a pixel low")
        {
            WantAccepts = 3f,
            OvershootAtMost = 3f,
        },

        new("6  A wide spread no longer reaches the floor, now that aim wanders",
            300f, 54f, 95f, 40, true,
            "this case has been true, then false, and is now true again for a third reason - which " +
            "is why all three are worth keeping.\n\n" +
            "        It was written asserting a rect this size could not reach the three pixel " +
            "floor. That was wrong: what the floor answers to is the aim point's distance from the " +
            "edge, not the size of the rect, and uniform draws at ninety-five per cent came within " +
            "1.35 pixels of the edge of a 54 pixel row.\n\n" +
            "        Perlin drift does not. Uniform noise fills its range; Perlin only approaches " +
            "its peak, and reaches it about as often as a hand puts a click exactly on a border - " +
            "so ninety-five per cent now behaves like about seventy, and the nearest of two " +
            "thousand draws sits nine pixels inside a row it used to graze.\n\n" +
            "        Worth knowing rather than correcting. The setting is a ceiling that is " +
            "approached rather than a band that is filled, aim clusters toward the middle the way " +
            "a hand does, and the guarantee from case 1 - landings inside the rect - now holds at " +
            "every spread instead of only the modest ones")
        {
            OvershootAtMost = 3f,
        },

        new("7  A bare point keeps whatever the machine asked for", 0f, 0f, 0f, 12, true,
            "there is no rect to be inside of, so there is nothing to tighten against and the " +
            "machine's own figure is the whole of the answer. A width of nought is this table's " +
            "way of saying no rect was given")
        {
            WantAccepts = 12f,
        },
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- how near the cursor must land -----------------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            // Fixed, so a failure is the same failure tomorrow. The spread is the only randomness
            // here and the point is to sweep it, not to sample it differently every run.
            var rng = new Random(20260818);
            var rect = new RectangleF(100f, 100f, c.Width, c.Height);

            var accepts = new float[Draws];
            var overshoot = new float[Draws];
            var drift = 0f;

            for (var i = 0; i < Draws; i++)
            {
                // A width of nought is this table's way of saying the caller gave a bare point,
                // and a bare point is not inside anything, so no bound applies.
                if (c.Width <= 0f)
                {
                    accepts[i] = c.Tolerance;
                    overshoot[i] = 0f;
                    continue;
                }

                // The drift advances per draw, which is what a run of clicks does. Uneven steps,
                // because an even march along the noise traces the same path at the same pace.
                drift += 0.35f + (float)rng.NextDouble() * 0.35f;

                var target = MousePointer.PointIn(rect, c.Spread, drift);

                // What the edges actually allow, with no floor under it. The floor is the thing
                // being measured, so it cannot also be the thing doing the measuring.
                var trueEdge = Math.Min(
                    Math.Min(target.X - rect.Left, rect.Right - target.X),
                    Math.Min(target.Y - rect.Top, rect.Bottom - target.Y));

                accepts[i] = Math.Min(c.Tolerance, MousePointer.BoundFor(rect, target));
                overshoot[i] = Math.Max(0f, accepts[i] - trueEdge);
            }

            var most = accepts.Max();
            var least = accepts.Min();
            var worst = overshoot.Max();
            var contained = worst <= 0f;

            var ok = contained == c.WantContained
                     && (c.WantAccepts == null ||
                         (Math.Abs(most - c.WantAccepts.Value) < 0.01f &&
                          Math.Abs(least - c.WantAccepts.Value) < 0.01f))
                     && (c.AcceptsAtMost == null || most <= c.AcceptsAtMost.Value + 0.01f)
                     && (c.OvershootAtMost == null || worst <= c.OvershootAtMost.Value + 0.01f)
                     // The floor is the only thing that ever lets a landing out, so nothing may
                     // ever be further out than the floor itself - whatever the rect or the spread.
                     && worst < MousePointer.TightestPx + 0.01f;

            if (!ok)
                failed++;

            var wants = new List<string>
            {
                c.WantContained
                    ? "every landing inside the rect"
                    : "the floor to win, landings not guaranteed inside",
            };

            if (c.WantAccepts != null)
                wants.Add($"a tolerance of exactly {c.WantAccepts:0.##}px");

            if (c.AcceptsAtMost != null)
                wants.Add($"a tolerance never over {c.AcceptsAtMost:0.##}px");

            if (c.OvershootAtMost != null)
                wants.Add($"never out by more than {c.OvershootAtMost:0.##}px");

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        given {(c.Width <= 0f ? "a bare point" : $"a {c.Width:0}x{c.Height:0} rect")}, " +
                              $"spread {c.Spread:0}%, the machine asking for {c.Tolerance}px");
            Console.WriteLine($"        want  {string.Join("; ", wants)}");
            Console.WriteLine($"        got   {least:0.##}-{most:0.##}px over {Draws:n0} draws, " +
                              (contained ? "always inside" : $"outside by up to {worst:0.##}px"));
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        // What the noise is actually for. Uniform draws are unrelated, so consecutive clicks at one
        // slot scatter across it; drift means each lands near the last and the cluster migrates.
        // Both cover the target over a long run - only one looks like a hand.
        //
        // Asked of a real pointer rather than of the arithmetic copied out, because the step it
        // walks along the noise is the whole of what is being measured, and a copy of it here would
        // go on passing after the real one changed.
        var row = new RectangleF(100f, 100f, 300f, 54f);
        var walk = new List<Vector2>();
        var jump = new List<Vector2>();
        var scatter = new Random(20260821);

        var hand = new MousePointer(null, new Random(20260821)) { SpreadPercent = 50f };

        for (var i = 0; i < 400; i++)
        {
            walk.Add(hand.PointIn(row));

            jump.Add(new Vector2(
                row.X + row.Width * (0.5f + (float)(scatter.NextDouble() - 0.5) * 0.5f),
                row.Y + row.Height * (0.5f + (float)(scatter.NextDouble() - 0.5) * 0.5f)));
        }

        // The spot the clicks scatter about: a moving average over a few aims takes the scatter out.
        var spot = MovingAverage(walk, 20);
        var spotStep = Average(spot);
        var walkSpan = Span(walk);
        var jumpStep = Average(jump);

        // The spot moves far less between aims than a random draw does, and still covers the row.
        var driftOk = spotStep < jumpStep / 5f && walkSpan > row.Height / 4f;

        if (!driftOk)
            failed++;

        Console.WriteLine($"{(driftOk ? "  ok  " : "FAIL  ")}8  The spot clicks scatter about wanders rather than jumping");
        Console.WriteLine("        given 400 successive aims at one 300x54 row, spread 50%, the spot their average over 20");
        Console.WriteLine("        want  the average moving far less between aims than a random draw, while still");
        Console.WriteLine("              covering the row");
        Console.WriteLine($"        got   {spotStep:0.0}px between neighbours against {jumpStep:0.0}px " +
                          $"for random draws, the clicks over a {walkSpan:0.0}px span");
        Console.WriteLine("        why   a person clicking one slot four hundred times rests somewhere, drifts,");
        Console.WriteLine("              and settles somewhere else. Uniform draws make a cloud no hand has");
        Console.WriteLine("              ever made - and make the same one every session, which is a signature");
        Console.WriteLine("              of its own");
        Console.WriteLine();

        var scattered = ScatterCases();
        failed += scattered.Failed;

        Console.WriteLine($"{All.Length + 1 + scattered.Count - failed}/{All.Length + 1 + scattered.Count} match");
        return failed;
    }

    /// <summary>Each point averaged with the ones before it, up to this many.</summary>
    private static List<Vector2> MovingAverage(List<Vector2> points, int over)
    {
        var averaged = new List<Vector2>();

        for (var i = 0; i < points.Count; i++)
        {
            var from = Math.Max(0, i - over + 1);
            var sum = Vector2.Zero;

            for (var j = from; j <= i; j++)
                sum += points[j];

            averaged.Add(sum / (i - from + 1));
        }

        return averaged;
    }

    /// <summary>How far, on average, points stray from their moving average on one axis: the scatter, the wander taken out.</summary>
    private static float ScatterOn(List<Vector2> points, bool x)
    {
        var spot = MovingAverage(points, 20);
        var total = 0f;

        for (var i = 0; i < points.Count; i++)
            total += Math.Abs(x ? points[i].X - spot[i].X : points[i].Y - spot[i].Y);

        return total / points.Count;
    }

    /// <summary>
    /// Where clicks land about the spot: scattered from one to the next, more along the approach than across it, a
    /// little short of the centre, each target with a spot of its own, and never outside the spread.
    /// </summary>
    private static (int Failed, int Count) ScatterCases()
    {
        var failed = 0;
        var count = 0;

        void Report(bool ok, string name, string given, string want, string got, string why)
        {
            count++;

            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{name}");
            Console.WriteLine($"        given {given}");
            Console.WriteLine($"        want  {want}");
            Console.WriteLine($"        got   {got}");
            Console.WriteLine($"        why   {why}");
            Console.WriteLine();
        }

        List<Vector2> Aims(RectangleF rect, Vector2 from, int seed, int n = 400)
        {
            var hand = new MousePointer(null, new Random(seed)) { SpreadPercent = 50f };
            var points = new List<Vector2>();

            for (var i = 0; i < n; i++)
                points.Add(hand.PointIn(rect, from));

            return points;
        }

        // A button the size of the reforging bench's, approached from above as from the tablets.
        var button = new RectangleF(790f, 1139f, 93f, 29f);
        var above = new Vector2(830f, 700f);
        var presses = Aims(button, above, 20261010);
        var uniform = new Random(20261010);
        var draws = new List<Vector2>();

        for (var i = 0; i < 400; i++)
            draws.Add(new Vector2(button.X + button.Width * (0.5f + (float)(uniform.NextDouble() - 0.5) * 0.5f),
                button.Y + button.Height * (0.5f + (float)(uniform.NextDouble() - 0.5) * 0.5f)));

        var pressStep = Average(presses);
        var drawStep = Average(draws);

        Report(pressStep > drawStep / 4f && pressStep < drawStep,
            "9  Each click lands apart from the last, but nearer than a random draw",
            "400 presses of a 93x29 button from above, spread 50%",
            "between a quarter of and all of a random draw's distance between neighbours",
            $"{pressStep:0.0}px between neighbours against {drawStep:0.0}px for random draws",
            "a button pressed once a reforge was struck in nearly the same spot every time: the aim drifted a " +
            "couple of pixels a click and nothing else moved it (2026-10-10)");

        // A square approached from the left and from above: the cloud stretched along each approach.
        var square = new RectangleF(400f, 400f, 120f, 120f);
        var fromLeft = Aims(square, new Vector2(0f, 460f), 7);
        var fromAbove = Aims(square, new Vector2(460f, 0f), 7);
        var leftRatio = ScatterOn(fromLeft, true) / Math.Max(0.01f, ScatterOn(fromLeft, false));
        var aboveRatio = ScatterOn(fromAbove, false) / Math.Max(0.01f, ScatterOn(fromAbove, true));

        Report(leftRatio > 1.25f && aboveRatio > 1.25f,
            "10 The scatter is longer along the approach than across it",
            "400 aims at a 120x120 square from the left, and 400 from above",
            "the scatter along each approach at least 1.25 times the scatter across it",
            $"{leftRatio:0.00} from the left, {aboveRatio:0.00} from above",
            "a reach misses long or short far more than it misses to the side");

        // The same square from the left and from the right, the same habit: the landing sits nearer each side.
        var fromRight = Aims(square, new Vector2(1000f, 460f), 7);
        var leftMean = fromLeft.Average(p => p.X);
        var rightMean = fromRight.Average(p => p.X);

        Report(rightMean - leftMean > 2f,
            "11 A little short of the centre, on the side the cursor came from",
            "400 aims at a 120x120 square from the left, and 400 from the right, the same habit",
            "the landings from the left averaging further left than those from the right",
            $"{leftMean:0.0}px from the left against {rightMean:0.0}px from the right",
            "a hand slows into a target and tends to stop before its middle");

        // Two buttons alike but apart, pressed in turn: each has a spot of its own.
        var hand2 = new MousePointer(null, new Random(99)) { SpreadPercent = 50f };
        var first = new RectangleF(100f, 100f, 93f, 29f);
        var second = new RectangleF(600f, 100f, 93f, 29f);
        var firstOff = new List<Vector2>();
        var secondOff = new List<Vector2>();

        for (var i = 0; i < 200; i++)
        {
            firstOff.Add(hand2.PointIn(first, new Vector2(350f, 600f)) - first.Center);
            secondOff.Add(hand2.PointIn(second, new Vector2(350f, 600f)) - second.Center);
        }

        var firstSpot = new Vector2(firstOff.Average(p => p.X), firstOff.Average(p => p.Y));
        var secondSpot = new Vector2(secondOff.Average(p => p.X), secondOff.Average(p => p.Y));
        var apart = Vector2.Distance(firstSpot, secondSpot);

        Report(apart > 2f,
            "12 Each target has a spot of its own",
            "two 93x29 buttons apart, pressed in turn 200 times each from between them",
            "their average landings, each from its own centre, more than 2px apart",
            $"{apart:0.0}px apart",
            "one shared drift put the same habit on every target");

        // Every aim inside the spread, the guarantee cases 1 to 7 rest on.
        var all = presses.Concat(fromLeft).Concat(fromAbove).Concat(fromRight).ToList();
        var rects = Enumerable.Repeat(button, presses.Count).Concat(Enumerable.Repeat(square, fromLeft.Count * 3)).ToList();
        var worst = 0f;

        for (var i = 0; i < all.Count; i++)
        {
            var r = rects[i];
            var u = Math.Abs(all[i].X - r.Center.X) / (r.Width / 2f);
            var v = Math.Abs(all[i].Y - r.Center.Y) / (r.Height / 2f);

            worst = Math.Max(worst, Math.Max(u, v));
        }

        // On the edge of the spread: a scatter clamped there piles up along it in a line no hand draws.
        var onEdge = 0;

        for (var i = 0; i < all.Count; i++)
        {
            var r = rects[i];
            var u = Math.Abs(all[i].X - r.Center.X) / (r.Width / 2f);
            var v = Math.Abs(all[i].Y - r.Center.Y) / (r.Height / 2f);

            if (u >= 0.5f - 0.0005f || v >= 0.5f - 0.0005f)
                onEdge++;
        }

        var edgeShare = (float)onEdge / all.Count;

        Report(edgeShare <= 0.01f,
            "14 Clicks do not pile up on the edge of the spread",
            "every aim of cases 9 to 11, spread 50%",
            "1% of aims or fewer exactly on the spread's edge",
            $"{edgeShare * 100f:0.0}% on the edge, {onEdge} of {all.Count}",
            "a scatter that would land outside the spread was pushed onto its edge, drawing a line of clicks " +
            "along it (picture of 2026-10-10); one outside is drawn again instead");

        Report(worst <= 0.5f + 0.001f,
            "13 No aim leaves the spread",
            "every aim of cases 9 to 11, spread 50%",
            "each within half the rect's half-size of its centre on both axes",
            $"furthest {worst:0.000} of the half-size",
            "the arrival tolerance is bounded by the aim's distance from the edge; an aim outside the spread is " +
            "one a landing can slip past the rect from");

        return (failed, count);
    }

}

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

        var walkStep = Average(walk);
        var jumpStep = Average(jump);
        var walkSpan = Span(walk);

        // Near its neighbour by an order of magnitude, and still covering the row rather than
        // sitting in one spot.
        var driftOk = walkStep < jumpStep / 5f && walkSpan > row.Height / 4f;

        if (!driftOk)
            failed++;

        Console.WriteLine($"{(driftOk ? "  ok  " : "FAIL  ")}8  Aim wanders rather than jumping");
        Console.WriteLine("        given 400 successive aims at one 300x54 row, spread 50%");
        Console.WriteLine("        want  each landing far nearer its predecessor than a random draw would");
        Console.WriteLine("              be, while still covering the row");
        Console.WriteLine($"        got   {walkStep:0.0}px between neighbours against {jumpStep:0.0}px " +
                          $"for random draws, over a {walkSpan:0.0}px span");
        Console.WriteLine("        why   a person clicking one slot four hundred times rests somewhere, drifts,");
        Console.WriteLine("              and settles somewhere else. Uniform draws make a cloud no hand has");
        Console.WriteLine("              ever made - and make the same one every session, which is a signature");
        Console.WriteLine("              of its own");
        Console.WriteLine();

        Console.WriteLine($"{All.Length + 1 - failed}/{All.Length + 1} match");
        return failed;
    }
}

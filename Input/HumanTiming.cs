using System;

namespace ExileInput2;

/// <summary>
/// How long the parts of a click take: how long the button is held down, how long before the click a modifier goes down,
/// and how long after the button comes up the modifier follows it. Each is drawn afresh, between a shortest and a
/// longest and most often near a usual - a triangular spread, so the bulk sits around the usual and neither end is as
/// likely as the middle. The three come from the Click Hold and Modifier Timing settings; Least, Usual and Most are
/// their defaults.
///
/// The defaults were chosen by the player (2026-10-11) from their own deposit clicks recorded with a hook: 38 ctrl+left
/// clicks held 62 to 110 ms, average 89, and ctrl down 47 to 141 ms before the first click of a press. The hook's
/// timestamps step in about 15.6 ms, so those readings are good to that, not to the millisecond.
/// </summary>
internal static class HumanTiming
{
    public const int Least = 50;
    public const int Usual = 80;
    public const int Most = 100;

    private static readonly Random Shared = new();

    /// <summary>One draw, in milliseconds, between these three, from the shared random source.</summary>
    public static int Ms((int Least, int Usual, int Most) spread) => Ms(Shared, spread);

    /// <summary>One draw, in milliseconds, at the defaults, from this random source, so a test can fix it.</summary>
    public static int Ms(Random random) => Ms(random, (Least, Usual, Most));

    /// <summary>One draw, in milliseconds, between these three, from this random source.</summary>
    public static int Ms(Random random, (int Least, int Usual, int Most) spread)
    {
        var (least, usual, most) = spread;

        if (most <= least)
            return least;

        usual = Math.Clamp(usual, least, most);

        // The inverse of the triangular distribution's cumulative share: below the usual's share of the width, the
        // rising side; above it, the falling side.
        var u = random.NextDouble();
        var width = (double)(most - least);
        var rising = (usual - least) / width;

        var ms = u < rising
            ? least + Math.Sqrt(u * width * (usual - least))
            : most - Math.Sqrt((1d - u) * width * (most - usual));

        return (int)Math.Round(ms);
    }
}

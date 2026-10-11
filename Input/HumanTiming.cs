using System;

namespace ExileInput2;

/// <summary>
/// How long the parts of a click take: how long the button is held down, how long before the click a modifier goes down,
/// and how long after the button comes up the modifier follows it. Each is drawn afresh, between Least and Most and
/// most often near Usual - a triangular spread, so the bulk sits around Usual and neither end is as likely as the middle.
///
/// Chosen by the player (2026-10-11) from their own deposit clicks recorded with a hook: 38 ctrl+left clicks held 62 to
/// 110 ms, average 89, and ctrl down 47 to 141 ms before the first click of a press. The hook's timestamps step in about
/// 15.6 ms, so those readings are good to that, not to the millisecond.
/// </summary>
internal static class HumanTiming
{
    public const int Least = 50;
    public const int Usual = 80;
    public const int Most = 100;

    private static readonly Random Shared = new();

    /// <summary>One draw, in milliseconds, from the shared random source.</summary>
    public static int Ms() => Ms(Shared);

    /// <summary>One draw, in milliseconds, from this random source, so a test can fix it.</summary>
    public static int Ms(Random random)
    {
        // The inverse of the triangular distribution's cumulative share: below Usual's share of the width, the rising
        // side; above it, the falling side.
        var u = random.NextDouble();
        var width = (double)(Most - Least);
        var rising = (Usual - Least) / width;

        var ms = u < rising
            ? Least + Math.Sqrt(u * width * (Usual - Least))
            : Most - Math.Sqrt((1d - u) * width * (Most - Usual));

        return (int)Math.Round(ms);
    }
}

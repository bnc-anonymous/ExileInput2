using System;
using System.Linq;

namespace ExileInput2.Tests;

/// <summary>
/// The install's seed and the noise it shapes: the same seed must always give the same noise, so an install keeps one
/// habit across sessions; two seeds must give different noise, or the seed does nothing; and nought must give Perlin's
/// own table, which every other table here is written against.
/// </summary>
internal static class SeedCases
{
    internal static int Run()
    {
        Console.WriteLine("--- the install's seed ---------------------------------------------");
        Console.WriteLine();

        float[] Sample(int seed)
        {
            PerlinNoise.UseSeed(seed);
            return Enumerable.Range(0, 64).Select(i => PerlinNoise.Full(i * 0.37f, 3.1f)).ToArray();
        }

        var original = Sample(0);
        var first = Sample(1234567);
        var again = Sample(1234567);
        var other = Sample(7654321);
        var back = Sample(0);

        PerlinNoise.UseSeed(0);

        var failed = 0;

        void Check(string name, bool ok, string why)
        {
            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{name}");
            Console.WriteLine($"        why   {why}");
            Console.WriteLine();
        }

        Check("1  The same seed gives the same noise", first.SequenceEqual(again),
            "an install keeps one habit across sessions; a seed that drew afresh each time would be no seed");
        Check("2  Two seeds give different noise", !first.SequenceEqual(other),
            "or every install still shares one habit and the seed is decoration");
        Check("3  A seed differs from Perlin's own table", !first.SequenceEqual(original),
            "the shuffle has to have happened");
        Check("4  Nought gives Perlin's own table back", original.SequenceEqual(back),
            "the tables elsewhere here are written against it, and a seed left behind by one case must not move another");

        var seeds = new[] { 1, 42, 1234567, 7654321, int.MaxValue - 1 };
        var factors = seeds.SelectMany(seed => Enumerable.Range(0, 5).Select(which => ExileInput2Settings.SeedFactor(seed, which))).ToList();

        Check("5  Seed nought leaves every setting as its slider",
            Enumerable.Range(0, 5).All(which => ExileInput2Settings.SeedFactor(0, which) == 1f),
            "nought means no seed: the sliders as they are, which is what Reset to defaults then gives exactly");
        Check("6  Any other seed moves a setting 10 to 20 per cent, up or down",
            factors.All(f => f is >= 0.8f and <= 0.9f or >= 1.1f and <= 1.2f),
            "the variation asked for: visible on the menu, and never so far that the feel stops being the slider's");
        Check("7  The same seed moves a setting the same way every time",
            seeds.All(seed => Enumerable.Range(0, 5).All(which => ExileInput2Settings.SeedFactor(seed, which) == ExileInput2Settings.SeedFactor(seed, which))),
            "an install's habit is the same session to session");
        Check("8  Seeds move settings both ways",
            factors.Any(f => f < 1f) && factors.Any(f => f > 1f),
            "a variation that only ever went one way would be a different default, not a habit of one's own");

        return failed;
    }
}

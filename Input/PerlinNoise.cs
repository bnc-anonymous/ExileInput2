using System;

namespace ExileInput2;

/// <summary>
/// Ken Perlin's noise, for aim that wanders rather than jumps.
///
/// The whole reason it is here rather than a call to Random: successive values are RELATED. Ask for
/// noise at 1.0 and at 1.1 and you get two numbers close together; ask Random twice and you get two
/// numbers with nothing to do with each other.
///
/// Which is the difference between a hand and a machine. A person clicking the same slot four
/// hundred times does not hit a uniformly distributed scatter of pixels across it - they rest
/// somewhere, drift, settle somewhere else. Uniform noise produces a cloud no hand has ever made,
/// and produces it every single time, which is a signature of its own.
///
/// The reference implementation, permutation table included. It is the standard one, doubled so the
/// lookups need no wrapping.
/// </summary>
internal static class PerlinNoise
{
    /// <summary>
    /// The practical peak of two-dimensional Perlin noise, which is not one.
    ///
    /// The gradients are unit vectors on the diagonals, so the most a sample can reach is the
    /// length of a half-diagonal - about 0.707. Anything treating the output as spanning -1 to 1
    /// quietly gets seventy per cent of the range it asked for, so callers scale by this and clamp.
    /// </summary>
    public const float Peak = 0.7071f;

    /// <summary>A value between roughly -0.707 and 0.707, smooth in both directions.</summary>
    public static float At(float x, float y)
    {
        var xi = (int)Math.Floor(x) & 255;
        var yi = (int)Math.Floor(y) & 255;

        x -= (float)Math.Floor(x);
        y -= (float)Math.Floor(y);

        var u = Fade(x);
        var v = Fade(y);

        var a = Permutation[xi] + yi;
        var b = Permutation[xi + 1] + yi;

        return Lerp(v,
            Lerp(u, Gradient(Permutation[a], x, y), Gradient(Permutation[b], x - 1, y)),
            Lerp(u, Gradient(Permutation[a + 1], x, y - 1), Gradient(Permutation[b + 1], x - 1, y - 1)));
    }

    /// <summary>A value between -1 and 1, with the range the caller expected.</summary>
    public static float Full(float x, float y) => Math.Clamp(At(x, y) / Peak, -1f, 1f);

    /// <summary>6t^5 - 15t^4 + 10t^3, which eases the interpolation at both ends.</summary>
    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static float Lerp(float t, float a, float b) => a + t * (b - a);

    private static float Gradient(int hash, float x, float y)
    {
        // The low bits pick one of four diagonal directions, and the sign bits pick its quadrant.
        return ((hash & 1) == 0 ? x : -x) + ((hash & 2) == 0 ? y : -y);
    }

    private static readonly int[] Permutation = Doubled(
    [
        151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30,
        69, 142, 8, 99, 37, 240, 21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94,
        252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171,
        168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60,
        211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1,
        216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86,
        164, 100, 109, 198, 173, 186, 3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118,
        126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170,
        213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9, 129, 22, 39,
        253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228, 251, 34,
        242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49,
        192, 214, 31, 181, 199, 106, 157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254,
        138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180,
    ]);

    /// <summary>
    /// The table laid end to end with itself.
    ///
    /// So that an index near the top can add one and look up a neighbour without wrapping. Doing it
    /// here rather than writing the table out twice means the two halves cannot drift apart.
    /// </summary>
    private static int[] Doubled(int[] table)
    {
        var doubled = new int[table.Length * 2];

        for (var i = 0; i < doubled.Length; i++)
            doubled[i] = table[i % table.Length];

        return doubled;
    }
}

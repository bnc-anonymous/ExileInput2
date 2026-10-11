using ExileCore2;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Graphics = ExileCore2.Graphics;
using RectangleF = ExileCore2.Shared.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2;

/// <summary>
/// A queue of clicks a caller has asked for ahead of time, each journey and timing drawn when the queue is made, so they
/// can be drawn on screen and then made exactly as drawn. The caller still asks for each move and click in turn, as
/// before; a move to the queue's next point uses that click's journey, and a move anywhere else drops the queue. See
/// ExileInput2.PlanClicks.
///
/// Drawn while Show is set: each journey's path, the current one bright; the click where it lands as an L or an R,
/// bright where the button goes down and dim beside it where it comes up; and a modifier - C, S or A - bright on the
/// path where it goes down and dim where it comes up. A modifier the move before still holds is handed over, not
/// pressed again, so a batch of ctrl+clicks shows one C going down and one coming up. See Stroke.HandOver.
/// </summary>
internal sealed class ClickPlan
{
    /// <summary>
    /// One planned click: the journey to it - its drift, and the shortest move and time per root pixel it is timed with,
    /// kept so the move is made as planned whatever is set by then - and its timings.
    /// </summary>
    internal sealed record Planned(RectangleF Rect, bool PointGiven, Vector2 From, Vector2 To, float Drift, float MinMs,
        float MsPerRootPx, Vector2 Control1, Vector2 Control2, Travel Path, int LeadMs, bool Carried, int HoldMs, int ReleaseMs,
        bool Right, Keys[] Modifiers);

    /// <summary>
    /// How far a planned journey creeps while the click before it still holds its button. With ctrl held the game took a
    /// click moved across a whole orb as a click, not a drag (the player, 2026-10-11), so a few pixels is safe. Chosen.
    /// See Travel.Glide.
    /// </summary>
    public const float CreepPx = 6f;

    /// <summary>
    /// How far a curve's control points stray from the smooth curve through the queue, as a share of the way, on the
    /// noise: a little, so the curve is a hand's rather than a draughtsman's. Chosen.
    /// </summary>
    private const float CurveWander = 0.08f;

    public readonly List<Planned> Items = [];

    /// <summary>The next click to be moved to.</summary>
    public int Next;

    /// <summary>The click moved to and not yet clicked, whose timings the click takes; null when none.</summary>
    public Planned Current;

    public bool Show;

    /// <summary>
    /// The queue for these clicks, from the cursor. Each lands on its point where one is given, or else where the pointer
    /// would aim in its rectangle from the click before - staying put when already in it. Each journey's drift is taken
    /// from the pointer as it would have been, its shortest move from minMsOf - each click's own, so a caller can time its
    /// first move unlike the rest - and its modifier lead, button hold and modifier release drawn now. A click whose
    /// modifiers match the one before it carries them over, with no lead of its own.
    /// </summary>
    public static ClickPlan Make(MousePointer pointer, Vector2 cursor, IReadOnlyList<RectangleF> rects, IReadOnlyList<Vector2> points,
        IReadOnlyList<bool> rights, IReadOnlyList<Keys[]> modifiers, bool show, Func<int> holdMs, Func<int> modifierMs,
        Func<int, float> minMsOf)
    {
        var plan = new ClickPlan { Show = show };
        var from = cursor;
        var count = Math.Max(rects.Count, points.Count);
        Keys[] before = Modifier.None;
        var landings = new List<Vector2>();

        // Where each click lands first, so each journey's curve can head for the one after it.
        for (var i = 0; i < count; i++)
        {
            var rect = i < rects.Count ? rects[i] : default;
            var given = i < points.Count && !float.IsNaN(points[i].X) && !float.IsNaN(points[i].Y);

            from = given ? points[i]
                : pointer.StayPut && MousePointer.Holds(rect, from) ? from
                : pointer.PointIn(rect, from);
            landings.Add(from);
        }

        from = cursor;

        for (var i = 0; i < count; i++)
        {
            var rect = i < rects.Count ? rects[i] : default;
            var given = i < points.Count && !float.IsNaN(points[i].X) && !float.IsNaN(points[i].Y);
            var to = landings[i];
            var mods = i < modifiers.Count && modifiers[i] != null ? modifiers[i] : Modifier.None;
            var drift = pointer.NextJourney();

            // **One smooth curve through the queue**, a Catmull-Rom spline as cubic beziers: the way into each click heads
            // for the next and the way out leaves in the same direction, so it slows through a click rather than turning a
            // corner at it. A little off it, on the noise. See Travel.Glide.
            var before2 = i > 0 ? (i > 1 ? landings[i - 2] : cursor) : from;
            var after = i < count - 1 ? landings[i + 1] : to;
            var line = to - from;
            var across = line.Length() > 1f ? new Vector2(-line.Y, line.X) * CurveWander * PerlinNoise.Full(drift, 0f) : Vector2.Zero;
            var control1 = from + (to - before2) / 6f + across;
            var control2 = to - (after - from) / 6f + across;
            var minMs = minMsOf(i);
            var perRootPx = pointer.TravelMsPerRootPx;
            var path = perRootPx <= 0
                ? new Travel(from, to, 1f, 0f, 0f, drift)
                : new Travel(from, to, minMs, perRootPx, pointer.ArcPercent, drift);

            if (perRootPx > 0)
                path.Glide(control1, control2, CreepPx);
            var carried = i > 0 && Modifier.Any(mods) && SameKeys(mods, before);

            plan.Items.Add(new Planned(rect, given, from, to, drift, minMs, perRootPx, control1, control2, path,
                carried || !Modifier.Any(mods) ? 0 : modifierMs(), carried, holdMs(), modifierMs(), i < rights.Count && rights[i], mods));
            from = to;
            before = mods;
        }

        return plan;
    }

    private static bool SameKeys(IReadOnlyList<Keys> a, IReadOnlyList<Keys> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (Array.IndexOf((Keys[])b, a[i]) < 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The paths, clicks and modifiers from the click in hand on, at this opacity: 1 while the queue is followed, falling
    /// to 0 over a second once it is given up. See the class and ExileInput2.Render.
    /// </summary>
    public void Draw(Graphics graphics, float opacity = 1f)
    {
        Color Faded(Color colour) => Color.FromArgb((int)(colour.A * Math.Clamp(opacity, 0f, 1f)), colour.R, colour.G, colour.B);

        var bright = Faded(Bright);
        var dim = Faded(Dim);

        var first = Current != null ? Next - 1 : Next;

        for (var i = Math.Max(0, first); i < Items.Count; i++)
        {
            var item = Items[i];
            var current = i == first;
            var line = Faded(current ? Color.FromArgb(230, Bright) : Color.FromArgb(110, 255, 255, 255));
            var points = new Vector2[PathSamples];

            for (var k = 0; k < PathSamples; k++)
                points[k] = item.Path.At(k / (PathSamples - 1f));

            graphics.DrawPolyLine(points, line, current ? 2f : 1.2f);

            var modifier = ModifierLetters(item.Modifiers);

            // The modifier going down, on the path where it does.
            if (item.LeadMs > 0 && modifier.Length > 0)
            {
                var at = item.Path.At(item.Path.ProgressAt(Math.Max(0f, item.Path.Ms - item.LeadMs)));
                Letter(graphics, modifier, at + new Vector2(0f, -12f), bright);
            }

            // The click: down bright on the point, up dim beside it, a letter's width and a little apart; its number
            // centred above it.
            var size = graphics.MeasureText("M");
            var step = new Vector2(size.X + 3f, 0f);
            var button = item.Right ? "R" : "L";

            Letter(graphics, button, item.To, bright);
            Letter(graphics, button, item.To + step, dim);
            graphics.DrawText($"{i + 1}", item.To - new Vector2(0f, size.Y + 2f), dim,
                ExileCore2.Shared.Enums.FontAlign.Center | ExileCore2.Shared.Enums.FontAlign.VerticalCenter);

            // The modifier coming up, after the last click that holds it.
            var last = i == Items.Count - 1 || !Items[i + 1].Carried;

            if (last && modifier.Length > 0)
                Letter(graphics, modifier, item.To + step * 2f, dim);
        }
    }

    private const int PathSamples = 20;

    private static void Letter(Graphics graphics, string text, Vector2 at, Color colour)
    {
        graphics.DrawText(text, at, colour,
            ExileCore2.Shared.Enums.FontAlign.Center | ExileCore2.Shared.Enums.FontAlign.VerticalCenter);
    }

    /// <summary>
    /// A button or modifier going down, and the current path: AutoExpedition's green, its Next explosive colour's
    /// default, so the plan reads as that plugin's other markings do.
    /// </summary>
    private static readonly Color Bright = Color.FromArgb(255, 90, 255, 120);

    /// <summary>A button or modifier coming up, and the clicks' numbers: light enough to read over the game.</summary>
    private static readonly Color Dim = Color.FromArgb(215, 225, 225, 225);

    /// <summary>C for ctrl, S for shift, A for alt; "" for none.</summary>
    private static string ModifierLetters(IReadOnlyList<Keys> modifiers)
    {
        var letters = "";

        foreach (var key in modifiers)
        {
            var letter = (key & Keys.KeyCode) switch
            {
                Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "C",
                Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "S",
                Keys.Menu or Keys.LMenu or Keys.RMenu => "A",
                _ => "",
            };

            if (!letters.Contains(letter))
                letters += letter;
        }

        return letters;
    }
}

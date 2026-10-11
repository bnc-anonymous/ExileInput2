using ExileCore2;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Graphics = ExileCore2.Graphics;
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
    /// <summary>One planned click: the journey to it, and its timings.</summary>
    internal sealed record Planned(Vector2 From, Vector2 To, float Drift, Travel Path, int LeadMs, bool Carried, int HoldMs,
        int ReleaseMs, bool Right, Keys[] Modifiers);

    public readonly List<Planned> Items = [];

    /// <summary>The next click to be moved to.</summary>
    public int Next;

    /// <summary>The click moved to and not yet clicked, whose timings the click takes; null when none.</summary>
    public Planned Current;

    public bool Show;

    /// <summary>
    /// The queue for these points, from the cursor: each journey's drift taken from the pointer as it would have been,
    /// and its modifier lead, button hold and modifier release drawn now. A click whose modifiers match the one before it
    /// carries them over, with no lead of its own.
    /// </summary>
    public static ClickPlan Make(MousePointer pointer, Vector2 cursor, IReadOnlyList<Vector2> points, IReadOnlyList<bool> rights,
        Keys[] modifiers, bool show, Func<int> holdMs, Func<int> modifierMs)
    {
        var plan = new ClickPlan { Show = show };
        var from = cursor;
        var mods = modifiers ?? Modifier.None;

        for (var i = 0; i < points.Count; i++)
        {
            var to = points[i];
            var drift = pointer.NextJourney();
            var teleport = pointer.TravelMsPerRootPx <= 0;
            var path = teleport
                ? new Travel(from, to, 1f, 0f, 0f, drift)
                : new Travel(from, to, pointer.MinTravelMs, pointer.TravelMsPerRootPx, pointer.ArcPercent, drift);
            var carried = i > 0 && Modifier.Any(mods);

            plan.Items.Add(new Planned(from, to, drift, path, carried || !Modifier.Any(mods) ? 0 : modifierMs(), carried,
                holdMs(), modifierMs(), i < rights.Count && rights[i], mods));
            from = to;
        }

        return plan;
    }

    /// <summary>The paths, clicks and modifiers from the click in hand on. See the class.</summary>
    public void Draw(Graphics graphics)
    {
        var first = Current != null ? Next - 1 : Next;

        for (var i = Math.Max(0, first); i < Items.Count; i++)
        {
            var item = Items[i];
            var current = i == first;
            var line = current ? Color.FromArgb(230, 255, 210, 70) : Color.FromArgb(110, 255, 255, 255);
            var points = new Vector2[PathSamples];

            for (var k = 0; k < PathSamples; k++)
                points[k] = item.Path.At(k / (PathSamples - 1f));

            graphics.DrawPolyLine(points, line, current ? 2f : 1.2f);

            var modifier = ModifierLetters(item.Modifiers);

            // The modifier going down, on the path where it does.
            if (item.LeadMs > 0 && modifier.Length > 0)
            {
                var at = item.Path.At(item.Path.ProgressAt(Math.Max(0f, item.Path.Ms - item.LeadMs)));
                Letter(graphics, modifier, at + new Vector2(0f, -12f), true);
            }

            // The click: down bright, up dim beside it.
            var button = item.Right ? "R" : "L";
            Letter(graphics, button, item.To, true);
            Letter(graphics, button, item.To + new Vector2(10f, 0f), false);
            graphics.DrawText($"{i + 1}", item.To + new Vector2(-12f, -12f), Color.FromArgb(150, 200, 200, 200));

            // The modifier coming up, after the last click that holds it.
            var last = i == Items.Count - 1 || !Items[i + 1].Carried;

            if (last && modifier.Length > 0)
                Letter(graphics, modifier, item.To + new Vector2(20f, 0f), false);
        }
    }

    private const int PathSamples = 20;

    private static void Letter(Graphics graphics, string text, Vector2 at, bool down)
    {
        graphics.DrawText(text, at, down ? Color.FromArgb(255, 255, 240, 120) : Color.FromArgb(120, 200, 200, 200),
            ExileCore2.Shared.Enums.FontAlign.Center | ExileCore2.Shared.Enums.FontAlign.VerticalCenter);
    }

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

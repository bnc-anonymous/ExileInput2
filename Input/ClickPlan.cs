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
        bool Right, Keys[] Modifiers, Travel.Flourish Shape);

    /// <summary>
    /// The share of planned journeys that swoop: their curve pushed well out to one side, an arc most of their own length
    /// wide, and their time stretched for the longer way. Only on journeys of SwoopShortestPx or more. Chosen, 5%.
    /// </summary>
    private const double SwoopChance = 0.05;

    private const float SwoopShortestPx = 40f;

    /// <summary>
    /// The share of planned journeys that overshoot: run 3 to 7% of their length past the target (4 to 24px) and settle
    /// back onto it over 60 to 120ms more. Only on journeys of OvershootShortestPx or more. Chosen, 5%.
    /// </summary>
    private const double OvershootChance = 0.05;

    /// <summary>
    /// The shortest journey that may overshoot: 60px, so a hop between neighbouring 70px inventory cells can. At 150px a
    /// deposit, all such hops, had two journeys in fifty long enough (2026-10-11). Chosen.
    /// </summary>
    private const float OvershootShortestPx = 60f;

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
            // Each control's reach capped at a third of this way, its own length in an evenly spaced queue. Uncapped, the
            // reach follows the ways either side, so a short hop between two long ones - a third load in the inventory
            // between the bench's result and the button - swung out in an arc several times its own length (2026-10-11).
            var control1 = from + Capped((to - before2) / 6f, line.Length() / 3f) + across;
            var control2 = to - Capped((after - from) / 6f, line.Length() / 3f) + across;
            var minMs = minMsOf(i);
            var perRootPx = pointer.TravelMsPerRootPx;
            var length = line.Length();
            var shape = Travel.Flourish.None;

            // **A swoop, now and then**: both controls pushed out to the side the way was already turning, and the time
            // stretched as the duration's rule would have it for the longer way, so the cursor does not whip round it.
            if (perRootPx > 0 && length >= SwoopShortestPx && pointer.Chance() < SwoopChance)
            {
                var incoming = from - before2;
                var turning = incoming.X * line.Y - incoming.Y * line.X;
                var side = turning != 0f ? MathF.Sign(turning) : pointer.Chance() < 0.5 ? -1f : 1f;
                var outward = new Vector2(-line.Y, line.X) / length * side * length * (0.6f + 0.6f * (float)pointer.Chance());

                control1 += outward;
                control2 += outward;

                var curved = CubicLength(from, control1, control2, to);

                shape = shape with
                {
                    DurationScale = (minMs + MathF.Sqrt(curved) * perRootPx) / Math.Max(1f, minMs + MathF.Sqrt(length) * perRootPx),
                };
            }

            // **An overshoot, now and then**: past the target along the way the curve arrives, and back onto it.
            if (perRootPx > 0 && length >= OvershootShortestPx && pointer.Chance() < OvershootChance)
            {
                var arriving = to - control2;
                var heading = arriving.Length() > 1f ? Vector2.Normalize(arriving) : line / length;
                var by = Math.Clamp(length * (0.03f + 0.04f * (float)pointer.Chance()), 4f, 24f);

                shape = shape with { Overshoot = heading * by, SettleMs = 60f + 60f * (float)pointer.Chance() };
            }

            var path = perRootPx <= 0
                ? new Travel(from, to, 1f, 0f, 0f, drift)
                : new Travel(from, to, minMs, perRootPx, pointer.ArcPercent, drift);

            if (perRootPx > 0)
            {
                path.Glide(control1, control2, CreepPx);
                path.Shape(shape);
            }
            var carried = i > 0 && Modifier.Any(mods) && SameKeys(mods, before);

            plan.Items.Add(new Planned(rect, given, from, to, drift, minMs, perRootPx, control1, control2, path,
                carried || !Modifier.Any(mods) ? 0 : modifierMs(), carried, holdMs(), modifierMs(), i < rights.Count && rights[i], mods,
                shape));
            from = to;
            before = mods;
        }

        return plan;
    }

    /// <summary>A cubic curve's length, near enough: the sum of 24 straight pieces along it.</summary>
    private static float CubicLength(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var length = 0f;
        var last = a;

        for (var k = 1; k <= 24; k++)
        {
            var s = k / 24f;
            var r = 1f - s;
            var at = r * r * r * a + 3f * r * r * s * b + 3f * r * s * s * c + s * s * s * d;

            length += Vector2.Distance(last, at);
            last = at;
        }

        return length;
    }

    /// <summary>This vector, shortened to at most this length.</summary>
    private static Vector2 Capped(Vector2 vector, float most)
    {
        var length = vector.Length();

        return length > most && length > 0f ? vector * (most / length) : vector;
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

    /// <summary>When each click was made, by its place in the queue, so a done click fades rather than vanishes. See Draw.</summary>
    private readonly Dictionary<int, DateTime> _doneAt = [];

    /// <summary>How long a done click's path and marks take to fade out.</summary>
    private static readonly TimeSpan DoneFadeFor = TimeSpan.FromSeconds(1);

    /// <summary>Marks the click moved to as made, now: its drawing fades from here. See ExileInput2.Click.</summary>
    public void MarkDone()
    {
        if (Current != null && Next > 0)
            _doneAt[Next - 1] = DateTime.UtcNow;
    }

    /// <summary>
    /// The paths, clicks and modifiers from the click in hand on, at this opacity: 1 while the queue is followed, falling
    /// to 0 over a second once it is given up. Clicks already made fade out over DoneFadeFor from when they were made. See
    /// the class and ExileInput2.Render.
    /// </summary>
    /// <param name="trail">
    /// The cursor's recent places and how far through the current journey it is, for the plan being followed: the
    /// current journey is then drawn as a trail fading behind the cursor and the way still ahead of it, rather than whole.
    /// </param>
    public void Draw(Graphics graphics, Colours colours, Marks marks, float opacity = 1f, bool doneOnly = false,
        CursorTrail? trail = null)
    {
        var first = Current != null ? Next - 1 : Next;
        var now = DateTime.UtcNow;

        if (doneOnly)
            first = Next;

        for (var i = 0; i < Math.Max(0, first); i++)
        {
            if (!marks.Fade || !_doneAt.TryGetValue(i, out var at) || now - at >= DoneFadeFor)
                continue;

            DrawItem(graphics, i, colours.Done, marks, false, opacity * (1f - (float)((now - at).TotalMilliseconds / DoneFadeFor.TotalMilliseconds)));
        }

        if (doneOnly)
            return;

        for (var i = Math.Max(0, first); i < Items.Count; i++)
            DrawItem(graphics, i, i == first ? colours.Current : colours.Later, marks, i == first, opacity,
                i == first && Current != null ? trail : null, colours.Later);

        if (trail is { } behind)
            DrawTrail(graphics, behind, colours.Current, opacity);
    }

    /// <summary>The cursor's recent places, and how far through its journey it is. See Draw.</summary>
    internal readonly record struct CursorTrail(IReadOnlyList<(Vector2 Where, float Age)> Points, float Progress);

    /// <summary>The cursor's recent places as a line fading out behind it, newest the strongest. See Draw.</summary>
    private static void DrawTrail(Graphics graphics, CursorTrail trail, Color colour, float opacity)
    {
        var points = trail.Points;

        for (var k = 1; k < points.Count; k++)
        {
            var strength = Math.Clamp(1f - points[k].Age, 0f, 1f);

            if (strength <= 0f || Vector2.Distance(points[k - 1].Where, points[k].Where) < 0.5f)
                continue;

            var faded = Color.FromArgb((int)(colour.A * strength * Math.Clamp(opacity, 0f, 1f)), colour.R, colour.G, colour.B);

            graphics.DrawLine(points[k - 1].Where, points[k].Where, 1f + 2f * strength, faded);
        }
    }

    /// <summary>One click's path and marks in its state's colour, at this opacity. See Draw.</summary>
    private void DrawItem(Graphics graphics, int i, Color state, Marks marks, bool current, float opacity,
        CursorTrail? trail = null, Color ahead = default)
    {
        Color Faded(float strength) =>
            Color.FromArgb((int)(state.A * strength * Math.Clamp(opacity, 0f, 1f)), state.R, state.G, state.B);

        var bright = Faded(1f);
        var dim = Faded(UpStrength);
        var item = Items[i];
        var line = Faded(1f);
        var points = new Vector2[PathSamples];

        // With the cursor on its way, only the way still ahead of it, in the colour of what is to come; behind it the
        // trail is drawn. See DrawTrail.
        if (trail is { } along)
        {
            var start = Math.Clamp(along.Progress, 0f, 1f);

            for (var k = 0; k < PathSamples; k++)
                points[k] = item.Path.At(start + (1f - start) * k / (PathSamples - 1f));

            if (start < 1f)
                graphics.DrawPolyLine(points, Color.FromArgb((int)(ahead.A * Math.Clamp(opacity, 0f, 1f)), ahead.R, ahead.G, ahead.B), 1.2f);
        }
        else
        {
            for (var k = 0; k < PathSamples; k++)
                points[k] = item.Path.At(k / (PathSamples - 1f));

            graphics.DrawPolyLine(points, line, current ? 2f : 1.2f);
        }

        var modifier = marks.Modifiers ? ModifierLetters(item.Modifiers) : "";

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

        if (marks.Buttons)
        {
            Letter(graphics, button, item.To, bright);
            Letter(graphics, button, item.To + step, dim);
        }

        if (marks.Numbers)
            Letter(graphics, $"{i + 1}", item.To - new Vector2(0f, size.Y + 2f), dim);

        // The modifier coming up, after the last click that holds it.
        var last = i == Items.Count - 1 || !Items[i + 1].Carried;

        if (last && modifier.Length > 0)
            Letter(graphics, modifier, item.To + step * 2f, dim);
    }

    private const int PathSamples = 20;

    private static void Letter(Graphics graphics, string text, Vector2 at, Color colour)
    {
        graphics.DrawText(text, at, colour,
            ExileCore2.Shared.Enums.FontAlign.Center | ExileCore2.Shared.Enums.FontAlign.VerticalCenter);
    }

    /// <summary>
    /// The colours a plan is drawn in, from ExileInput2's settings: the click being made and the way to it, the clicks to
    /// come, and the clicks made as they fade. Each colour's own alpha is its strength. See ExileInput2Settings.
    /// </summary>
    internal readonly record struct Colours(Color Current, Color Later, Color Done);

    /// <summary>
    /// Which marks a plan is drawn with, from ExileInput2's settings: the button letters, the modifier letters, each
    /// click's number, and whether clicks made and queues given up fade out or go at once. The paths always draw.
    /// </summary>
    internal readonly record struct Marks(bool Buttons, bool Modifiers, bool Numbers, bool Fade);

    /// <summary>
    /// How strong a mark of something coming up - a button, a modifier - and a click's number are, as a share of the
    /// colour's own: fainter than something going down, in the same colour.
    /// </summary>
    private const float UpStrength = 0.63f;

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

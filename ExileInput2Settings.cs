using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using Newtonsoft.Json;

namespace ExileInput2;

public class ExileInput2Settings : ISettings
{
    public ExileInput2Settings()
    {
        SeedUi = new CustomNode(DrawSeed);
    }
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Aim Within (%)",
        "How much of a target to aim inside, as a percentage of its size.\n\n" +
        "Nought is dead centre every time, which is both conspicuous and brittle - a rect read a " +
        "pixel off centre is still hit anywhere inside it. High values reach the edges, where a " +
        "rect drawn slightly larger than the thing it represents starts missing.\n\n" +
        "Clicks cluster towards the middle whatever this is, thinning towards its edge.\n\n" +
        "One figure for every plugin that asks, which is the point of this living here.")]
    public RangeNode<int> SpreadPercent { get; set; } = new RangeNode<int>(90, 0, 95);

    [Menu("Stay Put When Already There",
        "Leave the cursor alone when it is already on the thing being aimed at.\n\n" +
        "A hand resting on a button does not lift off and land again on it before pressing. " +
        "Twitching forty pixels to a different spot inside the same row is a movement with nothing " +
        "behind it, which reads worse than not moving, and it costs a journey each time to end up " +
        "where it started.\n\n" +
        "The click still goes out, modifiers and all - only the travel is skipped. Counted three " +
        "pixels inside the edge rather than the bare rect, because a rect is often drawn a shade " +
        "larger than the thing it stands for and staying on that margin would trade a small " +
        "movement for a click on the boundary.")]
    public ToggleNode StayPut { get; set; } = new ToggleNode(true);

    [Menu("Arrived Within (px)",
        "How near the cursor must be to where it was sent before a click is allowed.\n\n" +
        "Not zero: the game reports its cursor a frame or two behind, and rounding means the pixel " +
        "you set is not always the pixel read back. Too large and a click is accepted while the " +
        "pointer is still a row away from the thing it is aimed at - on a 54 pixel list row, forty " +
        "is most of a row.")]
    public RangeNode<int> TolerancePx { get; set; } = new RangeNode<int>(12, 1, 100);

    [Menu("Shortest Move (ms)",
        "The least time a move may take, however near the target.\n\n" +
        "A teleport is not a fast movement, it is the absence of one, and it is the most obvious " +
        "thing about automated input because a hand cannot do it.")]
    public RangeNode<int> MinTravelMs { get; set; } = new RangeNode<int>(40, 0, 1000);

    [Menu("Move Time Per Root Pixel (ms)",
        "How much longer a move takes for being further, per square root of the distance.\n\n" +
        "The square root and not the distance: a hand crossing the screen does not take ten times " +
        "as long as one crossing a tenth of it, because reaching is mostly aiming and aiming costs " +
        "about the same either way.\n\n" +
        "Zero teleports, which is what this did before and is the setting to reach for when " +
        "something is wrong and the question is whether the path is why.")]
    public RangeNode<int> TravelMsPerRootPx { get; set; } = new RangeNode<int>(6, 0, 60);

    [Menu("Path Bow (%)",
        "How far the path curves off the straight line, as a percentage of its length.\n\n" +
        "A wrist and an elbow rotate, so a hand travels an arc; only a mouse dragged along a ruler " +
        "goes straight. The direction is chosen per move, so two journeys between the same two " +
        "points do not trace the same line.")]
    public RangeNode<int> ArcPercent { get; set; } = new RangeNode<int>(7, 0, 60);

    [Menu("Stop When The Mouse Moves Within (px)",
        "How far the cursor may sit from anywhere this plugin put it before that counts as you " +
        "having taken the mouse back.\n\n" +
        "Moving the mouse is the stop. Everything halts - the journey, the gesture, any key being " +
        "held - and the plugin that was driving has to ask for the cursor again before it does " +
        "anything else, so a hand coming to rest does not hand it straight back.\n\n" +
        "There is no panic key and there is not meant to be one: reaching for the mouse is what " +
        "anyone does when something on screen is doing something they did not want, and a key " +
        "they have to remember is a key they will not reach for. Too small and the game's own " +
        "rounding trips it; too large and a real nudge goes unnoticed.")]
    public RangeNode<int> StopTolerancePx { get; set; } = new RangeNode<int>(10, 2, 60);

    [Menu("Give Up Waiting After (ms)",
        "How long the cursor may fail to arrive before the move is given up on.\n\n" +
        "A move that never lands means something is wrong about where it was aimed, and carrying " +
        "on from there clicks somewhere nobody chose. The caller is stopped the same way a moved " +
        "mouse stops it.")]
    public RangeNode<int> ArriveTimeoutMs { get; set; } = new RangeNode<int>(1500, 100, 10000);

    [Menu("Most Move Time Per Frame (ms)",
        "How much of a move one frame may cover. A move is timed by the clock, so a frame\n" +
        "held up - the game or the HUD pausing - would otherwise jump the cursor along\n" +
        "the move in one go. Keep it well above a normal frame, or ordinary moves stutter.\n\n" +
        "Lower makes a pause show as a smaller jump and a longer slow-down. 0 turns it off.")]
    public RangeNode<int> MostMoveTimePerFrameMs { get; set; } = new RangeNode<int>(50, 0, 200);

    [Menu("Click Hold Shortest (ms)",
        "The shortest a mouse button is held down for a click. Each click's hold is drawn\n" +
        "afresh between Shortest and Longest, most often near Usual.")]
    public RangeNode<int> ClickHoldShortestMs { get; set; } = new RangeNode<int>(50, 0, 300);

    [Menu("Click Hold Usual (ms)", "The most common time a mouse button is held down for a click.")]
    public RangeNode<int> ClickHoldUsualMs { get; set; } = new RangeNode<int>(80, 0, 300);

    [Menu("Click Hold Longest (ms)", "The longest a mouse button is held down for a click.")]
    public RangeNode<int> ClickHoldLongestMs { get; set; } = new RangeNode<int>(100, 0, 300);

    [Menu("Modifier Timing Shortest (ms)",
        "The shortest time ctrl, shift or alt goes down before a click - on the way, while\n" +
        "the cursor is still moving - and comes up after the button. Each is drawn afresh\n" +
        "between Shortest and Longest, most often near Usual.")]
    public RangeNode<int> ModifierTimingShortestMs { get; set; } = new RangeNode<int>(50, 0, 300);

    [Menu("Modifier Timing Usual (ms)", "The most common time a modifier goes down before a click and up after it.")]
    public RangeNode<int> ModifierTimingUsualMs { get; set; } = new RangeNode<int>(80, 0, 300);

    [Menu("Modifier Timing Longest (ms)", "The longest time a modifier goes down before a click and up after it.")]
    public RangeNode<int> ModifierTimingLongestMs { get; set; } = new RangeNode<int>(100, 0, 300);

    [Menu("Planned Clicks: Show Buttons",
        "When a plugin asks for its planned clicks to be drawn: L or R at each click, for the\n" +
        "left or right button - bright where it goes down, fainter beside it where it comes up.")]
    public ToggleNode PlanShowButtons { get; set; } = new ToggleNode(true);

    [Menu("Planned Clicks: Show Modifiers",
        "C, S or A for ctrl, shift or alt: bright on the path where it goes down, fainter\n" +
        "after the last click that holds it, where it comes up.")]
    public ToggleNode PlanShowModifiers { get; set; } = new ToggleNode(true);

    [Menu("Planned Clicks: Show Numbers", "Each click's place in the queue, above it.")]
    public ToggleNode PlanShowNumbers { get; set; } = new ToggleNode(false);

    [Menu("Planned Clicks: Fade Out",
        "Clicks made, and a queue given up, fade out over a second. Off, they go at once.")]
    public ToggleNode PlanFadeOut { get; set; } = new ToggleNode(true);

    [Menu("Planned Click Colour: Current",
        "When a plugin asks for its planned clicks to be drawn: the click being made and the\n" +
        "way to it. Marks of a button or key coming up, and the numbers, are drawn fainter.")]
    public ColorNode PlanCurrentColour { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 90, 255, 120));

    [Menu("Planned Click Colour: Later", "The clicks still to come, and the ways to them.")]
    public ColorNode PlanLaterColour { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 255, 210, 80));

    [Menu("Planned Click Colour: Done", "The clicks made, as they fade out over a second.")]
    public ColorNode PlanDoneColour { get; set; } = new ColorNode(System.Drawing.Color.FromArgb(255, 170, 170, 170));

    [Menu("Drop An Idle Claim After (s)",
        "How long a plugin may hold the cursor without using it before the claim is taken off it.\n\n" +
        "There is one cursor and there may be several plugins, so one takes it at a time. A plugin " +
        "that throws mid-gesture or is reloaded never reaches its release, and without this that " +
        "is the cursor gone for the session - with no error anywhere, just every other plugin " +
        "quietly doing nothing.\n\n" +
        "Long enough never to expire mid-gesture: a move waits on the game and a click waits on a " +
        "confirmation, and both are far under a second.")]
    public RangeNode<int> ClaimExpirySeconds { get; set; } = new RangeNode<int>(5, 1, 60);

    /// <summary>
    /// This install's own seed. It shuffles the noise every journey is shaped by - where the aim wanders, how a path
    /// bows - and moves each of the feel settings 10 to 20 per cent up or down from its slider
    /// (see Varied). Drawn at random the first time the plugin starts (see SeedDrawn) and kept, so each install keeps one
    /// habit of its own across sessions. Nought means none: Perlin's own noise, and the sliders used as they are.
    /// </summary>
    public int Seed { get; set; }

    /// <summary>
    /// Whether this install's seed has been drawn. Apart from Seed because nought is a seed a player may choose, and the
    /// first start must not take that choice for a seed never drawn.
    /// </summary>
    public bool SeedDrawn { get; set; }

    /// <summary>
    /// Which of the shipped changes to saved settings this file has been through. Nought in a file written before there
    /// were any, since a missing property keeps its default when the file is read. See Migrate.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>The latest shipped change to saved settings. Raise it with a step in Migrate.</summary>
    internal const int CurrentSettingsVersion = 2;

    /// <summary>
    /// Brings a saved file up to CurrentSettingsVersion, once, and says what changed; empty when nothing did.
    ///
    /// **1 - Aim Within from 50 to 90.** Clicks now cluster towards the middle whatever the spread, so 90 uses most of a
    /// target without crowding its edge (2026-10-10). Only a file still at the old default of 50 is moved: any other
    /// value was chosen. One left at 50 on purpose cannot be told from one never touched, and is moved too.
    /// </summary>
    internal static string Migrate(ExileInput2Settings settings)
    {
        if (settings == null || settings.SettingsVersion >= CurrentSettingsVersion)
            return "";

        var said = "";

        if (settings.SettingsVersion < 1 && settings.SpreadPercent.Value == 50)
        {
            settings.SpreadPercent.Value = 90;
            said = "Aim Within moved from the old default of 50% to 90%";
        }

        // 2 - Most Move Time Per Frame from 25 to 50: at 25, randomised down to 20, it held back ordinary frames as well as
        // stalls, and moves stuttered (2026-10-11). Only a file at the old default is moved.
        if (settings.SettingsVersion < 2 && settings.MostMoveTimePerFrameMs.Value == 25)
        {
            settings.MostMoveTimePerFrameMs.Value = 50;
            said = said.Length > 0 ? said + "; Most Move Time Per Frame from 25 to 50" : "Most Move Time Per Frame moved from 25 to 50";
        }

        settings.SettingsVersion = CurrentSettingsVersion;
        return said;
    }

    /// <summary>
    /// The seed, the buttons, and what the seed makes of the sliders above: a box to read or type the seed, New seed for
    /// one at random, Reset to defaults for the sliders, and each feel setting as used.
    /// </summary>
    [JsonIgnore]
    [Menu("Seed")]
    public CustomNode SeedUi { get; set; }

    /// <summary>
    /// The seed's help, shown on its own (?) beside the seed's buttons. Not given to the Menu attribute: ExileCore draws
    /// that one after everything the node draws, below the values as used, where it reads as belonging to the last line.
    /// </summary>
    private const string SeedHelp =
        "This install's own seed. It shapes every move - where the aim wanders, how a path\n" +
        "bows - and moves each feel setting above from its slider: 10 to 20 per cent up or\n" +
        "down, Aim Within 10 per cent down to none, Shortest Move 5 per cent down to 20 up,\n" +
        "and each click timing up to 5 per cent either way. The values below are the ones\n" +
        "used. Drawn at random the first time the plugin starts.\n\n" +
        "Nought uses the sliders as they are. Reset to defaults puts the sliders back and\n" +
        "leaves the seed alone.";

    /// <summary>
    /// The settings the seed varies: how input looks, not how carefully it lands or when it stops. Arrived Within, Stop
    /// When The Mouse Moves Within, Give Up Waiting After and Drop An Idle Claim After are left as set - varying them
    /// would change how safe a click is, not how it looks.
    /// </summary>
    private (string Name, RangeNode<int> Node)[] Varied =>
    [
        ("Aim Within (%)", SpreadPercent),
        ("Shortest Move (ms)", MinTravelMs),
        ("Move Time Per Root Pixel (ms)", TravelMsPerRootPx),
        ("Path Bow (%)", ArcPercent),
    ];

    /// <summary>
    /// How far the seed moves one varied setting: 10 to 20 per cent up or down, the same for the same seed and setting
    /// every time, except where FactorBoundsOf gives a setting its own bounds. One for nought.
    /// </summary>
    internal static float SeedFactor(int seed, int which)
    {
        if (seed == 0)
            return 1f;

        var draw = new System.Random(unchecked(seed * 31 + (which + 1) * 7919));

        if (FactorBoundsOf(which) is var (least, most))
            return (float)(least + draw.NextDouble() * (most - least));

        var size = 0.10 + draw.NextDouble() * 0.10;

        return (float)(draw.Next(2) == 0 ? 1d - size : 1d + size);
    }

    /// <summary>
    /// A varied setting's own bounds for its seed factor, anywhere between them; null for the 10 to 20 per cent either
    /// way. Aim Within from 10 per cent down to none: wider than its slider aims too near a target's edge. Shortest Move
    /// from 5 per cent down to 20 up: much below its slider, short hops clicked too quickly. Both chosen 2026-10-11.
    /// </summary>
    internal static (double Least, double Most)? FactorBoundsOf(int which) => which switch
    {
        AimWithinIndex => (0.90, 1.00),
        ShortestMoveIndex => (0.95, 1.20),
        _ => null,
    };

    /// <summary>Aim Within's and Shortest Move's places in Varied. See FactorBoundsOf.</summary>
    internal const int AimWithinIndex = 0;

    internal const int ShortestMoveIndex = 1;

    /// <summary>A varied setting as used: its slider moved by the seed, rounded, within the slider's range.</summary>
    private int Used(int which)
    {
        var node = Varied[which].Node;

        return System.Math.Clamp((int)System.Math.Round(node.Value * SeedFactor(Seed, which)), node.Min, node.Max);
    }

    internal int UsedSpreadPercent => Used(0);

    internal int UsedMinTravelMs => Used(1);

    internal int UsedTravelMsPerRootPx => Used(2);

    internal int UsedArcPercent => Used(3);

    /// <summary>
    /// How far the seed moves one click timing: up to 5 per cent either way, the same for the same seed and timing every
    /// time - less than the feel settings' 10 to 20, as these are already drawn afresh for every click. One for nought.
    /// </summary>
    internal static float TimingSeedFactor(int seed, int which)
    {
        if (seed == 0)
            return 1f;

        var draw = new System.Random(unchecked(seed * 31 + (which + 101) * 7919));

        return (float)(1d + (draw.NextDouble() * 2d - 1d) * 0.05);
    }

    /// <summary>One click timing as used: its slider moved by the seed, rounded, within the slider's range.</summary>
    private static int UsedTiming(RangeNode<int> node, int seed, int which) =>
        System.Math.Clamp((int)System.Math.Round(node.Value * TimingSeedFactor(seed, which)), node.Min, node.Max);

    /// <summary>Three timings as used, put in order whatever the sliders say: shortest, usual, longest.</summary>
    private (int Least, int Usual, int Most) UsedSpread(RangeNode<int> shortest, RangeNode<int> usual, RangeNode<int> longest, int first)
    {
        var values = new[] { UsedTiming(shortest, Seed, first), UsedTiming(usual, Seed, first + 1), UsedTiming(longest, Seed, first + 2) };

        System.Array.Sort(values);
        return (values[0], values[1], values[2]);
    }

    /// <summary>How long a mouse button is held for a click, as used. See HumanTiming.</summary>
    internal (int Least, int Usual, int Most) UsedClickHold => UsedSpread(ClickHoldShortestMs, ClickHoldUsualMs, ClickHoldLongestMs, 0);

    /// <summary>How long a modifier is down before a click and after its button, as used. See HumanTiming.</summary>
    internal (int Least, int Usual, int Most) UsedModifierTiming =>
        UsedSpread(ModifierTimingShortestMs, ModifierTimingUsualMs, ModifierTimingLongestMs, 3);

    private void DrawSeed()
    {
        var seed = Seed;

        ImGuiNET.ImGui.SetNextItemWidth(160f);

        if (ImGuiNET.ImGui.InputInt("Seed##exileInput2Seed", ref seed, 0, 0))
            Seed = seed;

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("New seed##exileInput2NewSeed"))
            Seed = NewSeed();

        ImGuiNET.ImGui.SameLine();

        if (ImGuiNET.ImGui.Button("Reset to defaults##exileInput2Reset"))
            ResetToDefaults();

        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.TextDisabled("(?)");

        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip(SeedHelp);

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.TextDisabled(Seed == 0 ? "With seed 0, the sliders as they are:" : "With this seed, as used:");

        for (var i = 0; i < Varied.Length; i++)
        {
            var (name, node) = Varied[i];
            var change = (SeedFactor(Seed, i) - 1f) * 100f;

            ImGuiNET.ImGui.TextUnformatted($"  {name}: {Used(i)}" +
                                (Seed == 0 ? "" : $"   (slider {node.Value}, {(change >= 0 ? "+" : "")}{change:0}%)"));
        }

        var hold = UsedClickHold;
        var modifier = UsedModifierTiming;

        ImGuiNET.ImGui.TextUnformatted($"  Click Hold (ms): {hold.Least} / {hold.Usual} / {hold.Most}" +
                            (Seed == 0 ? "" : "   (shortest / usual / longest, each up to 5% from its slider)"));
        ImGuiNET.ImGui.TextUnformatted($"  Modifier Timing (ms): {modifier.Least} / {modifier.Usual} / {modifier.Most}");
    }

    /// <summary>Every slider and switch back to its default. The seed is left as it is.</summary>
    private void ResetToDefaults()
    {
        var defaults = new ExileInput2Settings();

        SpreadPercent.Value = defaults.SpreadPercent.Value;
        StayPut.Value = defaults.StayPut.Value;
        TolerancePx.Value = defaults.TolerancePx.Value;
        MinTravelMs.Value = defaults.MinTravelMs.Value;
        TravelMsPerRootPx.Value = defaults.TravelMsPerRootPx.Value;
        ArcPercent.Value = defaults.ArcPercent.Value;
        StopTolerancePx.Value = defaults.StopTolerancePx.Value;
        ArriveTimeoutMs.Value = defaults.ArriveTimeoutMs.Value;
        MostMoveTimePerFrameMs.Value = defaults.MostMoveTimePerFrameMs.Value;
        ClaimExpirySeconds.Value = defaults.ClaimExpirySeconds.Value;
        ClickHoldShortestMs.Value = defaults.ClickHoldShortestMs.Value;
        ClickHoldUsualMs.Value = defaults.ClickHoldUsualMs.Value;
        ClickHoldLongestMs.Value = defaults.ClickHoldLongestMs.Value;
        ModifierTimingShortestMs.Value = defaults.ModifierTimingShortestMs.Value;
        ModifierTimingUsualMs.Value = defaults.ModifierTimingUsualMs.Value;
        ModifierTimingLongestMs.Value = defaults.ModifierTimingLongestMs.Value;
        PlanShowButtons.Value = defaults.PlanShowButtons.Value;
        PlanShowModifiers.Value = defaults.PlanShowModifiers.Value;
        PlanShowNumbers.Value = defaults.PlanShowNumbers.Value;
        PlanFadeOut.Value = defaults.PlanFadeOut.Value;
        PlanCurrentColour.Value = defaults.PlanCurrentColour.Value;
        PlanLaterColour.Value = defaults.PlanLaterColour.Value;
        PlanDoneColour.Value = defaults.PlanDoneColour.Value;
    }

    /// <summary>A seed at random: a cryptographic draw, never nought, which means none.</summary>
    internal static int NewSeed() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
}

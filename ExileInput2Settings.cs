using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace ExileInput2;

public class ExileInput2Settings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Aim Within (%)",
        "How much of a target to aim inside, as a percentage of its size.\n\n" +
        "Nought is dead centre every time, which is both conspicuous and brittle - a rect read a " +
        "pixel off centre is still hit anywhere inside it. High values reach the edges, where a " +
        "rect drawn slightly larger than the thing it represents starts missing.\n\n" +
        "One figure for every plugin that asks, which is the point of this living here.")]
    public RangeNode<int> SpreadPercent { get; set; } = new RangeNode<int>(50, 0, 95);

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

    [Menu("Hold A Modifier For (ms)",
        "How long control or shift is held either side of a click.\n\n" +
        "Small and not optional. On a currency item the difference between ctrl+right and right is " +
        "the difference between putting it away and picking it up.")]
    public RangeNode<int> ModifierHoldMs { get; set; } = new RangeNode<int>(60, 0, 200);

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
    /// This install's own seed for the noise every journey is shaped by - where the aim wanders, how a path bows, when a
    /// modifier goes down. Nought until the plugin first starts, which draws one at random and saves it, so each install
    /// keeps one habit of its own across sessions rather than every install sharing the same one. Not shown in the
    /// menu: there is nothing to choose about it.
    /// </summary>
    public int Seed { get; set; }
}

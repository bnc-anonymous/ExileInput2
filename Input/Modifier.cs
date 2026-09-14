using System.Collections.Generic;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>
/// The modifier keys, as a set rather than as two booleans.
///
/// This plugin started with ctrl and shift because that is what the flipper needed, and carried
/// them as a pair of bools through the bridge, the gesture, the stroke and the button presses.
/// Buttons.cs has a note warning that the pair had already drifted out of step with the pointer's
/// own modifier list, and calls it "a trap laid for whoever first wants a plain shift-click".
///
/// Alt is what came for the trap. Crafting wants shift held so the currency stays on the cursor
/// between applications, and alt on top of it to switch which currency is being applied - so the
/// combination alt-with-shift has to be expressible, and a third bool would only move the trap
/// along by one.
///
/// So the surface is a set of keys, which is what the pointer and <see cref="HeldKeys"/> have
/// always taken. The bools survive as a convenience at the edges, converted here.
/// </summary>
internal static class Modifier
{
    /// <summary>Nothing held. Distinct from null, and the commonest case by a wide margin.</summary>
    public static readonly Keys[] None = [];

    /// <summary>
    /// The physical keys for a ctrl/shift/alt combination.
    ///
    /// Left-hand keys throughout, matching what the plugin has always pressed. The game does not
    /// distinguish them and Windows reports the generic modifier as down either way, so there is
    /// no reason to prefer one and a good reason to be consistent: what goes down is what gets
    /// released, and a release that names the other one leaves a key held.
    /// </summary>
    public static Keys[] Of(bool ctrl, bool shift, bool alt = false)
    {
        if (!ctrl && !shift && !alt)
            return None;

        var keys = new List<Keys>(3);

        // Shift first, then control, then alt - the order the old Buttons.Hold used, preserved
        // because a modifier already down while another goes down is a state the game has seen
        // from this plugin for a long time and evidently tolerates.
        if (shift) keys.Add(Keys.LShiftKey);
        if (ctrl) keys.Add(Keys.LControlKey);
        if (alt) keys.Add(Keys.LMenu);

        return keys.ToArray();
    }

    /// <summary>
    /// The Windows virtual key whose async state answers for this key, or zero if none does.
    ///
    /// Asked of the generic modifier rather than the left-hand one - VK_CONTROL rather than
    /// VK_LCONTROL - because that is the one that answers for an injected press. The old code only
    /// ever asked about control; every modifier can be asked the same way, and one that cannot be
    /// confirmed is one that can go down without anybody noticing it did not.
    /// </summary>
    public static int VirtualKey(Keys key) => key switch
    {
        Keys.LControlKey or Keys.RControlKey or Keys.ControlKey => 0x11,
        Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey => 0x10,
        Keys.LMenu or Keys.RMenu or Keys.Menu => 0x12,
        _ => 0,
    };

    /// <summary>Whether this set holds anything at all.</summary>
    public static bool Any(IReadOnlyList<Keys> keys) => keys is { Count: > 0 };

    /// <summary>A set that reads as a list, for a log line or a panel.</summary>
    public static string Describe(IReadOnlyList<Keys> keys)
    {
        if (!Any(keys))
            return "none";

        var names = new List<string>(keys.Count);

        foreach (var key in keys)
        {
            names.Add(key switch
            {
                Keys.LControlKey or Keys.RControlKey or Keys.ControlKey => "ctrl",
                Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey => "shift",
                Keys.LMenu or Keys.RMenu or Keys.Menu => "alt",
                _ => key.ToString(),
            });
        }

        return string.Join("+", names);
    }
}

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>
/// Which way a click goes out, once the cursor has arrived.
///
/// One line of decision, given its own file because it is the line that produced the worst bug in
/// this plugin so far and the only way to check it is to state it apart from the pressing.
/// </summary>
internal static class Gesture
{
    /// <summary>
    /// Whether the button may go straight down on modifiers the approach already pressed.
    ///
    /// The whole point of pressing a modifier in flight is that by the time the cursor lands there
    /// is nothing left to wait for. But "already pressed" has to mean the keys are down *now*, and
    /// the first version of this meant two weaker things instead.
    ///
    /// It asked the pointer whether it had pressed anything, which stayed true after the keys were
    /// let go - so a second click with no move in between took the fast path with nothing held at
    /// all. And it guarded that with a readback that is null until the slow path has run once,
    /// which on a machine that takes the fast path from the start never happens. Two conditions,
    /// neither of which could fail when it mattered.
    ///
    /// The result was a bare left click on Faustus. On a currency item the difference between
    /// ctrl+right and right is the difference between putting it away and picking it up, so this
    /// fails closed: anything short of "the keys are down and we can see them" goes the long way,
    /// which confirms or refuses rather than assuming.
    /// </summary>
    /// <param name="modifiers">
    /// What the gesture wants held. EVERY one of them must read as down, not merely one.
    ///
    /// This used to take two bools and verify only control - the note here said plainly that "shift
    /// has no readback here, so a shift gesture rests on the pointer's own account of itself".
    /// That was survivable while nothing asked for shift alone. It stops being survivable now alt
    /// is expressible: an alt that did not take turns one currency into another, silently, at the
    /// moment of applying it to the item.
    /// </param>
    /// <param name="modifiersReady">
    /// Whether the approach believes it pressed them. Necessary, and nowhere near sufficient - see
    /// above for what happened when it was treated as sufficient.
    /// </param>
    /// <param name="isDown">
    /// What the keyboard actually reports, asked for rather than handed over.
    ///
    /// A function and not a value because the answer is expensive: it polls the key state, and
    /// evaluating it eagerly would read it on every click including the plain ones, which do not
    /// care what any modifier is doing and would each sit through that poll for an answer nothing
    /// looks at. So the order of the conditions is not just tidiness, and the test for it is not
    /// pedantry - it is only consulted once something is actually wanted.
    /// </param>
    public static bool StraightIn(IReadOnlyList<Keys> modifiers, bool modifiersReady, Func<Keys, bool> isDown)
    {
        if (!Modifier.Any(modifiers) || !modifiersReady || isDown == null)
            return false;

        for (var i = 0; i < modifiers.Count; i++)
        {
            if (!isDown(modifiers[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the keys a caller is holding should be brought up before a click is pressed.
    ///
    /// Only where the APPROACH pressed them, which is the whole of the rule and was the whole of
    /// the bug. The plugin keeps one bag of held keys per caller, and both the modifier pressed
    /// partway through a journey and a key the caller deliberately asked to hold go into it. So
    /// releasing the bag before every click - which is what happened - let go of the caller's own
    /// keys as well as the journey's.
    ///
    /// For a caller holding shift to keep a currency selected between applications, that was the
    /// entire craft: shift came up, the selection ended with it, and the click that followed a
    /// microsecond later was a bare left click on the item being crafted, which picks it up rather
    /// than applying anything to it.
    ///
    /// A click asking for no modifiers has nothing of its own to clean up. One asking for modifiers
    /// that were not pressed in flight has nothing either - the stroke is about to press them
    /// itself and release them at the end.
    /// </summary>
    public static bool ReleaseBeforePush(bool modifiersReady) => modifiersReady;
}

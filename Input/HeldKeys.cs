using ExileCore2;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>
/// Keys this plugin is holding down, so that every one of them can be let go.
///
/// A key held by us and forgotten is not a small fault. Control left down turns every click the
/// player makes afterwards into a ctrl+click, which in a stash moves items; shift left down turns
/// one into a whole-stack move; escape left down does nothing until it does. The flipper carried a
/// _heldKey field and a scattering of KeyUp calls in five separate files precisely because this
/// keeps happening, and five copies of a rule is five chances to miss a path.
///
/// So the rule is here once: nothing goes down without being written down, and
/// <see cref="ReleaseAll"/> is safe to call from anywhere, including a catch block, including twice.
///
/// Not static, because it is per machine - one giving up should not release a key another is
/// deliberately holding. <see cref="ReleaseEverything"/> is the exception, for a plugin shutting
/// down or a stop key, where letting go of too much is the safe direction.
/// </summary>
internal sealed class HeldKeys
{
    private readonly HashSet<Keys> _down = [];

    /// <summary>What is currently held, for a panel or a log line.</summary>
    public IReadOnlyCollection<Keys> Down => _down;

    /// <summary>Holds a key, remembering it so it can be released whatever happens next.</summary>
    public void Hold(Keys key)
    {
        try
        {
            if (SentWithScanCode(key))
                PhysicalKeys.Down((int)key);
            else
                Input.KeyDown(key);

            _down.Add(key);
        }
        catch
        {
            // Recorded anyway. A key that may or may not have gone down is one to try to release:
            // an unnecessary KeyUp costs nothing and a missed one costs the session.
            _down.Add(key);
        }
    }

    /// <summary>Lets go of one key.</summary>
    public void Let(Keys key)
    {
        try
        {
            if (SentWithScanCode(key))
                PhysicalKeys.Up((int)key);
            else
                Input.KeyUp(key);
        }
        catch
        {
            // Nothing to do about it, and the record is cleared either way - a key we cannot
            // release is not made more released by remembering it.
        }

        _down.Remove(key);
    }

    /// <summary>
    /// Whether a key goes through PhysicalKeys, with its own scan code: the letters, digits and Space, the keys a player
    /// also holds. The game takes a key sent either way (see PhysicalKeys), so this is for sending as a keyboard does,
    /// not a fix. Modifiers and every other key still go through ExileCore2.Input, because Buttons.Release and
    /// ReleaseEverything let modifiers go that way, and a press and its let-go are kept the same.
    /// </summary>
    private static bool SentWithScanCode(Keys key) =>
        key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or Keys.Space;

    /// <summary>Taps a key, held only as long as it takes to send both halves.</summary>
    public void Tap(Keys key)
    {
        Hold(key);
        Let(key);
    }

    /// <summary>
    /// Lets go of everything this instance is holding.
    ///
    /// Safe on any path out, including one where nothing was ever held.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (var key in new List<Keys>(_down))
            Let(key);

        _down.Clear();
    }

    /// <summary>
    /// The modifiers, whoever pressed them, for a stop key or a plugin shutting down.
    ///
    /// Deliberately not confined to what this instance recorded. The case it exists for is the one
    /// where the record is wrong - a crash between the KeyDown and the bookkeeping, or a machine
    /// disposed without unwinding - and there the honest move is to release everything that does
    /// damage and not ask who owns them.
    ///
    /// Both the sided keys and the generic ones, because a caller may hold either and they are not
    /// the same key to Windows. Hold(Keys.ShiftKey) presses VK_SHIFT; releasing VK_LSHIFT does not
    /// reliably clear it. This list used to name the left-hand pair only, so a plugin holding shift
    /// the generic way - which is what Keys.ShiftKey is - could survive the very sweep meant to
    /// catch it, and alt was not on the list at all.
    ///
    /// A modifier left down after a crash is not a tidiness problem. It turns every click the
    /// player makes afterwards into a different click, and in a stash that moves their things.
    /// </summary>
    public static void ReleaseEverything()
    {
        foreach (var key in new[]
                 {
                     Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey,
                     Keys.ControlKey, Keys.LControlKey, Keys.RControlKey,
                     Keys.Menu, Keys.LMenu, Keys.RMenu,
                     Keys.Escape,
                 })
        {
            try
            {
                Input.KeyUp(key);
            }
            catch
            {
                // Best effort, one key at a time, so one failing does not strand the rest.
            }
        }
    }
}

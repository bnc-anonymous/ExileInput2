using System;
using System.Runtime.InteropServices;

namespace ExileInput2;

/// <summary>
/// Key presses sent the way a keyboard sends them: with the key's own scan code, and marked extended only for the keys a
/// keyboard marks extended. Used for the let-go of a held key when a key block begins, its press again when the block
/// ends, and the letters, digits and Space held or tapped for a caller.
///
/// ExileCore2.Input.KeyDown and KeyUp call keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY) and keybd_event(vk, 0,
/// KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP): scan code 0 and every key extended (read from ExileCore2.dll's IL,
/// 2026-10-25). **The game does not mind.** A W sent that way walks, and the player's own key-up lets it go (tested in
/// game, 2026-10-30). This class was written believing otherwise, after S and Space stayed held following instant
/// loot's key blocks (AutoExpedition, 2026-10-25). The cause was a key pressed again after the player had let go of it,
/// in the gap between a block's end and Restore, which KeyWatch's key-up check has covered since db33f43: with that gap
/// held open for a second, W stayed held whichever way it was sent, and never with the check on. Kept because it sends
/// keys as a keyboard does and costs nothing, not because the game needs it.
/// </summary>
internal static class PhysicalKeys
{
    /// <summary>Presses the key with this virtual-key code.</summary>
    public static void Down(int key) => Send(key, false);

    /// <summary>Lets go of the key with this virtual-key code.</summary>
    public static void Up(int key) => Send(key, true);

    private static void Send(int key, bool up)
    {
        var scanCode = (byte)MapVirtualKey((uint)key, MapvkVkToVsc);
        var flags = (Extended(key) ? KeyeventfExtendedKey : 0u) | (up ? KeyeventfKeyUp : 0u);

        keybd_event((byte)key, scanCode, flags, UIntPtr.Zero);
    }

    /// <summary>
    /// The keys a keyboard sends with the extended flag: Page Up, Page Down, End, Home, the arrows, Print Screen, Insert,
    /// Delete, the right Windows and Apps keys, numpad divide, Num Lock, right Control and right Alt.
    /// </summary>
    private static bool Extended(int key) =>
        key is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    private const uint MapvkVkToVsc = 0;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scanCode, uint flags, UIntPtr extraInfo);
}

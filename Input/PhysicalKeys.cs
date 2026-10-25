using System;
using System.Runtime.InteropServices;

namespace ExileInput2;

/// <summary>
/// Key presses sent the way a keyboard sends them: with the key's own scan code, and marked extended only for the keys a
/// keyboard marks extended. For a sent key that must pair with the player's physical one - the let-go of a held key when
/// a key block begins, and its press again when the block ends.
///
/// **Not ExileCore2.Input.KeyDown and KeyUp**, which call keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY) and keybd_event(vk,
/// 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP): scan code 0 and every key extended (read from ExileCore2.dll's IL,
/// 2026-10-25). A key told apart by scan code then differs from the player's: the player's release after a press sent
/// that way left S and Space held in the game (AutoExpedition instant loot, the player's account, 2026-10-25). Whether
/// the game tells keys apart by scan code is inferred from that, not read. Keys this plugin both presses and lets go,
/// such as a click's modifiers, pair with each other and still go through ExileCore2.Input.
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

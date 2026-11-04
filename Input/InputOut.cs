using ExileCore2;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2;

/// <summary>
/// Where every key, button and cursor move this plugin sends goes out: to the hardware-input boards while they are in use
/// (see Boards), and through ExileCore2.Input and PhysicalKeys otherwise, as before. One place, so a key cannot go down
/// one way and come up the other.
/// </summary>
internal static class InputOut
{
    /// <summary>Presses a key the way ExileCore2.Input does, or on the keyboard board.</summary>
    public static void KeyDown(Keys key)
    {
        if (!Boards.InUse || !Boards.Key(key, true))
            Input.KeyDown(key);
    }

    /// <summary>Lets go of a key the way ExileCore2.Input does, or on the keyboard board.</summary>
    public static void KeyUp(Keys key)
    {
        if (!Boards.InUse || !Boards.Key(key, false))
            Input.KeyUp(key);
    }

    /// <summary>Presses a key with its own scan code (see PhysicalKeys), or on the keyboard board, where every key is a keyboard's.</summary>
    public static void PhysicalDown(int key)
    {
        if (!Boards.InUse || !Boards.Key((Keys)key, true))
            PhysicalKeys.Down(key);
    }

    /// <summary>Lets go of a key with its own scan code (see PhysicalKeys), or on the keyboard board.</summary>
    public static void PhysicalUp(int key)
    {
        if (!Boards.InUse || !Boards.Key((Keys)key, false))
            PhysicalKeys.Up(key);
    }

    /// <summary>Presses or lets go of the left or right mouse button.</summary>
    public static void Button(bool right, bool down)
    {
        if (Boards.InUse)
            Boards.Button(right, down);
        else if (right && down)
            Input.RightDown();
        else if (right)
            Input.RightUp();
        else if (down)
            Input.LeftDown();
        else
            Input.LeftUp();
    }

    /// <summary>Sends the cursor to a screen point: at once, or steered there by the mouse board.</summary>
    public static void CursorTo(Vector2 screen)
    {
        if (Boards.InUse)
            Boards.CursorTo((int)screen.X, (int)screen.Y);
        else
            Input.SetCursorPos(screen);
    }
}

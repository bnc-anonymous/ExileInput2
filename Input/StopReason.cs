namespace ExileInput2;

/// <summary>
/// Why a pointer gave up, or None while it is working.
///
/// There is no panic key in this plugin and there is not meant to be one: taking the mouse back is
/// the stop, because that is the reflex a player already has when something on screen is doing
/// something they did not want. A key they have to remember is a key they will not reach for.
/// </summary>
public enum StopReason
{
    None = 0,

    /// <summary>The cursor turned up somewhere the pointer never put it.</summary>
    PlayerMovedTheMouse,

    /// <summary>A move was asked for and the cursor never confirmed it, inside the timeout.</summary>
    MoveNeverLanded,

    /// <summary>The game window stopped being the foreground one.</summary>
    GameLostFocus,

    /// <summary>Something in the game is taking typed input - chat, a search box.</summary>
    GameIsTakingInput,

    /// <summary>Another caller took the cursor with a higher priority. See ExileInput2.TakeWithPriority.</summary>
    TakenOver,

    /// <summary>The player pressed a key, to a caller that asked to be stopped by keys. See ExileInput2.StopOnPlayerKeys.</summary>
    PlayerPressedAKey,
}

using System;
using System.Collections.Generic;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput2.Tests;

/// <param name="Want">The reason the pointer should end up stopped with, or None for still working.</param>
internal sealed record StopCase(string Name, StopReason Want, string Reason)
{
    /// <summary>Which step the player's hand arrives on, or -1 for a run they never touch.</summary>
    public int PlayerMovesAtStep { get; init; } = -1;

    /// <summary>Which step the player's hand comes off again, the cursor going back to following the sends, or -1 for never.</summary>
    public int PlayerLetsGoAtStep { get; init; } = -1;

    /// <summary>How long the player's mouse is ignored for from the start, in ms, as IgnorePlayerMouse asks. 0 for not at all.</summary>
    public int PlayerIgnoredMs { get; init; }

    /// <summary>Where the player drags the cursor to.</summary>
    public Vector2 PlayerMovesTo { get; init; } = new(40f, 40f);

    /// <summary>
    /// How many frames behind the sends the game's own report is.
    ///
    /// The thing most likely to be got wrong, and the reason the check is a ring rather than a
    /// comparison against the latest send. Mid-journey the two are never equal.
    /// </summary>
    public int ReportLagFrames { get; init; } = 2;

    /// <summary>
    /// How long a HUD frame lasts, which is how often the pointer steps and sends. 16 is 60 fps; ExileCore's
    /// TargetFps runs it faster, 165 on the player's machine (2026-10-10), and the game's report lags by its own
    /// frames, not the HUD's.
    /// </summary>
    public int FrameMs { get; init; } = 16;

    /// <summary>Swallow the sends, so the cursor never confirms and the timeout has a job.</summary>
    public bool SendsGoNowhere { get; init; }

    /// <summary>Acknowledge the stop and try again, which only a release is allowed to do.</summary>
    public bool ResumeAfter { get; init; }

    /// <summary>
    /// The game reports a point partway between the two newest sends it could have seen, rather than one of them, as
    /// it did on 2026-10-11 at 18:23: (620,278), on the line between sends (637,286) and (540,244) and 19 px from both.
    /// </summary>
    public bool ReportsBetweenSends { get; init; }

    /// <summary>Send once and have done with it, the way a zero travel time does.</summary>
    public bool Teleports { get; init; }

    /// <summary>
    /// Finish the journey, let the player move the mouse, then claim again and go somewhere else.
    ///
    /// The session boundary, which is the thing the ring of recent sends has to be cleared at.
    /// </summary>
    public bool SecondSession { get; init; }
}

/// <summary>
/// What happens when the mouse moves and it was not this plugin that moved it.
///
/// The safety model of the whole thing lives in these cases. There is no panic key here and there
/// is not meant to be one: reaching for the mouse is what anyone already does when something on
/// screen is doing something they did not ask for, and a key they have to remember is a key they
/// will not reach for in the moment they need it.
///
/// Which puts all the weight on telling the player's movement from the plugin's own. The game
/// reports its cursor several frames behind where it was put, so mid-journey the reported position
/// and the last sent position are never the same - and a check written as "is the cursor where I
/// last put it" stops on the first step of every journey it ever makes. That is case 2, and it is
/// why the check asks whether the cursor is near ANY point sent recently rather than the newest.
/// </summary>
internal static class StopCases
{
    private static readonly Vector2 Start = new(200f, 900f);
    private static readonly Vector2 Target = new(1100f, 320f);

    internal static readonly StopCase[] All =
    [
        new("1  A run nobody interrupts finishes", StopReason.None,
            "the case that has to pass before any of the others mean anything. A journey the "
            + "player never touches must run to the end, or the safety is just a plugin that "
            + "does not work"),

        new("2  The game reporting the cursor several frames late is not the player",
            StopReason.None,
            "the bug this check is shaped around. The game's cursor trails the sends by a frame "
            + "or several, so mid-journey 'is it where I last put it' is false on every single "
            + "step. Written that way the plugin stops on the first step of every journey it "
            + "makes, blames the player, and there is nothing on screen to say otherwise")
        {
            ReportLagFrames = 6,
        },

        new("3  The player taking the mouse stops it", StopReason.PlayerMovedTheMouse,
            "the whole point. The cursor turns up somewhere the pointer never sent it, which "
            + "means a hand moved it, which means stop - no velocity, no guessing at intent, no "
            + "asking whether they meant it")
        {
            PlayerMovesAtStep = 3,
        },

        new("4  A nudge counts, not just a grab", StopReason.PlayerMovedTheMouse,
            "a hand resting on the mouse and shifting it slightly is still a hand on the mouse. "
            + "The tolerance is there for the game's rounding, not for deciding how serious the "
            + "player was")
        {
            PlayerMovesAtStep = 3,
            PlayerMovesTo = new Vector2(260f, 940f),
        },

        new("5  A stop stays stopped", StopReason.PlayerMovedTheMouse,
            "stepping again after a stop must do nothing at all. A machine polling every tick "
            + "would otherwise carry straight on the moment the hand came to rest, and the player "
            + "would have to hold the mouse still to keep it from happening - which is the "
            + "opposite of taking it back")
        {
            PlayerMovesAtStep = 3,
        },

        new("6  Acknowledging it clears it", StopReason.None,
            "the way back. A caller that has noticed, given up on what it was doing and released "
            + "gets a working pointer again - otherwise the first nudge of a session disables the "
            + "plugin until the game restarts")
        {
            PlayerMovesAtStep = 3,
            ResumeAfter = true,
        },

        new("7  A cursor that ignores where it is sent reads as a hand on the mouse",
            StopReason.PlayerMovedTheMouse,
            "and that is the right answer rather than a near miss. A cursor that will not follow "
            + "is indistinguishable from a cursor somebody is holding, from in here and from the "
            + "caller's side both - in either case this plugin is not driving it, and in either "
            + "case the thing to do is stop. The timeout was written expecting to catch this and "
            + "the movement check gets there first, several steps sooner")
        {
            SendsGoNowhere = true,
        },

        new("8  With no journey to notice, the timeout is what catches it",
            StopReason.MoveNeverLanded,
            "teleport mode sends once and then has nothing to step, so there is no stream of "
            + "positions to compare anything against and case 7's check never gets a look in. "
            + "This is the path the timeout exists for, and the reason it is not dead code")
        {
            SendsGoNowhere = true,
            Teleports = true,
        },

        new("10 At 165 fps, the game reporting the cursor 90ms late is not the player", StopReason.None,
            "case 2 at the frame rate the HUD actually runs at. The ring held the last twelve sends, a fifth of a "
            + "second at 60 fps and 73ms at 165, so a report that lagged a few game frames fell out of it and the "
            + "pointer's own path read as a hand: three runs stopped, blaming the player, on 2026-10-10, the cursor "
            + "found 13, 17 and 24 px from the nearest send")
        {
            FrameMs = 6,
            ReportLagFrames = 15,
        },

        new("11 At 165 fps a nudge still counts", StopReason.PlayerMovedTheMouse,
            "case 4 at the same frame rate. Remembering sends by time rather than count must not "
            + "widen what reads as the pointer's own")
        {
            FrameMs = 6,
            ReportLagFrames = 15,
            PlayerMovesAtStep = 3,
            PlayerMovesTo = new Vector2(260f, 940f),
        },

        new("12 While the player is ignored, a hand on the mouse does not stop it", StopReason.None,
            "a caller that asked for one move and click to land however the player's hand moves. The hand comes and "
            + "goes within the window and the journey carries on to the end")
        {
            PlayerMovesAtStep = 3,
            PlayerLetsGoAtStep = 6,
            PlayerIgnoredMs = 1000,
        },

        new("13 Once the window has passed, a hand stops it again", StopReason.PlayerMovedTheMouse,
            "the window is bounded. Ignored for two frames, the hand arriving at the fourth is a hand on the mouse")
        {
            PlayerMovesAtStep = 3,
            PlayerLetsGoAtStep = 6,
            PlayerIgnoredMs = 32,
        },

        new("14 The game reporting a point between two sends is not the player", StopReason.None,
            "the cursor read on the straight line between two consecutive sends, 19 px from either, with nobody "
            + "touching the mouse: the run stopped and a second reward window went unanswered (2026-10-11 18:23). "
            + "Frames of 50ms make the sends far apart, as a stalled HUD frame does")
        {
            FrameMs = 50,
            ReportsBetweenSends = true,
        },

        new("15 With sends far apart, a nudge off the path still counts", StopReason.PlayerMovedTheMouse,
            "case 14 must not widen what reads as the pointer's own past its path: a hand that moves the cursor "
            + "off the line between sends is still a hand")
        {
            FrameMs = 50,
            ReportsBetweenSends = true,
            PlayerMovesAtStep = 3,
            PlayerMovesTo = new Vector2(260f, 940f),
        },

        new("9  A new session does not inherit the last one's sends", StopReason.None,
            "moving the mouse BETWEEN two runs is not interference with either of them, and it is "
            + "what anybody does - place a bomb, reposition, place the next. Carried across the "
            + "boundary the ring of recent sends says the cursor is somewhere the pointer never "
            + "put it, which is true and means nothing, and the second run stops on its own first "
            + "step blaming the player for a move they made while nothing was running")
        {
            SecondSession = true,
        },
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- when the player takes the mouse back -----------------------------");
        Console.WriteLine();

        foreach (var original in All)
        {
            var c = original;

            var now = TimeSpan.Zero;
            var frame = 0;
            var sent = new List<(int Frame, Vector2 Where)> { (int.MinValue, Start) };
            var playerTook = false;

            // The game's cursor: the newest thing sent to it long enough ago to have been read
            // back, and Start until anything qualifies.
            //
            // Against the frame counter rather than the length of the list, because a lag that
            // only advances when something new is sent never catches up - it would leave the
            // reported cursor a few points short of the destination forever, and a journey that
            // has finished would look like a cursor that refused to go. Real lag is time passing.
            Vector2 Reported()
            {
                if (playerTook)
                    return c.PlayerMovesTo;

                var where = Start;
                var before = Start;

                foreach (var (at, point) in sent)
                {
                    if (at <= frame - c.ReportLagFrames)
                    {
                        before = where;
                        where = point;
                    }
                }

                // A third of the way back from the newest seen send to the one before it.
                return c.ReportsBetweenSends ? where + (before - where) / 3f : where;
            }

            var pointer = new MousePointer(null, new Random(20260912))
            {
                Cursor = Reported,
                Send = to =>
                {
                    if (!c.SendsGoNowhere)
                        sent.Add((frame, to));
                },
                Clock = () => now,
                StayPut = false,
                TolerancePx = 12,
                StopTolerancePx = 10,
                ArriveTimeoutMs = 1500f,
                MinTravelMs = 60f,
                TravelMsPerRootPx = c.Teleports ? 0f : 8f,
                ArcPercent = 15f,
            };

            pointer.Place(Target);
            pointer.IgnorePlayerFor(c.PlayerIgnoredMs);

            var steps = 0;
            var stoppedAtStep = -1;
            var keptMoving = false;

            for (var i = 0; i < 400 && !pointer.Arrived(); i++)
            {
                frame = i;

                if (i == c.PlayerMovesAtStep)
                    playerTook = true;

                if (i == c.PlayerLetsGoAtStep)
                    playerTook = false;

                pointer.Step();
                steps++;

                if (pointer.Stopped != StopReason.None)
                {
                    stoppedAtStep = i;

                    // Case 5's question, asked of every case that stops: does another step do
                    // anything? Nothing may be sent after a stop.
                    var atStop = sent.Count;
                    pointer.Step();
                    keptMoving = sent.Count != atStop;
                    break;
                }

                now += TimeSpan.FromMilliseconds(c.FrameMs);
            }

            var reason = pointer.Stopped;

            if (c.ResumeAfter)
            {
                pointer.Resume();
                reason = pointer.Stopped;
            }

            // The second leg: the player moves the cursor while nothing is running, the caller
            // claims again - which is what clears the ring - and a fresh journey sets off.
            if (c.SecondSession && reason == StopReason.None)
            {
                var parked = new Vector2(640f, 700f);

                playerTook = true;
                c = c with { PlayerMovesTo = parked };
                pointer.Fresh();
                playerTook = false;
                sent.Clear();
                sent.Add((int.MinValue, parked));

                pointer.Place(Start);

                for (var i = 0; i < 400 && !pointer.Arrived(); i++)
                {
                    frame = 400 + i;
                    pointer.Step();
                    steps++;

                    if (pointer.Stopped != StopReason.None)
                        break;

                    now += TimeSpan.FromMilliseconds(c.FrameMs);
                }

                reason = pointer.Stopped;
            }

            var ok = reason == c.Want && !keptMoving;

            // A run nobody interrupts has to have actually gone somewhere, or "still working" is
            // equally true of a pointer that never moved at all.
            if (c.Want == StopReason.None && !c.ResumeAfter && sent.Count <= 1)
                ok = false;

            if (!ok)
                failed++;

            var doing = c.PlayerMovesAtStep >= 0
                ? $"a journey to ({Target.X:0},{Target.Y:0}), the player moving the mouse to "
                  + $"({c.PlayerMovesTo.X:0},{c.PlayerMovesTo.Y:0}) at step {c.PlayerMovesAtStep}"
                : $"a journey to ({Target.X:0},{Target.Y:0}) nobody interrupts";

            if (c.SendsGoNowhere)
                doing += ", with a cursor that ignores where it is sent";
            if (c.Teleports)
                doing += ", teleporting";
            if (c.ResumeAfter)
                doing += ", then released and taken again";
            if (c.SecondSession)
                doing += ", then the mouse moved between runs and a second journey claimed afresh";

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        doing {doing}, reported {c.ReportLagFrames} frame(s) late");
            Console.WriteLine($"        want  {Describe(c.Want)}");
            Console.WriteLine($"        got   {Describe(reason)}  ({steps} step(s), "
                              + $"{sent.Count - 1} sent"
                              + $"{(stoppedAtStep >= 0 ? $", stopped on step {stoppedAtStep}" : "")}"
                              + $"{(keptMoving ? ", AND KEPT MOVING AFTERWARDS" : "")})");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine($"{All.Length - failed}/{All.Length} match");

        return failed;
    }

    private static string Describe(StopReason reason) => reason switch
    {
        StopReason.None => "still working",
        StopReason.PlayerMovedTheMouse => "stopped: the mouse was moved",
        StopReason.MoveNeverLanded => "stopped: the cursor never got there",
        StopReason.GameLostFocus => "stopped: the game is not in front",
        StopReason.GameIsTakingInput => "stopped: the game is taking typed input",
        _ => reason.ToString(),
    };
}

using System;

namespace ExileInput2.Tests;

/// <summary>
/// Everything here that can be decided without a game running.
///
/// Which is less than all of it and more than it looks. The plugin's job is to move a cursor and
/// press a button, and neither of those can be checked from a console - but the decisions around
/// them can: how far a landing may be off, who is allowed to move the mouse, what shape a journey
/// has, and whether a click may go straight down on a modifier already held.
///
/// Those four are where every bug in this plugin so far has been. Not one of them was a mistake
/// about SetCursorPos or SendInput; each was a rule that read correctly and was wrong - a stale
/// flag, a guard that could not fire, a tolerance measured against the wrong thing, a clock timing
/// the wrong interval. That is the kind of fault a table catches and a run of the game does not,
/// because the game shows you the outcome and never the reasoning.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var failed = 0;

        failed += AimCases.Run();

        Console.WriteLine();
        failed += PointerCases.Run();

        Console.WriteLine();
        failed += TravelCases.Run();

        Console.WriteLine();
        failed += GestureCases.Run();

        Console.WriteLine();
        failed += ReleaseCases.Run();

        Console.WriteLine();
        failed += StrokeCases.Run();

        Console.WriteLine();
        failed += LeaseCases.Run();

        Console.WriteLine();
        failed += StopCases.Run();

        Console.WriteLine();
        failed += SeedCases.Run();

        Console.WriteLine();
        failed += SettingsCases.Run();

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "all tables match"
            : $"{failed} case(s) do not match");

        return failed == 0 ? 0 : 1;
    }
}

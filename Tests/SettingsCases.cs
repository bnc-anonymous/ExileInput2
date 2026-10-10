using System;

namespace ExileInput2.Tests;

/// <summary>
/// Shipped changes to saved settings: each moves only what it says, once, and leaves a value somebody chose.
/// </summary>
internal static class SettingsCases
{
    internal static int Run()
    {
        Console.WriteLine("--- shipped changes to saved settings -------------------------------");
        Console.WriteLine();

        var failed = 0;
        var count = 0;

        void Case(string name, int version, int spread, int wantSpread, string why)
        {
            count++;

            var settings = new ExileInput2Settings { SettingsVersion = version };
            settings.SpreadPercent.Value = spread;

            ExileInput2Settings.Migrate(settings);

            // A second run must change nothing: the version says it has been done.
            var once = settings.SpreadPercent.Value;
            ExileInput2Settings.Migrate(settings);

            var ok = settings.SpreadPercent.Value == wantSpread && once == wantSpread &&
                     settings.SettingsVersion == ExileInput2Settings.CurrentSettingsVersion;

            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{name}");
            Console.WriteLine($"        given version {version}, Aim Within {spread}%");
            Console.WriteLine($"        want  {wantSpread}%, version {ExileInput2Settings.CurrentSettingsVersion}, the same after a second run");
            Console.WriteLine($"        got   {settings.SpreadPercent.Value}%, version {settings.SettingsVersion}");
            Console.WriteLine($"        why   {why}");
            Console.WriteLine();
        }

        Case("1  A file left at the old default moves to 90", 0, 50, 90,
            "50 was the default until clicks clustered towards the middle; 90 now uses the target without crowding its edge");
        Case("2  A value somebody chose stays", 0, 70, 70,
            "anything but the old default was set by hand");
        Case("3  A file already brought up to date is not moved again", 1, 50, 50,
            "after the change, 50 can only be a choice made since");

        Console.WriteLine($"{count - failed}/{count} match");
        return failed;
    }
}

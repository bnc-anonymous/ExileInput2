using System;
using System.Collections.Generic;

namespace ExileInput2.Tests;

/// <summary>A thing done to a lease, in order, so a case reads as the story it is.</summary>
internal sealed record Step(string What, Func<Lease, string> Do);

internal sealed record LeaseCase(string Name, Step[] Steps, string WantHolder, string Reason);

/// <summary>
/// Who is allowed to move the mouse, and what happens when the holder goes away.
///
/// There is one cursor and there may be several plugins. Two driving it at once produces a click at
/// the average of two intentions, which lands on neither and is indistinguishable from a misread
/// rect - so this is worth being sure of rather than nearly sure of.
///
/// The cases that matter are the awkward ones: 5, where a holder that died comes back and must not
/// be able to release a claim somebody else has since taken, and 6, where a holder that is working
/// steadily must never have the cursor taken off it mid-gesture.
/// </summary>
internal static class LeaseCases
{
    private static TimeSpan _idle;

    private static Step Take(string who) => new($"{who} takes it",
        l => l.Take(who) ? $"{who} got it" : $"{who} refused");

    private static Step Release(string who) => new($"{who} releases it",
        l => { l.Release(who); return $"{who} released"; });

    private static Step Held(string who) => new($"{who} acts",
        l => l.Held(who) ? $"{who} may act" : $"{who} may not act");

    private static Step Wait(double seconds) => new($"{seconds:0.#}s passes",
        l => { _idle += TimeSpan.FromSeconds(seconds); return $"{seconds:0.#}s later"; });

    private static Step Break() => new("the stop key", l => { l.Break(); return "broken"; });

    internal static readonly LeaseCase[] All =
    [
        new("1  A free cursor goes to whoever asks", [Take("flipper")], "flipper",
            "nothing to arbitrate"),

        new("2  A second plugin is refused rather than queued",
            [Take("flipper"), Take("radar")], "flipper",
            "the refused caller keeps its own state and tries again next tick, which is what all " +
            "of them already do when a click does not go through - so nothing new had to be " +
            "written to handle being refused"),

        new("3  Taking it twice is not an error", [Take("flipper"), Take("flipper")], "flipper",
            "a machine that asks on every tick rather than remembering whether it already holds " +
            "the cursor is doing something reasonable. Refusing that would only teach callers to " +
            "keep a flag this class already keeps for them"),

        new("4  Handing it back lets the next one in",
            [Take("flipper"), Release("flipper"), Take("radar")], "radar",
            "the ordinary case, and the one that has to work for the sharing to be worth having"),

        new("5  A plugin that died and came back cannot release someone else's claim",
            [Take("flipper"), Wait(6), Take("radar"), Release("flipper")], "radar",
            "the whole point of naming the holder on release. The flipper threw mid-gesture and " +
            "never reached its release; the lease expired, radar took it, and then the flipper's " +
            "tidy-up finally ran. Without the name check that release takes the cursor off radar " +
            "in the middle of whatever it is doing, and the fault appears in the other plugin"),

        new("6  A holder that keeps working never has it taken away",
            [Take("flipper"), Wait(4), Held("flipper"), Wait(4), Held("flipper"), Wait(4)],
            "flipper",
            "twelve seconds against a five second expiry, and it still holds - because the expiry " +
            "is about silence, not about elapsed time. It must never fire mid-gesture: a move " +
            "waits on the game to catch up and a click waits on a confirmation, and a cursor taken " +
            "away between the two leaves a modifier held and a click unsent"),

        new("7  A holder that goes quiet loses it", [Take("flipper"), Wait(6)], null,
            "the reason there is an expiry at all. A plugin that throws, is disabled, or is " +
            "reloaded by the updater never reaches its release - and without a timeout that is the " +
            "cursor gone for the session, with no error anywhere and every other plugin quietly " +
            "doing nothing. That is the worst kind of fault to diagnose"),

        new("8  An expired holder cannot act on the strength of having held it",
            [Take("flipper"), Wait(6), Held("flipper")], null,
            "Held has to answer the same question Holder does, or a machine mid-gesture keeps " +
            "moving a cursor the lease says it does not have"),

        new("9  The stop key takes it off whoever has it",
            [Take("flipper"), Break()], null,
            "for a person who wants their mouse back now, and for the plugin unloading. Neither " +
            "knows or should have to know who is holding it"),

        new("10 A nameless caller gets nothing", [new("an unnamed plugin takes it",
            l => l.Take("  ") ? "got it" : "refused")], null,
            "blank is not a name, and if it were then every caller that forgot to set one would " +
            "share a single claim and silently take the cursor off each other"),
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- who is allowed to move the mouse --------------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            _idle = TimeSpan.Zero;

            var lease = new Lease { Now = () => _idle };
            var told = new List<string>();

            foreach (var step in c.Steps)
                told.Add(step.Do(lease));

            var ok = lease.Holder == c.WantHolder;

            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        doing {string.Join(", then ", Array.ConvertAll(c.Steps, s => s.What))}");
            Console.WriteLine($"        want  held by {c.WantHolder ?? "nobody"}");
            Console.WriteLine($"        got   held by {lease.Holder ?? "nobody"}  ({string.Join("; ", told)})");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine($"{All.Length - failed}/{All.Length} match");
        return failed;
    }
}

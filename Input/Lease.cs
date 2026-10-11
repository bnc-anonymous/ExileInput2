using System;
using System.Diagnostics;

namespace ExileInput2;

/// <summary>
/// Who is allowed to move the mouse right now.
///
/// There is one cursor and there may be several plugins. Two of them driving it at once does not
/// degrade gracefully - it produces a click at the average of two intentions, which lands on
/// neither and is indistinguishable from a misread rect.
///
/// So a caller takes the cursor before using it and gives it back afterwards, and every call names
/// the holder. A call from anyone else is refused rather than queued: the refused caller keeps its
/// own state and tries again next tick, which is what all of them do already when a click does not
/// go through, so nothing new has to be written to handle it.
/// </summary>
public sealed class Lease
{
    /// <summary>A monotonic reading shared by every lease, and never a wall clock.</summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private string _holder;

    /// <summary>When the holder last did anything, by the reading below.</summary>
    private TimeSpan _acted;

    /// <summary>
    /// What time it is, for driving the clock by hand.
    ///
    /// The expiry is the one rule here that cannot be checked by using the class normally - the
    /// alternative to injecting time is a test that sleeps for five seconds, which nobody runs
    /// twice.
    ///
    /// A reading of the current time and not a measure of how long the holder has been idle, which
    /// is what the first version of this handed over. That seam could not express the rule it
    /// existed to check: refreshing the lease has to reset the idle time, and a test driving the
    /// idle time directly has already decided the answer. It reported a working lease as broken
    /// twice, which is the right way round for a bad seam to fail but still cost the time.
    /// </summary>
    internal Func<TimeSpan> Now { get; set; }

    private TimeSpan Reading => Now?.Invoke() ?? Clock.Elapsed;

    private TimeSpan Since => Reading - _acted;

    /// <summary>
    /// How long a holder may go without calling anything before the lease is taken off it.
    ///
    /// The reason there is an expiry at all: a plugin that throws mid-gesture, gets disabled, or is
    /// reloaded by the updater never reaches its release. Without a timeout that is the cursor gone
    /// for the session, and the failure looks like every other plugin quietly doing nothing -
    /// which is the worst kind of fault to diagnose, because nothing anywhere reports an error.
    ///
    /// Generous, because it must never expire mid-gesture. A move waits on the game to catch up and
    /// a click waits on a confirmation; five seconds is far longer than either and far shorter than
    /// a session.
    /// </summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Who holds it, or null when it is free.</summary>
    public string Holder => Stale ? null : _holder;

    /// <summary>The priority the holder took it with: 0 for an ordinary Take. See ExileInput2.TakeWithPriority.</summary>
    public int HolderPriority { get; private set; }

    private bool Stale => _holder != null && Since > Expiry;

    /// <summary>
    /// Claims the cursor, or says no.
    ///
    /// Taking it twice is not an error - a machine that asks on every tick rather than remembering
    /// whether it already holds it is doing something reasonable, and refusing that would only
    /// teach callers to keep a flag this class already keeps for them.
    /// </summary>
    public bool Take(string who) => Take(who, 0);

    /// <summary>Claims the cursor at a priority, recorded for a later TakeWithPriority to compare against.</summary>
    public bool Take(string who, int priority)
    {
        if (string.IsNullOrWhiteSpace(who))
            return false;

        if (Holder != null && Holder != who)
            return false;

        _holder = who;
        _acted = Reading;
        HolderPriority = priority;
        return true;
    }

    /// <summary>Whether this caller may act, refreshing the lease if so.</summary>
    public bool Held(string who)
    {
        if (string.IsNullOrWhiteSpace(who) || Holder != who)
            return false;

        _acted = Reading;
        return true;
    }

    /// <summary>
    /// Gives the cursor back.
    ///
    /// Only the holder may, so a plugin tidying up on shutdown cannot release a lease another one
    /// took in the meantime.
    /// </summary>
    public void Release(string who)
    {
        if (Holder != who)
            return;

        _holder = null;
        _acted = default;
    }

    /// <summary>Takes it off whoever has it, for a stop key or the plugin unloading.</summary>
    public void Break()
    {
        _holder = null;
        _acted = default;
    }
}

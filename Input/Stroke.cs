using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace ExileInput2;

/// <summary>The keyboard and mouse, as a thing a test can stand in for.</summary>
internal interface IButtons
{
    bool? Readable { get; }
    void Answered();
    void NeverAnswers();
    bool IsDown(Keys key);
    bool AllDown(IReadOnlyList<Keys> keys);
    void Hold(IReadOnlyList<Keys> keys);
    void Press(bool right, int holdMs);
    bool PressHeld { get; }
    void Release();
}

/// <summary>The real ones.</summary>
internal sealed class RealButtons : IButtons
{
    public bool? Readable => Buttons.Readable;
    public void Answered() => Buttons.Answered();
    public void NeverAnswers() => Buttons.NeverAnswers();
    public bool IsDown(Keys key) => Buttons.IsDown(key);
    public bool AllDown(IReadOnlyList<Keys> keys) => Buttons.AllDown(keys);
    public void Hold(IReadOnlyList<Keys> keys) => Buttons.Hold(keys);
    public void Press(bool right, int holdMs) => Buttons.Press(right, holdMs);
    public bool PressHeld => Buttons.PressHeld;
    public void Release() => Buttons.Release();
}

/// <summary>
/// One modified click, spread over as many ticks as it needs and blocking on none of them.
///
/// This is the part that used to be three sleeps in a row. Press the modifier, poll up to 200ms for
/// it to read back, wait 60ms, click, wait 60ms, release - all on the calling thread, which in this
/// HUD is the single thread that draws every plugin. A third of a second of frozen overlay for one
/// click, and the overlay belongs to every other plugin too.
///
/// The waiting is the same waiting. What changed is that each step returns and is picked up on the
/// next tick, which is how the rest of the plugin has always worked - the machines calling this are
/// state machines already, and every one of them handles a click that did not go out by keeping it
/// pending and asking again.
///
/// So the caller sees "not yet" as a false, which is the answer it already knew how to take.
/// </summary>
internal sealed class Stroke
{
    private enum Phase
    {
        /// <summary>Nothing held, nothing owed.</summary>
        Idle,

        /// <summary>The modifier is down and we are waiting to believe it.</summary>
        Settling,

        /// <summary>The click has gone; the modifier stays down until the button is up and a moment after.</summary>
        Letting,
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly IButtons _keys;

    private Phase _phase;
    private bool _right;
    private TimeSpan _at;

    /// <summary>How long the modifier is down before the click, drawn when it goes down. See HumanTiming.</summary>
    private int _leadMs;

    /// <summary>How long the modifier stays down after the button comes up, drawn at the click. See HumanTiming.</summary>
    private int _releaseMs;

    /// <summary>When the button was seen up after the click; null until it is.</summary>
    private TimeSpan? _buttonUpAt;

    /// <summary>One draw of a click's timings, in milliseconds. HumanTiming's by default; fixed by the tests.</summary>
    internal Func<int> TimingMs { get; set; } = HumanTiming.Ms;

    public Stroke(IButtons keys = null) => _keys = keys ?? new RealButtons();

    /// <summary>What time it is. Injected by the tests; see the lease for why it is a reading.</summary>
    internal Func<TimeSpan> Now { get; set; }

    private TimeSpan Reading => Now?.Invoke() ?? Clock.Elapsed;

    private double SinceMs => (Reading - _at).TotalMilliseconds;

    /// <summary>How long to keep asking whether the modifier is down before giving up on it.</summary>
    public int ConfirmMs { get; set; } = 200;

    /// <summary>Told when the keys come up, so the caller's own bookkeeping can follow.</summary>
    public Action OnLetGo { get; set; }

    /// <summary>Whether a gesture is part-way through.</summary>
    public bool Busy => _phase != Phase.Idle;

    /// <summary>
    /// Asks for a click, and says whether the button has gone down.
    ///
    /// False means "not this tick" far more often than it means "no". Both are answered the same
    /// way by every caller - keep the click pending and ask again - so the two do not need telling
    /// apart here, and a caller that never gets its click runs out of its own timeout and says so
    /// with far more context than this could.
    /// </summary>
    public bool Push(IReadOnlyList<Keys> modifiers, bool right)
    {
        // Nothing held means nothing to confirm and nothing to let go of. This is most clicks, and
        // it never waited even under the old arrangement.
        // A click whose button is still down is not over: another press now would be a down on a down.
        if (_keys.PressHeld)
            return false;

        if (!Modifier.Any(modifiers))
        {
            _keys.Press(right, TimingMs());
            return true;
        }

        // The last gesture has not let go yet. Overlapping two of them on one set of keys is how a
        // release meant for the first arrives in the middle of the second.
        if (_phase == Phase.Letting)
            return false;

        if (_phase == Phase.Idle)
        {
            _keys.Hold(modifiers);

            _phase = Phase.Settling;
            _right = right;
            _at = Reading;
            _leadMs = TimingMs();
        }

        // Every requested modifier, not just control. This used to strike immediately whenever
        // control was not among them, on the grounds that shift had no readback - it does, and so
        // does alt, and an unverified modifier is one that can silently fail to take. Once down, the
        // click still waits out the lead: a person's ctrl goes down 50 to 100 ms before the click.
        if (_keys.AllDown(modifiers))
        {
            _keys.Answered();
            return SinceMs >= _leadMs && Strike();
        }

        // Where the state has never once been readable there is nothing to wait for either - but
        // there is then no evidence at all, and the lead is what is left.
        if (_keys.Readable == false)
            return SinceMs >= _leadMs && Strike();

        if (SinceMs < ConfirmMs)
            return false;

        if (_keys.Readable == true)
        {
            // It has answered before, so this is a real no rather than a check that does not work
            // here. Nothing is clicked, and the caller keeps the click for another go.
            Abandon();
            return false;
        }

        // Never once answered, which means the state cannot be read from here rather than that the
        // modifiers are not down. Refusing on that reading is how a check meant to prevent one bad
        // click prevented every good one instead, four times in a row.
        _keys.NeverAnswers();
        return Strike();
    }

    /// <summary>
    /// Clicks on a modifier somebody else is already holding, and takes over letting it go.
    ///
    /// For the approach that pressed its modifier in flight and has had the whole journey to
    /// confirm it. Nothing to press and nothing to wait for, but the same hold afterwards - the
    /// click and the key coming up in the same breath is the half of the old gesture that was
    /// actually load-bearing, and the fast path never had it.
    /// </summary>
    public bool StraightIn(bool right)
    {
        if (_phase == Phase.Letting || _keys.PressHeld)
            return false;

        _right = right;
        return Strike();
    }

    private bool Strike()
    {
        _keys.Press(_right, TimingMs());

        _phase = Phase.Letting;
        _at = Reading;
        _releaseMs = TimingMs();
        _buttonUpAt = null;

        return true;
    }

    /// <summary>
    /// Advances the gesture: once the click's button is up, and its release time after that, the modifier comes up.
    /// Called every tick by whoever owns this.
    /// </summary>
    public void Step()
    {
        if (_phase != Phase.Letting || _keys.PressHeld)
            return;

        _buttonUpAt ??= Reading;

        if ((Reading - _buttonUpAt.Value).TotalMilliseconds >= _releaseMs)
            LetGo();
    }

    /// <summary>Drops the gesture and brings the keys up, for a caller giving up or going away.</summary>
    public void Abandon()
    {
        if (_phase != Phase.Idle)
            LetGo();
    }

    private void LetGo()
    {
        _keys.Release();
        _phase = Phase.Idle;

        OnLetGo?.Invoke();
    }
}

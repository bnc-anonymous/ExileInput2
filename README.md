# ExileInput2

One owner for the mouse and keyboard, shared by every plugin that wants them. For Path of Exile 2
and ExileCore2.

Install it with **PluginUpdater** — paste this repository's URL into its Add tab. The csproj sits at
the repo root, which is where the HUD looks once the clone lands in `Plugins/Source`.

## Integrating

There is nothing to reference. Copy **`Client/ExileInput2Client.cs`** into your own source, change
the namespace, and give it a name unique to your plugin:

```csharp
_input = new ExileInput2Client(GameController, "MyPlugin");

if (_input.WasStopped())         // the player took the mouse back; stop what you were doing
{
    _input.Release();            // and say so, or Take goes on refusing
    return;
}

if (!_input.Take())              // somebody else has the cursor; try again next tick
    return;

_input.MoveTo(button.GetClientRectCache);
// ... on a later tick, once the game has caught up ...
if (_input.Arrived())
    _input.Click(ctrl: true, right: true);
// ... when your machine finishes or gives up ...
_input.Release();
```

## Modifiers

`ctrl` and `shift` have bool parameters because they are what most callers want. Anything else -
alt, or a combination the two bools cannot say - goes as a set of keys:

```csharp
// Shift keeps a currency on the cursor between applications; alt on top of it changes
// which currency is applied. Two bools could not express that at all.
_input.Click([Keys.LShiftKey, Keys.LMenu]);

_input.MoveTo(item.GetClientRectCache, [Keys.LShiftKey, Keys.LMenu]);
```

Every requested modifier is confirmed down before the button is pressed, not just control. A
modifier that silently failed to take does not produce a visible error — it produces a different
action on an expensive item.

The bool forms are not deprecated and are not going anywhere. They call the same code.

## Taking the mouse back

**Moving the mouse is the stop.** The cursor turning up somewhere this plugin never sent it means a
hand moved it, and everything halts: the journey, the gesture, any key being held. The plugin that
was driving loses the claim and `Take` refuses it until it calls `Release`, so a hand coming to rest
does not hand the cursor straight back.

There is no panic key and there is not meant to be one. Reaching for the mouse is what anyone
already does when something on screen is doing something they did not ask for; a key they have to
remember is a key they will not reach for in the moment they need it.

The same stop covers the game losing focus, the game taking typed input, and a cursor that will not
go where it is sent. `Stopped()` says which — check it on every tick of a sequence. A caller that
ignores it stops working rather than fighting the player for the cursor, which is the safe way round
for it to be wrong, but it stops silently.

All of this is a guess about lag away from being useless, so it is under test: `Tests/StopCases.cs`
is the table, and case 2 is the one that matters. The game reports its cursor several frames behind
where it was put, so mid-journey the reported position and the last sent position are never equal -
and a check written as "is the cursor where I last put it" stops on the first step of every journey
it ever makes and blames the player for it.

## Why nothing is shared but a file

Everything crossing the bridge is a type both sides already have — `RectangleF`, `Vector2`, `Keys`,
`bool`, `float`. So there is no DLL in the HUD root, no version to keep in step, and no cast to fail
at load. An interface would have meant all three.

If this plugin is not installed, `Available` is false and every call returns false, so a consumer can
fall back to driving `ExileCore2.Input` itself. Nothing here is load-bearing for anyone.

## One at a time

There is one cursor and there may be several plugins. Two driving it at once does not degrade
gracefully — it produces a click at the average of two intentions, which lands on neither and looks
exactly like a misread rect.

So every call names its caller, and that name is the claim. Calls from anyone else are refused
rather than queued: callers already cope with a click that did not go through, and none of them cope
with one that happened later than they thought.

**The claim expires.** A plugin that throws mid-gesture, gets disabled, or is reloaded never reaches
its release — and without an expiry that is the cursor gone for the session, with no error anywhere,
just every other plugin quietly doing nothing. Five seconds by default: far longer than a move or a
click, far shorter than a session.

## What it knows that a fresh implementation would not

- **A ctrl+right click that loses its modifier does not fail quietly.** On a Chaos Orb it *uses* the
  orb, putting it on the cursor where it blocks every click after it. So the key state is read before
  pressing, with a timed hold as the fallback where the state cannot be read at all.
- **`SetCursorPos` takes screen coordinates; elements report client ones.** Getting that wrong is
  invisible in fullscreen and wrong by the window offset everywhere else. The translation happens in
  one place here.
- **A click sent in the same breath as the move lands where the cursor used to be.** The game reports
  its position a frame or two late, so arrival is confirmed before pressing.
- **A key held and forgotten costs more than a failed action.** Control left down turns the player's
  next click into a ctrl+click, which in a stash moves items. Every key is tracked and released
  together.

- **A cursor that stops following looks exactly like a cursor somebody is holding.** From in here
  they are the same situation, and in both of them the right thing to do is stop rather than carry
  on clicking.

## Tests

`Tests/` is a console runner rather than a test framework, because the useful output is the whole
table side by side and a framework reports the first failure and hides the rest.

```bash
export exileCore2Package="<your ExileCore2 folder>"
dotnet run --project Tests/Tests.csproj
```

What it covers is the reasoning rather than the input: how far a landing may be off, who is allowed
to move the mouse, what shape a journey has, and whether a click may go down on a modifier already
held. Every bug this plugin has had was one of those - a stale flag, a guard that could not fire, a
tolerance measured against the wrong thing - and not one was a mistake about `SetCursorPos`.

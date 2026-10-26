# Design notes — why WindowShuttle decided what it decided

The README states conclusions; this file keeps the measurements and reasoning behind them.
Nothing here is required to use the app — it's for the reader who asks "why that shape?".

## Why only two hotkeys ship bound

A global hotkey takes that combination away from *every* application on the desktop, not just
WindowShuttle. `Ctrl+Alt+<letter>` in particular collides with things like Word's insert-©
and IntelliJ's Extract Constant. So WindowShuttle claims as little as it can get away with —
`Swap` and `Undo`, because being unable to undo a bad move is the worst first impression a
window-moving tool can make — and leaves the rest for you to bind yourself.

## The bare right button: verified working, still not the default

That is the usual shape for gesture software, and it does work here — measured, not assumed:
a purpose-built Win32 window logging the messages it actually receives (Win11's menus are
XAML, so counting popup windows proves nothing) produced the full
`WM_RBUTTONDOWN -> WM_RBUTTONUP -> WM_CONTEXTMENU` both with and without WindowShuttle
running, landing pixel-for-pixel on the press point.

It isn't the default because the stakes are lopsided: what you save is one modifier key,
what you bet is the system-wide context menu. If the replay fails on some machine — another
low-level hook eats it, some program only honours real input — the user loses right-click
menus everywhere and will not think to blame a window-moving utility. The capability is
still there for anyone who wants it: record *Send by direction*'s slot as a bare right
button (it is allowed for this action only — the six click-type actions may not take one).

## The Windows key can't be recorded

Windows reclaims that key before the click reaches any application; measured on the author's
machine, at the moment of the click it reads as not held. That is why the recording prompt
names only `Ctrl` / `Alt` / `Shift`: a press with no readable modifier is dropped and no
binding is stored. The one exception is *Send by direction*, whose slot takes a bare button by
design — so the rule is really "a modifier-free press is dropped on the six click-type slots."
The app does not pop a card explaining this — what needed saying is in the prompt, before you
act.

## Mouse gestures have no conflict signal (unlike hotkeys)

`RegisterHotKey` can't bind mouse buttons, so gestures run through a global low-level mouse
hook — a mechanically stricter case than a hotkey. A hotkey's registration succeeds or fails
on the spot, which is why a taken combination shows up red; a mouse hook has no such signal.
Windows never tells WindowShuttle "this gesture is already claimed," so a gesture can
silently collide with another program with neither side noticing. The app says this **once**,
in the settings strip at the bottom of the window, right after the mouse glyph that marks
the gesture slot on every action card — repeating it next to each of the seven slots would
present a feature the app actively discourages as an equal citizen of the shortcut column.
One confirmed collision: `Alt` + right-click is AltSnap's default window-resize gesture.

## The hook thread and its 300 ms budget

The hook itself runs on its own dedicated thread and does one thing — decide whether it
recognises the combination — before returning; the actual window moving happens on another
thread. Windows gives each hook on that chain only 300 ms, and going over it stalls clicks
system-wide, so nothing blocking is ever put on that thread.

## When the "gestures are dead" card appears — and why not more often

The condition is "you are using gestures", not "the foreground changed". The version that
only watched the foreground tested badly: doing nothing but clicking into a password manager
produced an unsolicited offer to restart as administrator, for a window you never meant to
move. The information is useful at exactly one moment — you were just flicking windows and
have now walked into the dead zone. So someone who never uses gestures never sees the card,
and someone who used one in the morning is not reminded of it in the afternoon.

## Why the modifier race can't be widened

By the time the drag reveals what you meant, the button-down has already been delivered —
there is no tolerance to widen. And keyboards are inherently slower to report than mice:
matrix scan plus debounce, and a typical 125 Hz USB polling rate (one report per 8 ms)
against a mouse's 1000 Hz — so even a genuinely simultaneous press usually reaches the
system mouse-first. Confirmed in one real failure here: the button-down arrived with `Ctrl`
still not registered, and that press dragged 721 px, so it was unmistakably a gesture.

## Pointer for the mouse, focus for the keyboard — one rule, two anchors

These are not two rules but one: the referent should be whatever you are pointing at right
now. With your hand on the mouse the pointer *is* the pointing device, and using focus
instead would mean pointing at a browser and watching your editor fly away. With your hand
on the keyboard the pointer is parked wherever you last left it — possibly on another
screen, over another window — and reading it as intent reads an unrelated leftover.
Windows' own `Win+Shift+←/→` and PowerToys FancyZones both act on focus for the keyboard.

## The map follows every move, and a side failure never changes the move

Every window-moving action funnels through `CommandRouter.Executed`. (Identify screens is the
one exception: it moves nothing and calls the overlay directly, so it never reaches the
router.) The main window subscribes there and refreshes the map only when `Commit.Moved > 0`:
a no-op changed nothing, and a display change already refreshes the monitor list. The
subscription runs before the tray's notification subscriber and catches its own failures; the
router also catches a throwing subscriber. A failed notification or map redraw is logged to
`crash.log`, never turned into a failed move. The rescue timer no longer refreshes a second
time.

## Recording ends when the binding lands; the keyboard keeps its focus

The mouse chord recorder clears focus after a capture because the mouse is the input device
and the global hook must be disarmed. The keyboard recorder deliberately differs: clearing
focus would drop a keyboard user back to the top of the window. A successful capture (or a
Delete/Backspace clear) leaves focus on the slot but marks it as no longer listening; the next
key goes to the global hotkey or the window, not a second capture. Clicking the slot again
re-arms it. The slot shows a quiet focus ring, not the live recording glow.

## One card for every unmovable focused window

`FocusNotMovable` is not just "you have not selected a window": it also covers WindowShuttle's
own window, the desktop/shell, a skipped fullscreen app, and a hung app. The card therefore
says "Can't move that window — click another one" instead of "select a window first";
the latter is false for a fullscreen or hung window the user already selected. The same
neutral line works for every cause; a cause-specific message would require the plan to carry
which cause it hit.

## Full titles for truncated chips

A chip shows at most forty characters, and several browser windows with the same prefix are
otherwise indistinguishable. Hovering shows the full title plus the Enter hint, wrapped to
420 DIP; the chip's automation name is also set to the full title (the tooltip is the
sighted path).

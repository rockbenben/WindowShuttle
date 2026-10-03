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
still there for anyone who wants it: record that slot as a bare right button (it is allowed
for this action only).

## The Windows key can't be recorded

Windows reclaims that key before the click reaches any application; measured on the author's
machine, at the moment of the click it reads as not held. That is why the recording prompt
names only `Ctrl` / `Alt` / `Shift`: a press with no readable modifier is dropped and no
binding is stored. The app does not pop a card explaining this — what needed saying is in
the prompt, before you act.

## Mouse gestures have no conflict signal (unlike hotkeys)

`RegisterHotKey` can't bind mouse buttons, so gestures run through a global low-level mouse
hook — a mechanically stricter case than a hotkey. A hotkey's registration succeeds or fails
on the spot, which is why a taken combination shows up red; a mouse hook has no such signal.
Windows never tells WindowShuttle "this gesture is already claimed," so a gesture can
silently collide with another program with neither side noticing. The app says this next to
the gesture slots, not just here. One confirmed collision: `Alt` + right-click is AltSnap's
default window-resize gesture.

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

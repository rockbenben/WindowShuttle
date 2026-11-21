# Design notes — why WindowShuttle decided what it decided

The README states conclusions; this file keeps the measurements and reasoning behind them.
Nothing here is required to use the app — it's for the reader who asks "why that shape?".

## Extraction completeness and archive verification

A shutdown or disk error can leave an executable present but truncated. Extraction invalidates
the old marker first and writes .complete-sha256 only after the root executable is complete.
The marker detects interruption and local damage. RunAsync downloads and verifies the archive
against the release checksum on every check, so a local marker cannot establish package origin.

## Verify the package users receive

`tools/package-release.ps1` is shared by local packaging and CI. It checks both published
executables are x64 single-file builds, unpacks each ZIP into a clean directory, compares the
bytes, and runs `--smoke` with isolated application-data and temporary directories. The same
explicit asset list feeds SHA-256 generation and release upload, so an unrelated ZIP cannot
enter the checksums and a missing upload fails the job. The existing raw framework-dependent
download remains available; builds remain unsigned. These local checks do not prove hosted
publication, provenance or `releases/latest`; those still require the tagged workflow.

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


## Execution results and recovery

The committer records changed window handles separately from failure counts: a maximized
window can move in the first placement step and fail the second. The router stages pre-move
positions before touching windows, publishes only changed entries, and keeps failed undo
targets. Zero changed windows leave the previous snapshot intact. Atomic replacement avoids
truncating the active snapshot; a pending snapshot survives final-save failure for recovery.
Single-instance initialization waits for either a command sink or actual mutex ownership.
An IPC timeout reports uncertainty without replaying the operation.

## Portable elevated startup and isolated verification

Portable single-file builds can register elevated logon tasks from user-writable locations.
The application no longer enforces protected ownership and ACLs as a prerequisite. Protected
deployment remains recommended: replacing a writable executable also replaces the code the
task runs with elevation. The application does not change directory permissions automatically.
Registration uses Environment.ProcessPath, the requesting user's account and interactive highest
privileges. Readback verifies the requesting user, executable, --tray argument, enabled state and privilege mode
before removing the old Run value. Legacy task paths with surrounding quotes are normalized.
UAC refusal retains the old startup mode; registration, removal and readback failures report
their actual stage and details in the notification and crash.log rather than a path-policy error.
Only `GetTask` returning `0x80070002` confirms task absence. .NET COM interop can surface
that HRESULT as `FileNotFoundException`. Service, folder and task-definition failures still
leave the startup state unknown.
Registry tests use temporary HKCU keys; the UIA harness uses SettingsStore.HomeOverride.
Release tags enter scripts through environment variables. The shared release policy rejects
published tags and non-increasing versions before publishing; publication jobs are serialized.
The preflight reads every release page, so an older-created release with the highest version
cannot be hidden behind recent drafts or prereleases. An offline regression executes the
workflow's actual preflight against a simulated paginated response.
Startup transition tests drive the real settings method with a simulated UAC result and a
temporary registry key. Acceptance, refusal and task-operation failure cover both directions;
Registration/readback regressions use substituted task operations and a temporary registry key.
A read-only Task Scheduler query covers the real missing-task response with a unique name.
Actual Task Scheduler writes, consent and a real logon remain manual checks.

## Desktop topology and update checks

The map uses one physical pixel coordinate system so vertical stacks and grids retain their
relative placement. The footer and action viewport are remeasured together when its height
changes. Each update check downloads a fresh archive for comparison with the release checksum;
the local completion marker only detects interrupted extraction. A single process serializes
checks, bounds the body transfer with cancellation, and performs hashing and extraction off
the UI thread.


## Undo identifies a window instance across tool restarts

An HWND alone can point at a different window later. PID and process creation FILETIME reject
a different process lifetime, but an application can recreate a window without restarting.
Before a user move, WindowIdentityProbe records those process fields and sets a random scalar
property named WindowShuttle.UndoIdentity.v1 on the window if it has none. Repeated moves reuse
the token. The property belongs to that native window and survives WindowShuttle exiting; a
destroyed window's replacement starts without it. The token is scalar data, so it owns no
allocated memory, kernel handle, startup registration, or file to clean up.

The optional Identity field keeps older JSON snapshots readable. Missing or incomplete
identity is never reconstructed from an HWND's current occupant. Those entries, including
pending recovery entries, are retained and skipped. A complete identity that cannot currently
be queried is also deferred. A confirmed replacement is excluded when a successful partial
undo publishes its next snapshot. If nothing can safely move, existing files stay untouched.

Every plan produced by UndoStore.ToPlan requires identity verification. Normal undo windows
use individual SetWindowPos calls instead of a deferred batch; placement checks run before
each step, and correction checks again before writing. The reverse snapshot carries the same
identity, preserving undo-of-undo. Capture failure does not turn a successful user move into
an error; its unverifiable entry cannot later authorize an undo.

Windows offers separate identity and positioning calls, so a target can still be destroyed
between the last check and the native write. These checks narrow that race; they do not make
the operation atomic. The marker is not authentication against a process deliberately
editing another window's properties or the snapshot. UIPI or process query restrictions can
make identity unavailable and therefore reduce undo availability. Downgrading to an older
binary also restores that binary's older HWND-only behavior.

## Dragging a window on the map

The chip captures the initial press so crossing another title cannot change which window moves. Dragging begins at the Windows drag threshold. The destination changes fill during the drag, leaving the border available for its existing monitor status.

The confirmation dialog reserves a row for its buttons. When the monitor height cap applies, the message scrolls independently so the last line and both choices remain reachable.

## Main window layout

The Updates menu groups a one-time check with the persistent startup preference. Opening the menu works offline. The map header carries the drag instruction so short windows can give that row back to the map and action list. Card spacing leaves the shortcut targets at their original size. The language selector aligns with the first settings row as the switches wrap.

At the minimum window height, the settings controls collapse behind a toggle. The mouse gesture caution stays visible beside it. A manual toggle takes precedence over automatic resizing for the lifetime of the window. With settings collapsed, the map can shrink to its crowded-layout floor to leave two complete action rows visible.

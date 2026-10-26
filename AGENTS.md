# AGENTS.md

This is the repository-level working agreement for AI coding agents. Read it before changing code. If your tool has its own instruction file, treat it as a pointer to this file, not as a second source of rules.

WindowShuttle is a **Windows-only WPF / .NET 10** tray application that moves windows between monitors. It ships as a portable single-file executable — no installer, no account, no web stack, no plugin system. The user-visible docs are `README.md` and `README.zh.md`; the developer docs are `docs/design-notes.md` (why it decided what it decided) and `docs/manual-checks.md` (the checks no machine can automate).

## 1. Read before changing

- **`docs/design-notes.md`** — the measurements and reasoning behind the current behaviour. A surprising constant in the code usually has its justification here; if the file does not mention it, that is a finding, not a licence to invent one.
- **`docs/manual-checks.md`** — what a human still has to do by hand, and **which parts of that file are already covered by an automated test**. Read the last section before adding a new manual item.
- **`.github/workflows/ci.yml`** — the real build, test and release gates. A green local build is not a release guarantee.
- **`git status` and `git log`** — the working tree may contain someone else's unfinished work. Never treat it as a clean release tree.
- **The WPF-specific skill**, if available (`wpf-desktop`) — especially its layout, DPI and activation references, before touching anything that renders.

## 2. Build and verify

```bash
dotnet build WindowShuttle.sln -c Debug
dotnet test  tests/WindowShuttle.Core.Tests
```

The application has two in-process self-checks, both wired into `OnStartup` **before** the single-instance mutex, so they never disturb a resident instance and never touch your `settings.json`:

```bash
WindowShuttle.exe --smoke              # construct and lay out every window, then exit
WindowShuttle.exe --shots <directory>  # render every window to PNG: 2 themes x 5 languages x 7 screen sizes
```

- `--smoke` reports through a marker file at `%TEMP%\windowshuttle-smoke.txt` (`OK`, or the exception). **Judge it by the marker, not by the exit code** — a WPF app started through `Start-Process` can hand back a null `ExitCode`.
- `--shots` is a **local** tool. It is not in CI; the screenshot matrix is too slow for it. It is listed in `docs/manual-checks.md` as a manual step.
- `tools/UiaHarness` is a real UI Automation measurement tool, not a mock: it drives the actual windows and prints measured rects. Use it when a judgement has to be about a real screen.
- **Never run the real global mouse hook or the full-screen topmost overlay on a machine someone is using.** The hook chain is system-wide (a slow link stalls every click on the desktop), and a full-screen always-on-top window that hangs leaves a ghost window that eats clicks until `dwm.exe` is restarted. The two self-check switches never install the hook and never raise the overlay — they only construct and lay out windows — so they are safe to run anywhere.

## 3. Load-bearing invariants

These are the rules the product is built on. Changing one is a product decision, not a refactor — and every one of them is load-bearing in a way that is easy to break silently.

- **One screen numbering: position, left to right (then top to bottom within a column), 1-based.** It is what the map prints, what *Identify screens* flashes, what `--to <n>` takes and what `list` reports as `position`. It is **not** the Windows display-device number, which has no relation to where a screen sits and can be reshuffled by ports, docks and driver reinstalls. The Windows number survives as `index` / `device` and as the `system N` line on each map card.
- **Pointer for the mouse, focus for the keyboard — one rule with two anchors.** A mouse gesture acts on the window under the cursor *as of the press*; hotkeys, the tray menu and the CLI act on the focused window. Do not "unify" these.
- **Undo is exactly one step**, and the automatic off-screen rescue deliberately does **not** take that slot. The rescue is machine-initiated; letting it overwrite the snapshot would destroy the move the user was about to undo.
- **A side failure never changes the move.** Notifications and the map redraw are downstream of the result. A throwing subscriber is caught and logged to `crash.log`; it must never turn a completed move into exit code 3, nor take the resident process down.
- **The app is offline unless the user asks otherwise.** The opt-in update check is the only network path. Do not add telemetry, cloud calls or silent background traffic.
- **RTL is set per window, never through `FlowDirectionProperty.OverrideMetadata(typeof(Window), …)`.** That form collides with WPF's own `Window` static constructor and makes the process unable to create *any* window — the app then fails to start at all, including `list` and `--smoke`. See the comment in `App.OnStartup`.
- **The mouse hook runs on its own dedicated thread and has a 300 ms system budget.** Nothing blocking may ever be put on that thread. The move itself happens on the UI thread via `BeginInvoke`.
- **`RegisterHotKey` gives hotkeys a conflict signal; the mouse hook cannot.** That asymmetry is why the gesture slots have no conflict state, and why the app states the cost once in the settings strip instead of pretending the slots are safe.

## 4. Layout, DPI and geometry

- The process is **PerMonitorV2** aware (`app.manifest`). Never assume one DPI for the whole desktop.
- Geometry is **recomputed for the destination, never copied**: sizes are rescaled, maximised stays maximised, minimised restores on the new screen, and a snapped window keeps its half or its quarter. The snap slot is detected geometrically — Windows' own snap-group bookkeeping is out of reach for any third party.
- The action list has **responsive column breakpoints** (`MainWindow.TwoColumnMinWidth` / `ThreeColumnMinWidth`, measured on the action area's own width, not the window's). The thresholds are derived from the width at which the longest action name stops being trimmed into an ellipsis; changing them means re-measuring that boundary, not picking a rounder number.
- The map canvas is pinned `LeftToRight` in both the main window and the identify overlay: it draws where the screens physically are, and mirroring it would make the screen numbers disagree with the desk.
- When you measure anything on this machine, the measuring process must be **DPI-aware** (`SetThreadDpiAwarenessContext(-4)`), or the numbers come back silently divided by the system scale and you will "fix" code that was never broken. Prefer the app's own screenshots and the UIA harness.

## 5. Localization and documentation

- The neutral `Strings.resx` is the **Simplified Chinese** source. Every UI string also exists in the 17 satellite resx files. Add a new key to **all 18** files; never leave a raw key or an empty translation. `StringsCoverageTests` checks key-set completeness and unused keys.
- A user-visible change lands with its documentation **in the same change**. `README.md` / `README.zh.md` are the short version; `docs/design-notes.md` carries the reasoning; `docs/manual-checks.md` carries what still needs hands.
- **Comments explain why, not what.** Do not write drifting line numbers, test counts, release versions or "this round" notes into comments or docs. A comment that names a file, a constant, a threshold or a test must still be true — when the code moves, the comment moves with it or it gets deleted. Do not leave pointers to documents that are not in the repository.
- One XML `<summary>` per member. A second idea about the same member goes in `<remarks>`; a doc block that describes a *different* member belongs on that member.
- The two READMEs are line-for-line mirrors. Change one, change the other in the same commit.

## 6. User data and safety

- Configuration and state live under `%APPDATA%\WindowShuttle` (`settings.json`, `undo.json`, `crash.log`, and the `chord-debug.on` / `chord-debug.log` pair). Tests must not edit the user's real settings, startup entries or running instance — `SettingsStore.HomeOverride` exists for exactly that.
- The elevated autostart path is a **scheduled task named `WindowShuttle`**, and it is mutually exclusive with the plain `HKCU\…\Run` value. Ticking *As administrator* swaps one for the other; unticking *Start with Windows* must clear **both**. A task left behind points every logon at a file that no longer exists.
- Destructive actions keep their existing confirmation and honest failure reporting. Do not silently swallow an error in a user-triggered flow.
- Do not add a confirmation dialog, extra step or notification unless it removes a demonstrated confusion or risk.

## 7. CI and releases

- The version comes from the git tag. The release workflow injects it into the build; do not maintain a second version number.
- The **SDK** version has exactly one source: `global.json`. CI reads it through `setup-dotnet`'s `global-json-file` — never add a second `dotnet-version`, or local and CI drift apart. Because `global.json` carries a `rollForward`, its `version` must be a **full** version (`10.0.100`, not `10.0` / `10.0.0`): `setup-dotnet` validates it against `/^\d+\.\d+\.[1-9]\d{2,}$/` and fails the job otherwise.
- The release path is: test + `--smoke` + exit codes → publish both profiles → `SHA256SUMS.txt` → build-provenance attestation → publish with `make_latest` → assert `releases/latest` points at the new tag. The checksum file must use **LF** line endings, and every downloadable asset must be covered by it.
- `SHA256SUMS.txt` and the file it vouches for sitting on the same page is why the attestation exists — they are not alternatives.
- The publish job runs under the **`release` environment**, which GitHub creates on the first tagged run. It carries **no protection rules**: a pushed tag publishes immediately and **nothing waits for a human**. To add that gate, configure required reviewers on that environment — the workflow does not need to change. Check the environment's actual settings before claiming the gate is active.
- The exe is **not code-signed**; the README and the release body both say so (SmartScreen: *More info → Run anyway*). This is a recorded deviation, not an oversight — see `.desktop-policy.json`.
- Do not push, create a tag, publish a release or edit published assets without explicit maintainer approval. A local commit is not a release. For a bad published release, forward-fix with a new version; do not overwrite or delete published assets.
- A green workflow is not proof that users received the new build. Verify the latest release and its assets separately.

## 8. Working with the tree

- Keep the change minimal and match the surrounding style. The code carries a lot of "why" comments; add to that voice rather than replacing it with a summary.
- Separate commits by feature domain. Do not stage unrelated dirty files, generated screenshots, logs, local settings or the user's configuration.
- Do not commit build outputs, screenshots, temporary configs, or anything under `.qoder-credits/`.
- The registry (`%APPDATA%\WindowShuttle`) and the scheduled task are the app's only side effects outside the process — anything you add there needs a matching way to undo it.

## 9. Definition of done

Before reporting a change as complete:

1. Build and the relevant tests pass, with no new warnings.
2. `--smoke` passes. `--shots` passes when layout, copy or theming changed.
3. New or changed strings exist in all 18 resx files, and `StringsCoverageTests` passes.
4. Every comment you touched still describes the code it sits in — including file names, constants and thresholds.
5. The docs that the change contradicts have been updated in the same commit, and the two READMEs still mirror each other.
6. `git diff --check` passes.
7. The report states what was verified, what was not, and whether anything was pushed or released. Never claim a visual, permission-related or release check that was not actually run.

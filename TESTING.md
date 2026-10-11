# Testing record (Joshua8-AI fork)

How this fork is verified, and the last recorded results. Run everything from the
repo root on Windows with Node 18+ and the .NET 10 SDK.

## Offline (no Civil 3D needed)

| check | command | last result (2026-09-11, `civil3d-2027-support`) |
|---|---|---|
| Node unit tests | `npm test` | 425 passed / 33 files |
| Generated tool reference current | `npm run docs:check` | current (206 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` | 0 warnings, 0 errors |
| Deploy scripts parse | `[Parser]::ParseFile` on `scripts\*.ps1` | clean |

Baseline before the 2026-09-11 improvement pass: 424 tests; `docs:check` and
`version:check` **failed** on every Windows clone — a line-ending bug in the
checkers, not real drift (fixed in `fix-check-eol`, also in upstream PR #7).

## Live (Civil 3D 2027 open)

| scenario | how | last result |
|---|---|---|
| Startup + health | `npm run test:live-plugin` | connected, plugin 1.2.1.0 |
| Zero documents: fail fast | close all drawings, `npm run test:live-plugin` (has a `noDrawing` step) | `CIVIL3D.NO_DRAWING` in 5 ms |
| Zero documents: gate released on client disconnect | force a 120 s timeout, then `civil3d_health` | `operationInProgress:false`, queue 0 |
| Zero documents: open a drawing | `civil3d_request_approval` for `civil3d_drawing new` (fingerprint `no-active-drawing`), then `new` with the token | `Drawing1.dwg` opened immediately |
| Document open: open another | same, with a drawing active (`new` always uses the `Application.Idle` hop now) | `Drawing2.dwg` opened immediately |
| Live verifier watchdog | run `verify-live-plugin.mjs` against a fake plugin that never answers `getDrawingInfo` | fails in 5 s (aborts the request), not 120 s |
| Gated write round-trip | `civil3d_point create` → `list` → `delete`, each via `civil3d_request_approval` | created/listed/deleted; reused token rejected |

Host-hop recovery (`civil3d-2027` @ `87d6bf1`, installed 2026-09-25, Jorge's machine, Civil 3D 2027 on a blank drawing):

| scenario | how | result |
|---|---|---|
| Normal reads and gated write | `civil3d_drawing info`, `civil3d_surface list`, point create → list → delete | pass |
| LISP prompt at the Command line | COM `SendCommand` of `(getstring "...")`, then `civil3d_drawing info` | call answered after ~5 s through the Idle hop; the prompt was cancelled (`getstring` reports `CMDACTIVE` 0, so the watcher took the fallback; logged, kept until restart) |
| Stuck LISP reader (the original incident) | COM `SendCommand` of an unterminated string, command line at `("_>`, then `civil3d_drawing info` | `CIVIL3D.HOST_BUSY` after 15.0 s (was a 120 s timeout) |
| Recovery once the prompt clears | Civil 3D's automatic save cleared the `("_>` prompt; then drawing info, point list, point create | pass without a restart (previously every call failed until restart) |
| User in a command | 3DORBIT active, then a gated point delete | `CIVIL3D.HOST_BUSY`; the user's command was not interrupted |
| Modal dialog open | "Drawing Recovery" notice open at startup, then drawing info / surface list | **120 s timeouts**: no Idle ticks inside a modal loop. Fixed by the no-Idle watchdog, re-tested below |
| Modal dialog open (no-Idle watchdog, `fix/host-busy-no-idle`, 2026-09-26) | COM `SendCommand` of `_.UNITS` (Drawing Units dialog open), then `civil3d_drawing info` | `CIVIL3D.HOST_BUSY` "a dialog is open ..." after 15.0 s |
| After the dialog closes | Cancel on Drawing Units, then drawing info and surface list | pass at once, on the normal command-context hop (no fallback switch logged) |

The Drawing Units dialog ignored `WM_CLOSE` and a posted `IDCANCEL`; a `BM_CLICK` on its Cancel button took effect only after a delay. Close test dialogs by hand if a script cannot.

Notes: after a rebuild Civil 3D asks "Security - Unsigned Executable File" for the bundle DLL; check the path and answer **Load Once**. A plugin call made through the command-context hop cancels whatever command or prompt is active (AutoCAD's `ExecuteInCommandContextAsync` behaviour).

Before the deadlock fix the zero-document scenarios hung for 120 s per call and
wedged the plugin until Civil 3D restarted (`docs/FINDINGS.md` in
`Joshua8-AI/civil3d-automation` has the original field report). The fix is
`fix-no-document-deadlock` / upstream PR #8.

## Command-context wedge without losing undo (`fix/plugin-command-route`, 2026-10-11, live)

Civil 3D 2027 on Joshua8-AI's machine, a copy of an HW drawing (TIN surface EG).

Trigger: with no prompt left open, another process runs a command, and afterwards
no `ExecuteInCommandContextAsync` callback runs, not even one queued from the main
thread, while `Application.Idle` keeps firing (373 ticks in 3 s, `CMDACTIVE` 0).
It is not deterministic. A COM `SendCommand("(princ) ")` from PowerShell did it in
3 of 3 runs on 2026-10-10 and in 0 of 3 on 2026-10-11; a command run by a COM
automation tool (civil3d-driver: `REGEN`, `UNDO 1`, an eval) did it within one or
two commands in three Civil 3D sessions and not at all in two others. None of these
brought the route back: `WM_NULL` every 250 ms for 10 s, `WM_MOUSEMOVE`, a Shift
key down/up, another COM command, or switching the active document away and back.

Found along the way: outside a command, a document lock plus a regular
`Transaction` wipes the drawing's undo history, even when it is only read and
aborted; an `OpenCloseTransaction` does not. A registered command started with COM
`SendCommand` from a background thread still runs at once in the wedged state.

Offline: `npm test` 429 passed / 34 files (new `host_route_fallback.test.ts`),
`docs:check` current, `build-2027.ps1` 0 warnings / 0 errors.

Live on this branch, after a civil3d-driver `REGEN` wedged the hop:

| scenario | result |
|---|---|
| before the wedge: `listAlignments`, health | normal route (`hostRoute: commandContext`) |
| first call after the wedge | answered in 5.3 s (the grace period); `hostRoute: fallback` from then on |
| `getDrawingInfo`, `listSurfaces`, `listAlignments` | 0.1 s each |
| `createAlignment` J-5, then J-6 | created through `C3DMCPHOP`, 0.1-0.2 s each |
| `UNDO 1`, twice | removed J-6, then J-5: the edits are undoable and the Idle reads in between left the undo history alone |
| a LISP `getstring` prompt left open over COM, then a write | `CIVIL3D.HOST_BUSY` after 15.1 s; the prompt kept waiting, nothing was typed into it |
| Esc, then the same write; `UNDO 1` | created in 0.2 s; undone |

The same change in the Joshua8-AI fork (`civil3d-2027-support` 34a6b2b) also ran
`-PLOT` sequences through `C3DMCPHOP` in the fallback (1.2 s, landscape) and kept an
edit made before the wedge undoable.

## Deploy-script regression checks

- `install-bundle.ps1` derives `SeriesMin/Max` from the build's target framework
  (`net10.0-windows` → R26.0, `net8.0-windows` → R25.1) and refuses anything else.
- Both install and `-Uninstall` refuse while a running Civil 3D holds the bundle DLL
  (probe: hold the DLL open exclusively, run the script, expect refusal, verify the
  bundle is untouched).
- `gather-refs-2027.ps1` skips only byte-identical staged references and refreshes
  mismatches (probe: plant a wrong DLL under a required name in a scratch `-Destination`).
- `build-2027.ps1` leaves `obj\project.assets.json` on `net8.0-windows` afterwards so a
  default 2026 build with `--no-restore` still works.

## Not automated

The plugin has no C# unit tests; `tests/FileBoundaryHarness` (dotnet) is upstream's
only .NET test. Everything Civil 3D-side is exercised through the live scenarios above.

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

## Host routes without the command-context hop (`fix/plugin-command-route`, 2026-10-11, live)

Civil 3D 2027 on Joshua8-AI's machine, a copy of an HW drawing (TIN surfaces EG
and EG - No Breaklines). civil3d-driver, a COM automation add-in, was loaded and
sent commands throughout.

### Diagnosis (diagnostic build, not merged)

The build logged command, LISP, lock and quiescence events, and checked whether
a queued `ExecuteInCommandContextAsync` callback runs. Each trial started Civil
3D, checked that a callback ran, sent one trigger, then checked again (6 s).

| Windows session | loaded | trigger | callback after it |
|---|---|---|---|
| disconnected | all add-ins | PowerShell COM `SendCommand("(princ) ")` | never ran |
| disconnected | all add-ins | the plugin's own `C3DMCPHOP` (in-process COM) | never ran |
| disconnected | all add-ins | in-process COM `(princ)` | never ran |
| disconnected | all add-ins | `SendStringToExecute("(princ) ")` x3 | ran each time |
| disconnected | this plugin only, Civil 3D started directly | PowerShell COM `(princ)` | never ran |
| connected (RDP) | all add-ins | PowerShell COM `(princ)` x3 | ran (20-51 ms) |
| connected (RDP) | all add-ins | `C3DMCPHOP` x2, in-process COM, driver `REGEN` x2, PowerShell `REGEN` | ran (12-34 ms) |

- In the wedged state, queued callbacks never start their internal
  `EXECUTEFUNCTION` command.
- Meanwhile `Application.Idle` keeps firing, `CMDACTIVE` is 0, the editor is
  quiescent and the document is unlocked.
- Nothing revived it:
  - waiting;
  - `SendStringToExecute`, `PostCommandPrompt`;
  - more COM commands;
  - `WM_NULL`, focus, injected mouse and key input.

Also found:
- `Editor.SelectImplied` returns an error outside a `UsePickSet` command, even
  when LISP `(ssget "_I")` sees the selection.
- A plugin read under a document lock empties the redo stack.

### This branch, live (Windows session disconnected)

| check | result |
|---|---|
| read right after PowerShell COM and driver commands | 0.11 s |
| 30 rounds of read + write, COM or driver command every few rounds | all ok; median 0.07 / 0.10 s, max 0.41 s; 30 alignments |
| three reads, then command-line `UNDO 1` | removed the last write: reads add no undo step |
| write, `undoDrawing`, `redoDrawing`, `undoDrawing` | removed, restored, removed |
| `computeSurfaceVolume`, `calculateSurfaceVolume`, `qtySurfaceVolume` | ok; no scratch surface left in the drawing |
| LISP `sssetfirst` of all alignments, then `getSelectedCivilObjectsInfo`, `getProjectContext` | selection reported; still selected afterwards |
| LISP `getstring` prompt open: read, then write | `HOST_BUSY` after 15 s each; the prompt kept waiting |
| `LINE` waiting for a point: read | `HOST_BUSY` after 15 s; `LINE` still active |
| Esc, then read or write | 0.1 s |

The same design in the Joshua8-AI fork (`civil3d-2027-support` 00b4bbd) passed
further runs in both session states:
- 80 requests from 8 concurrent clients;
- a client disconnecting mid-request;
- a modal dialog;
- jobs, plot, publish and xref operations;
- two drawings and zero drawings;
- a 200-request soak (median 0.12 s).

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

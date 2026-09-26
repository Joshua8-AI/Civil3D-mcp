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

Before the deadlock fix the zero-document scenarios hung for 120 s per call and
wedged the plugin until Civil 3D restarted (`docs/FINDINGS.md` in
`Joshua8-AI/civil3d-automation` has the original field report). The fix is
`fix-no-document-deadlock` / upstream PR #8.

## Xrefs, data-shortcut references, drawing comparison (2026-09-26, `feature/xref-datashortcut-compare`)

Offline, on the branch:

| check | command | result |
|---|---|---|
| Node unit tests (adds `xref_domain`, `data_shortcut_references`, `compare_domain`) | `npm test` | 440 passed / 36 files |
| Generated tool reference current | `npm run docs:check` | current (208 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |
| Fingerprint diff harness | `npm run test:compare-diff` | passed |
| P1/P2/P4 harness still green | `npm run test:p2-boundaries` | passed |
| Startup smoke | `npm run test:startup` | 210 registered tools |

**Live verification is still pending.** Civil 3D was not running and nothing
was deployed. Run these with the plugin loaded and `CIVIL3D_IMPORT_ROOTS` /
`CIVIL3D_EXPORT_ROOTS` covering the test folders:

- `civil3d_xref list` on a drawing with one attached, one overlaid, one
  unloaded, one missing (renamed source), and one nested xref whose parent
  is unloaded. Expect `loaded`, `unloaded`, `not_found`, and `orphaned`,
  with correct `savedPath` and `foundPath`. Check the parent resolution by
  native pointer (`GraphNode.In()` returns an untyped wrapper).
- `attach` and `overlay` with `pathType: relative` on a saved host, then
  `unload`, `reload`, `repath` to a copied file, `bind` with `insert`, and
  `detach`, each through `civil3d_request_approval`. Confirm that the
  no-transaction path (`ExecuteLockedWithoutTransactionAsync`) does not raise
  `eInvalidOpenState` and does not wedge the host gate.
- `civil3d_project data_shortcut_references` on a drawing with current, stale
  (edit and save the source), and broken (rename the source) references.
  Then `data_shortcut_repair` to the renamed source. Check that
  `AeccDataShortcutMgd` resolves (it is loaded on the host thread), that
  `workingFolder` and `currentProjectFolder` are filled, and that `repaired`
  is true.
- `data_shortcut_promote`: check whether `_AeccPromoteReference` honours the
  pickfirst set or prompts for the object. The command name comes from the
  CUIx, but its prompt sequence has not been verified. Check
  `data_shortcut_sync` with `_AeccSynchronizeReferences`.
- `civil3d_compare drawing` against the drawing's own `.bak` or a previous
  submittal copy, including one that is currently open in another document
  (this uses the `OpenTryForReadShare` fallback). Then `snapshot` followed by
  `compare_snapshot` on an unchanged drawing: expect `identical: true`. Time a
  large drawing, because the whole comparison runs on the host thread under
  the gate.

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

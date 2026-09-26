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

### Pending live verification: `civil3d_plot` (feature/plot-publish)

Offline only so far: plugin compiles against 2027 refs, vitest covers schemas,
approval classification, routing and job registration, and the FileBoundary
harness covers the directory lock used while the plotter writes (2026-09-26 on
the branch: `npm test` 437 passed / 34 files, `docs:check` current at 207
entries, `build-2027.ps1` 0 warnings / 0 errors). None of the
following has been run against a live Civil 3D 2027 yet:

| scenario | how | last result |
|---|---|---|
| Discovery | `civil3d_plot` `list_layouts`, `list_page_setups`, `list_plotters` (with and without `device: "DWG To PDF.pc3"`) | pending |
| Zero documents | close all drawings, call `list_layouts` and `plot_layouts_to_pdf` | pending (expect `CIVIL3D.NO_DRAWING` fast) |
| Plot one layout | approval, then `plot_layouts_to_pdf` with `layoutNames: [..]`, `outputPath` under Documents | pending |
| Plot all layouts | `allLayouts: true`, `outputDirectory`; check per-layout bytes/pageCount, CTAB and BACKGROUNDPLOT restored | pending |
| Page setup / paper override | `pageSetup`, then `paperSize` + `plotStyleTable: "monochrome.ctb"` | pending |
| Overwrite guard | repeat without `overwrite` (expect `CIVIL3D.CONFLICT`), then with `overwrite: true` | pending |
| Path boundary | `outputDirectory` outside export roots (expect `CIVIL3D.PATH_NOT_ALLOWED`, nothing plotted) | pending |
| Prompt-chain drift | a layout whose paper is not on DWG To PDF.pc3 (expect up-front `INVALID_INPUT`, no stuck `-PLOT`) | pending |
| Publish multi-sheet | save drawing, `publish_sheet_set` with 2+ layouts; confirm page count and order; `keepDsd: true` to inspect the DSD | pending (DSD `Type=6` and the `-PUBLISH` prompt chain are unverified on 2027) |
| Publish unsaved guard | modify drawing, `publish_sheet_set` (expect `CIVIL3D.CONFLICT`) | pending |
| As job | `asJob: true`, poll `civil3d_job status`, cancel mid-batch | pending |

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

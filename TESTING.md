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

### Live on Civil 3D 2027 — 2026-09-26 (plot, xref, compare, bridge reads)

Plugin from `civil3d-2027-support` at `35b5600`, installed with
`build-2027.ps1 -Install`. Calls went straight to the plugin on 8757 through
the repo's own `build/utils/ConnectionManager.js` (the approval gate lives in
the Node server and is covered offline). Drawings: copies of the 2027 Civil
Tutorials `Pipe Networks-1A.dwg` (meters, NH83; 2 TIN surfaces, 56 parcels),
`Pipe Networks-3.dwg` (feet; 1 gravity network, 11 pipes / 12 structures) and
`Plan Production-Plan Profile Sheets-Create.dwg` (2 layouts, 6 page setups),
placed under Documents (the default file root).

| scenario | result |
|---|---|
| `npm run test:live-plugin`, zero documents | health OK; `CIVIL3D.NO_DRAWING` in 6 ms |
| `getDrawingUnits` (meters drawing / feet drawing) | `Meters` / `Feet`, `unitsConsistent: true` |
| `getSurfaceTinVertices` EG (23,092 vertices) | full set in 163 ms (1.07 MB); `maxPoints: 5` → even stride, `truncated: true` |
| `getParcelGeometry` rectangular lot / lot with an arc | `computedArea` = `area` to 0.001 m²; `geometrySource: baseCurve:Polyline` |
| `getParcelGeometry` road ROW (parcel with holes) | outer loop only: 77,140 vs 20,913 m², flagged in `notes` — **known gap** |
| `getPipeNetwork` Pipe Networks-3 | **was failing** (`Retrieve attribute failed` on network style / parts list); fixed in `35b5600`: 11 pipes + 12 structures in 131 ms, `style`/`partsList` null, all new pipe fields filled, inverts match `civil3d_compare` |
| `plotListLayouts` / `plotListPageSetups` / `plotListPlotters` | 2 layouts / 6 setups / 19 devices (2.4 s for the device list) |
| `plotLayoutsToPdf` Letter layout on DWG To PDF.pc3 | refused up front with a `paperSize` hint (no stuck `-PLOT`) |
| `plotLayoutsToPdf` `allLayouts` + `paperSize: "ANSI A (11.00 x 8.50 Inches)"` | 2 PDFs in 1.6 s; Layout2 content checked visually |
| `plotPublishSheetSet` right after plotting | `CIVIL3D.CONFLICT` (plotting marks the drawing modified) |
| `plotPublishSheetSet` `requireSaved: false`, `keepDsd: true` | 2-sheet PDF in 3.1 s; generated DSD accepted by 2027 `-PUBLISH` |
| `listXrefs` / `overlayXref` (relative) / `unloadXrefs` / `detachXrefs` | correct statuses and stored `.\Parcel-1A.dwg` |
| `reloadXrefs`, `bindXrefs` | **were hanging to the 120 s timeout** (work done, control never returned); fixed in `7a7fac9` via `-XREF`: reload 80 ms, repath 95 ms, bind (insert) 209 ms |
| `writeDrawingSnapshot` / `compareDrawingSnapshot` | 28 KB fingerprint in 124 ms / diff in 336 ms |
| `compareDrawings` against a DWG open in another tab | side database read, 376 ms |
| `listDataShortcutReferences` | empty list, working folder reported |

Not exercised live: `data_shortcut_repair` / `promote` / `sync` (the tutorial
drawings have no data-shortcut references), `asJob` plotting, overwrite and
path-boundary refusals, zero-document plot calls, grid/volume surfaces for
`getSurfaceTinVertices`, US-survey-foot drawings for `getDrawingUnits`.

Autodesk's own AutoCAD MCP (`AutoCAD-MCP-Server-2027.bundle`, not ours):
its `MCPHTTPSTART` command exists only when `ACMCP_TOOLS_CONFIG_FILE` points at
a config with `EnableHttpServer: true`, and then fails with
`FileNotFoundException: ModelContextProtocol.AspNetCore 0.4.0.0` — the 2027
bundle does not ship its HTTP host, so it cannot be registered as an external
MCP server.

### Live checklist: `civil3d_plot` (feature/plot-publish)

Offline only so far: plugin compiles against 2027 refs, vitest covers schemas,
approval classification, routing and job registration, and the FileBoundary
harness covers the directory lock used while the plotter writes (2026-09-26 on
the branch: `npm test` 437 passed / 34 files, `docs:check` current at 207
entries, `build-2027.ps1` 0 warnings / 0 errors). Live results from
2026-09-26 are in the table above; rows still marked pending were not run:

| scenario | how | last result |
|---|---|---|
| Discovery | `civil3d_plot` `list_layouts`, `list_page_setups`, `list_plotters` (with and without `device: "DWG To PDF.pc3"`) | passed 2026-09-26 |
| Zero documents | close all drawings, call `list_layouts` and `plot_layouts_to_pdf` | pending (expect `CIVIL3D.NO_DRAWING` fast) |
| Plot one layout | approval, then `plot_layouts_to_pdf` with `layoutNames: [..]`, `outputPath` under Documents | pending |
| Plot all layouts | `allLayouts: true`, `outputDirectory`; check per-layout bytes/pageCount, CTAB and BACKGROUNDPLOT restored | passed 2026-09-26 (2 layouts, 1.6 s) |
| Page setup / paper override | `pageSetup`, then `paperSize` + `plotStyleTable: "monochrome.ctb"` | pending |
| Overwrite guard | repeat without `overwrite` (expect `CIVIL3D.CONFLICT`), then with `overwrite: true` | pending |
| Path boundary | `outputDirectory` outside export roots (expect `CIVIL3D.PATH_NOT_ALLOWED`, nothing plotted) | pending |
| Prompt-chain drift | a layout whose paper is not on DWG To PDF.pc3 (expect up-front `INVALID_INPUT`, no stuck `-PLOT`) | passed 2026-09-26 |
| Publish multi-sheet | save drawing, `publish_sheet_set` with 2+ layouts; confirm page count and order; `keepDsd: true` to inspect the DSD | passed 2026-09-26 (2 sheets, DSD accepted by 2027) |
| Publish unsaved guard | modify drawing, `publish_sheet_set` (expect `CIVIL3D.CONFLICT`) | passed 2026-09-26 (plotting itself marks the drawing modified) |
| As job | `asJob: true`, poll `civil3d_job status`, cancel mid-batch | pending |
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

**Partly verified live on 2026-09-26** (see the live table above: list,
overlay, unload, reload, repath, bind, detach, compare, snapshot). Still to
run with the plugin loaded and `CIVIL3D_IMPORT_ROOTS` /
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

## Bridge-support reads (2026-09-26, `feature/bridge-support-commands`)

`getSurfaceTinVertices`, `getParcelGeometry`, `getDrawingUnits`, and the new
pipe fields on `getPipeNetwork`. Offline, on the branch:

| check | command | result |
|---|---|---|
| Node unit tests (adds `bridge_support_commands`) | `npm test` | 467 passed / 38 files |
| Generated tool reference current | `npm run docs:check` | current (209 entries) |
| Version files in sync | `npm run version:check` | agree on 1.2.1 |
| Plugin compiles (2027 refs) | `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |
| Arc, area, chaining, decimation and unit math | `npm run test:bridge-math` | passed |
| Other harnesses still green | `npm run test:compare-diff`, `npm run test:p2-boundaries` | passed |
| Startup smoke | `npm run test:startup` | 211 registered tools |

The Civil 3D API members used were checked against the 2027 reference
assemblies (metadata only): `TinSurface.GetTriangles(bool)`,
`TinSurfaceTriangle.Vertex1..3.Location`, `GridSurface.GetVertices(bool)`,
`Parcel.BaseCurve`/`GetGeCurve`/`Explode`, `Pipe.InnerHeight`/`OuterDiameterOrWidth`,
`SettingsUnitZone.DrawingUnits`/`ImperialToMetricConversion`.

**Mostly verified live on 2026-09-26** (see the live table above, and the
bridge end-to-end run in civil3d-automation `bridge/TESTING.md`). Still open:

- `getSurfaceTinVertices` on a TIN surface with an outer boundary: check that
  `totalVertexCount` excludes points outside the boundary, that a `boundary`
  polygon clips, and that `maxPoints: 100000` on a surface with more vertices
  returns `truncated: true`, the same subset on a second call, and a response
  under 8 MiB. Time it on a surface with over 1 million vertices (it walks
  every visible triangle on the host thread). Try a grid surface (DEM) and a
  TIN volume surface (expect `CIVIL3D.INVALID_INPUT`).
- `getParcelGeometry` on a rectangular lot, a lot with a curved frontage, and
  a parcel with a curve on its back line. Record `geometrySource`: whether
  `Parcel.BaseCurve` works or the `GetGeCurve`/`Explode` fallbacks are needed.
  `computedArea` should match `area` (a note appears when they differ by more
  than 0.1%). Check the sign of the bulges against the drawing, and check a
  parcel with an interior hole: only the outer loop is expected.
- `getDrawingUnits` on drawings with INSUNITS = Feet (2), USSurveyFeet (21),
  Meters (6) and Undefined (0), and on a Feet drawing whose Civil 3D
  Drawing Settings use the US survey foot (expect `unitsConsistent: false`
  and a warning).
- `getPipeNetwork`: compare `startPoint`/`endPoint` with the pipe's grips and
  `startInvert`/`endInvert` with the inverts shown in Pipe Properties,
  including an elliptical or box pipe (`innerHeight` differs from
  `innerDiameter`).
- Bridge end to end: `bridge_status` should report the three commands as
  `available`; `bridge_create_toposolid sampling: "tin"` and the pipe and
  parcel tools should use the new data.

## cubic review fixes ported from upstream PRs #17-#19 (2026-09-26, offline only)

Ported from the review-fix commits on `upstream/plot-publish`,
`upstream/xref-datashortcut-compare` and `upstream/bridge-support-reads`.
None of these have been re-run live.

- **Plot/publish (#17):** the plotter and `-PUBLISH` write to a hidden,
  unpredictable temp name in the locked output folder; it is checked to be a
  regular file and renamed over the final name (`FileBoundary.BeginExternalWrite`),
  so a link planted at the final name after validation cannot redirect output,
  and a failed plot no longer deletes an existing PDF. `RunCommandAsync` refuses
  to start a command while one it drove earlier is still at a prompt (the queued
  Ctrl-C only runs after the host work returns). `publish_sheet_set` skips (with
  a warning) or rejects never-initialized layouts. Whitespace-only `pageSetup`,
  `paperSize`, `plotStyleTable` and `device` are rejected by the schema.
- **Data shortcuts / compare (#18):** unreadable reference health is `unknown`,
  not `current`; repair/promote prefer a reference over a same-named local object;
  `view_frame_group` reaches `data_shortcut_repair`; `civil.truncated`; malformed
  snapshots are `INVALID_INPUT`; block attributes and TIN/grid triangle count and
  areas are in the fingerprint.
- **Bridge reads (#19):** inner height falls back to `InnerDiameterOrWidth` only
  for circular pipes (otherwise null heights/inverts); the units/info reads return
  the raw coordinate-system code without a library lookup. The same defensive
  inner-height read was applied to the `civil3d_compare` pipe-network fingerprint,
  where a throwing `InnerHeight` dropped the whole network.

| check | result |
|---|---|
| `npm run build` | ok |
| `npm test` | 472 passed / 38 files |
| `npm run docs:check` | current (209 entries) |
| `npm run version:check` | agree on 1.2.1 |
| `npm run test:startup` | 211 tools |
| `test:p2-boundaries`, `test:compare-diff`, `test:bridge-math` | passed |
| `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |

Live checks still to do: plot and publish to a fresh and an existing PDF
(temp file renamed, no `.mcp-tmp.pdf` left behind); publish with a
never-opened layout; `civil3d_compare` on a network with a non-circular pipe.

## Profile view, layer and K-value fixes (2026-09-28, offline only)

Three bugs found live on Civil 3D 2027: `civil3d_profile view_create` always
failed (reflection probed `ProfileView.Create` overloads that do not exist; now
the typed 2027 overload, with first-style/band-set fallback and a new `layer`);
a missing requested layer silently became the current layer (now created, all
10 `GetLayerId` callers are inside `CivilExecution.WriteAsync`); and
`check_k_values` used A as a decimal and a metric-only table (now
K = L / (100·|g2−g1|) against AASHTO SSD tables in mph or km/h, `speedUnits`
defaulting from the drawing units). `get` also reported symmetric parabolas as
asymmetric; fixed. The band-set lookup, which always returned a null id, was
fixed along the way.

| check | result |
|---|---|
| `npm run build` | ok |
| `npm test` | 480 passed / 39 files (new `tests/profile_domain.test.ts`, 8 tests) |
| `npm run docs:check` | current (209 entries) |
| `npm run version:check` | agree on 1.2.1 |
| `npm run test:startup` | 211 tools |
| `npm run test:vertical-curve-math` (new harness) | passed |
| `test:p2-boundaries`, `test:compare-diff`, `test:bridge-math` | passed |
| `.\scripts\build-2027.ps1` (no `-Install`) | 0 warnings, 0 errors |

**Live verification pending** (needs the new DLL installed, so Civil 3D must be
closed first):
- `view_create` with no style/band set, with named ones, with an unknown style
  (warning), and with `layer` set to a new layer; check name, handle, layer and
  style in the result and in the drawing.
- `create_layout layer:"C-ROAD-DES"` on a drawing without that layer: the layer
  is created and the profile is on it; an invalid name (e.g. `A<B`) is rejected.
- `check_k_values` on the 280 ft, −1.4 % → +1.4 % sag: K = 100, required 96 at
  50 mph (feet drawing, default mph); 52 mph uses the 55 mph row with a note;
  25 mph is rejected; `speedUnits:"km/h"` on the feet drawing converts lengths.
- `get` on a profile with an `add_curve` symmetric parabola reports
  `symmetric_parabola`.
- `view_band_set` now imports the named band set (it previously got a null id).

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

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Script Merger for The Witcher 3 — a Windows desktop tool (WinForms, not WPF) that detects and merges conflicting mod script files. It scans a mod folder, finds `.ws`/`.xml` files (including inside `.bundle` packages) that multiple mods modify, and drives a 3-way merge (vanilla + mod1 + mod2) via the external tool KDiff3. `.bundle` package contents are unpacked with QuickBMS and repacked with wcc_lite.

This is a fork of the upstream `AnotherSymbiote/WitcherScriptMerger` repo (still the `origin` remote — no separate fork exists yet), currently mid-modernization. See `HANDOFF.md` at the repo root for the full rationale behind the fork, detailed gotchas hit during the .NET modernization, and the current list of open goals (whitespace/diff-noise in KDiff3 merges, a possible CLI mode, dependency-packaging/licensing decisions) — read it before picking up follow-on work in this repo.

## Build & run

- Build: `dotnet build WitcherScriptMerger.sln` from the repo root.
- Run: launch the built `WitcherScriptMerger.exe`, or `dotnet run --project WitcherScriptMerger/WitcherScriptMerger.csproj`. At startup the app validates `KDiff3Path`/`QuickBmsPath`/`QuickBmsPluginPath`/`WccLitePath` from `App.config` (`Paths.ValidateDependencyPaths` in `WitcherScriptMerger/Paths.cs`) and shows a blocking `DependencyForm` if any are missing — the external binaries (KDiff3, QuickBMS, wcc_lite) are **not** in source control (see "External tool dependencies" below), so a fresh checkout won't run end-to-end without sourcing them separately.
- Single project, single `.sln` — there is no separate class library to build independently.

### Tests

There is no test project in this repo (`dotnet test` has nothing to run). The precedent set in `HANDOFF.md` for verifying logic changes — especially anything touching hash output, `MergeInventory.xml` schema, or KDiff3 invocation — is a disposable, non-committed `dotnet new console` scratch app: exercise synthetic edge cases plus a cross-check against a real value already recorded in a live `MergeInventory.xml`. Follow that pattern rather than assuming API docs alone are sufficient, particularly for anything hash- or serialization-related (see "Compatibility constraints" below).

## Architecture

Single WinForms project (`WitcherScriptMerger/WitcherScriptMerger.csproj`, SDK-style, targets `net10.0-windows7.0`). There is no MVC/MVP split — `Forms/MainForm.cs` (~1000 lines) is a monolithic orchestrator that directly owns the tree controls, constructs `ModFileIndex`/`FileMerger`, and wires up their async callbacks. `Program.MainForm` (a static field) is used as a global service locator for dialogs (`Program.MainForm.ShowMessage/ShowError/ShowModal`) called from deep inside domain classes like `FileMerger` and `Paths` — UI and domain logic are not cleanly separated, so don't assume domain code is UI-independent when refactoring.

Folder map:
- `Forms/` — WinForms screens: `MainForm.cs` (the hub), `OptionsForm.cs`, `DependencyForm.cs` (startup blocker if tool paths are invalid), `MergeReportForm.cs`, `PackReportForm.cs`, `PriorityPrompt.cs`, `MessageBoxManager.cs`.
- `Controls/` — custom `TreeView` subclasses: `SMTree.cs` (base, metadata/context-menu logic), `ConflictTree.cs` (detected conflicts), `MergeTree.cs` (existing merges), `SMTreeSorter.cs`, `ToolStripRegion.cs`.
- `FileIndex/` — scans the mods folder and builds the conflict index: `ModFileIndex.cs` (`BuildAsync` → `Conflicts`), `ModFile.cs`, `ModFileCategory.cs`.
- `Inventory/` — core merge domain + persistence: `FileMerger.cs`, `Merge.cs`, `MergeInventory.cs`, `FileHash.cs`, `MergeProgressInfo.cs`.
- `LoadOrder/` — mod load-order logic: `CustomLoadOrder.cs`, `LoadOrderComparer.cs`, `LoadOrderValidator.cs`, `ModLoadSetting.cs`.
- `Tools/` — wrappers that shell out to bundled external executables: `KDiff3.cs`, `QuickBms.cs`, `WccLite.cs`, `Hasher.cs`.
- Root: `Program.cs` (entry point), `AppSettings.cs`, `Paths.cs`, `Extensions.cs`, `TaskbarProgress.cs`, `App.config`.

### Startup flow (`Program.cs`)

`[STAThread] Main()` → `Application.EnableVisualStyles()` → construct static `Program.Settings = new AppSettings()` (exits via MessageBox if `App.config` is missing) → `Paths.ValidateDependencyPaths()` (shows `DependencyForm` if KDiff3/QuickBMS/wcc_lite paths are invalid) → construct and `Application.Run(MainForm)`.

### Merge flow

No hand-rolled diff algorithm lives in this codebase — it's an orchestrator around KDiff3 for text merges and QuickBMS/wcc_lite for `.bundle` archives:

1. `FileIndex/ModFileIndex.BuildAsync` scans `Paths.ModsDirectory`, groups files by relative path, flags conflicts.
2. `MainForm` feeds the results into `ConflictTree`/`MergeTree`; the user checks nodes to merge.
3. `Inventory/FileMerger.MergeByTreeNodesAsync` runs on a `BackgroundWorker`, building/reusing an `Inventory/Merge` record per file and dispatching to `MergeFlatFileNode` (plain `.ws`/`.xml`) or `MergeBundleFileNode` (bundle-packed files, which first go through `Tools/QuickBms.UnpackFile`).
4. `FileMerger.MergeText` calls `Tools/KDiff3.Run(source1, source2, vanillaFile, outputPath)`, which shells out to `KDiff3.exe` (`--auto` for auto-solvable 3-way merges, or opens its GUI for manual resolution).
5. On success, `Inventory/MergeInventory.AddModToMerge` hashes the result via `Tools/Hasher` (xxHash32, `System.IO.Hashing`) and persists the merge record to `MergeInventory.xml` (`XmlSerializer`). `MergeInventory.HasResolvedConflict` re-checks these hashes on refresh to detect merges made stale by upstream mod file changes.
6. Bundle content changes additionally go through `FileMerger.PackNewBundle` → `Tools/WccLite.PackBundle` + `GenerateMetadata` to repack `blob0.bundle`.

### Compatibility constraints

- **Hash format is load-bearing.** `MergeInventory.xml` (including real, already-populated files on developer machines) stores per-file hashes compared by string equality to detect when a mod source file has changed since it was last merged. Any change to `Tools/Hasher.cs` must produce byte-for-byte identical output to the current implementation, or every existing recorded merge silently "goes stale." Verify with the synthetic-edge-cases + real-recorded-hash cross-check pattern described under Tests.
- **TFM must keep the explicit `7.0` OS-version suffix.** The project uses `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` (to keep the hand-written `Properties/AssemblyInfo.cs`), which also suppresses the SDK's auto-generated `[assembly: SupportedOSPlatform("windows")]` attribute — that attribute is added manually in `AssemblyInfo.cs` instead. The TFM must stay `net10.0-windows7.0` (not bare `net10.0-windows`); dropping the `7.0` suffix reintroduces ~800 spurious `CA1416` platform-compatibility warnings.

### Settings & persistence

- App settings: `AppSettings.cs` wraps `System.Configuration.ConfigurationManager` over `App.config`'s `<appSettings>` block (`Get<T>`/`Get`/`Set`/`Save`, cached `Configuration` object) — deliberately *not* `Properties.Settings` (that scaffolding was removed during the SDK-style migration). Settings are cached and require an explicit `Save()` call.
- Merge history: `MergeInventory.xml`, via `XmlSerializer` (`Inventory/MergeInventory.cs`).
- Game load order: `LoadOrder/CustomLoadOrder.cs` reads the game's own `mods.settings` file.

### External tool dependencies

Three bundled Windows executables are invoked via `Process.Start`, with relative paths configured in `App.config`'s `<appSettings>` (`KDiff3Path`, `QuickBmsPath`, `QuickBmsPluginPath`, `WccLitePath`):
- **KDiff3** (`Tools\KDiff3\KDiff3.exe`) — GPL-licensed, safe to bundle into a release.
- **QuickBMS** (`Tools\QuickBMS\quickbms.exe` + `witcher3.bms` plugin) — no license file found; do not add to source control.
- **wcc_lite** (`Tools\wcc_lite\bin\x64\wcc_lite.exe`) — no license file found; do not add to source control.

None of these binaries are committed to this repo (matches the original upstream project's precedent) — keep it that way; if packaging is tackled later, it belongs in a separate release artifact, not source control.

## Coding standards & SOP

See `CONTRIBUTING.md` for observed code style (bracing, naming, region conventions) and repository process (branching, commit style, AI-assisted-development disclosure).

# CLAUDE.md — WitcherScriptMerger.Core

Guidance for working in `WitcherScriptMerger.Core` (`net10.0`, no WinForms reference,
deliberately cross-platform-capable). This project holds all domain logic — file
scanning, merge orchestration, load-order handling, settings/paths, and the CLI/MCP
entry-point logic shared by both hosts. Nothing here references `System.Windows.Forms`.

See the root `CLAUDE.md` for the overall project/fork context and the other three
projects. See `Mcp/CLAUDE.md` for the MCP tools' minimal-permissions detail (not
duplicated here). See `docs/decisions/kdiff3-retirement.md` for the full empirical
history of the KDiff3 engine this project's `DiffPlexMergeEngine` replaced, and
`docs/decisions/bundle-format-replacement-spike.md` for why QuickBMS/wcc_lite are
still external dependencies rather than an in-process replacement.

## Folder map

- `FileIndex/` — scans the mods folder and builds the conflict index: `ModFileIndex.cs`
  (`BuildAsync` → `Conflicts`), `ModFile.cs`, `ModFileCategory.cs`.
- `Inventory/` — core merge domain + persistence: `FileMerger.cs` (headless orchestration
  plus TreeNode-free interactive orchestration — see "FileMerger: interactive vs.
  headless split" below), `Merge.cs`, `MergeInventory.cs`, `FileHash.cs`,
  `MergeProgressInfo.cs`.
- `LoadOrder/` — mod load-order logic: `CustomLoadOrder.cs`, `LoadOrderComparer.cs`,
  `LoadOrderValidator.cs`, `ModLoadSetting.cs`.
- `Tools/` — wrappers that shell out to bundled external executables, plus the sole
  text-merge engine: `QuickBms.cs`, `WccLite.cs`, `Hasher.cs`, `FileEncoding.cs`
  (UTF-16LE+BOM normalization — see "Text-merge input encoding" below),
  `DiffPlexMergeEngine.cs` (see below), `FileOpener.cs` (portable "open in the OS's
  default associated app" helper; the only call site is
  `DiffPlexMergeEngine.MergeHeadless`, opening a genuine conflict's marker sidecar).
- `Cli/` — `MergeOperations.cs`: the scan-then-merge sequence shared by both hosts'
  `merge` CLI verb and by the MCP tools (see "CLI & MCP orchestration" below).
- `Mcp/` — `WsmMcpTools.cs`: the MCP server's tool implementations (see below and
  `Mcp/CLAUDE.md`).
- Root: `AppState.cs` (shared mutable state — see below), `AppSettings.cs`, `Paths.cs`,
  `StringExtensions.cs`, `IMergeNotifier.cs`, `NotifyTypes.cs` (the neutral
  `NotifyResult`/`NotifyButtons`/`DialogIcon` enums), `HeadlessMergeNotifier.cs`,
  `VersionInfo.cs` (`GetVersion(Assembly)` — the shared implementation behind both
  hosts' `--version` CLI flag and their MCP server's `ServerInfo.Version`; not
  duplicated per host despite the two hosts' differing assembly-versioning setups — see
  each host's own `CLAUDE.md` for the call sites and why the fallback chain handles both
  uniformly).

## AppState & IMergeNotifier

`AppState` (`AppState.cs`) holds the shared mutable state that used to be static fields
directly on the WinForms host's `Program` class — `Notifier`/`Settings`/`LoadOrder`/
`Inventory`. It moved here because domain code that now lives in Core needs to read/write
it, and Core can never reference either host assembly (the dependency only flows
host → Core). Both hosts' `Program` classes re-expose these as pass-through properties
(the WinForms host keeps calling them `Program.Notifier` etc.) so their own call sites
didn't need to change.

- **`AppState.Notifier`** defaults to `HeadlessMergeNotifier` via a field initializer,
  unconditionally, so any startup error is safe to report even before it's known whether
  a given run is GUI, CLI, or MCP.
- **`AppState.Settings`** is a **lazy property**, not a field initializer
  (`LazyInitializer.EnsureInitialized`, not the simpler non-atomic
  `_settings ?? (_settings = new AppSettings())`, for thread-safety against a hypothetical
  future concurrent first-access caller) — deliberately decoupled from `Notifier`'s eager
  init. `AppSettings`'s constructor calls `Environment.Exit(1)` if it can't find a config
  file next to the entry assembly — correct for the real GUI/CLI/MCP entry points, where
  that's genuinely fatal, but not for `WitcherScriptMerger.Tests`'s `dotnet test` host,
  which has no matching `.config`. C# runs *all* of a type's static field initializers
  together on first touch of *any* static member, so before this laziness existed, merely
  reading `AppState.Notifier` — which Core code legitimately does on its own (e.g.
  `DiffPlexMergeEngine`'s headless skip/guard messages) — silently also ran
  `new AppSettings()` and killed the entire test process. See
  `WitcherScriptMerger.Tests/CLAUDE.md` for the test-side constraints this laziness
  exists to satisfy.
- `AppState` has an explicit (empty) static constructor so its own init ordering is
  deterministic rather than left to `beforefieldinit`'s discretion. `Paths.cs`'s
  `ScriptsDirectory`/`ModsDirectory`/`IsScriptsDirectoryDerived`/`IsModsDirectoryDerived`
  read `AppState.Settings.Get(...)` on every access rather than caching the result via a
  static field initializer, for the identical reason one layer further out: a field
  initializer there would force `Settings` to construct merely from touching an unrelated
  static member of `Paths` (e.g. `GetRelativePath`), via the same `beforefieldinit`
  mechanism.

**`IMergeNotifier`** (`IMergeNotifier.cs`, `NotifyTypes.cs`, `HeadlessMergeNotifier.cs`)
replaces every direct UI call in domain code with `AppState.Notifier.*`. It's defined
against neutral `NotifyResult`/`NotifyButtons`/`DialogIcon` types, not
`DialogResult`/`MessageBoxButtons`/`MessageBoxIcon` — Core can't reference
`System.Windows.Forms` at all. `ShowModal(Form)` isn't part of the interface: every call
site is GUI-only, interactive code living in the WinForms host, which calls
`MainForm.ShowModal` directly instead. `IsInteractive` was dropped too (confirmed dead
code, zero read call sites before or after the Core split). `ShowMessage`'s
`defaultResult` parameter is the caller's own answer for "which result is safe/
non-destructive for this specific prompt" — added for `LoadOrderValidator`'s
`YesNoCancel` prompt, whose safety shape is inverted from usual (Cancel, not Yes/No, is
the one destructive/permanent choice there). `HeadlessMergeNotifier` writes to the
console and returns a fixed, non-destructive default per button set (don't overwrite,
don't use a still-conflicting merge name, don't continue past a failure) unless the
caller overrides it via `defaultResult`. The WinForms host's `MainForm` implements this
interface too, translating the neutral types to/from real WinForms calls — see
`WitcherScriptMerger/CLAUDE.md` for that translation and the one behavior change it
introduced.

This indirection isn't abstraction for its own sake: it's also what fixed a real
null-ref hazard in `LoadOrder/CustomLoadOrder.Refresh()`, which used to reach
`Program.MainForm` directly at construction time — a problem for any code path (like the
CLI/MCP entry points) that constructs a `CustomLoadOrder` before a `MainForm` exists at
all. Going through `AppState.Notifier` (defaulting to `HeadlessMergeNotifier`, always
constructed) instead of the WinForms host's concrete form removed that ordering
dependency entirely.

## FileMerger: interactive vs. headless split

Core's `FileMerger` (`Inventory/FileMerger.cs`) never sees a `TreeNode`,
`BackgroundWorker`, or `Forms.*` type.

- Its **headless** methods (`MergeConflictsHeadless`, `MergeFlatConflictHeadless`,
  `MergeBundleConflictHeadless`, `ResolveMergeOrder`, ...) are unchanged in shape from
  before the Core/host split.
- Its **interactive** methods (`MergeFilesInteractive`, `MergeFlatFileInteractive`,
  `MergeBundleFileInteractive`, `MergeTextInteractive`) take a plain
  `InteractiveMergeRequest` (relative path, bundle flag, vanilla file path, ordered
  `MergeSource[]`) instead of `TreeNode[]`, and report back through `OnMergeReport`/
  `OnPackReport` callbacks instead of constructing report forms directly. The WinForms
  host's `Inventory/InteractiveMergeRunner.cs` is the concrete thing that drives this
  path (extracts `InteractiveMergeRequest`s from checked `TreeNode`s, owns the
  `BackgroundWorker`, supplies the callbacks) — see `WitcherScriptMerger/CLAUDE.md`.

There used to be an `IMergeEngine` interface between `FileMerger` and the actual
text-merge implementation, with two implementations — `KDiff3MergeEngine` (host,
wrapping the external KDiff3 process) and `DiffPlexMergeEngine` (Core, in-process). KDiff3
was retired (see `docs/decisions/kdiff3-retirement.md` for the full rationale and the
empirical KDiff3 process-behavior findings preserved there); with only one implementation
ever going to remain, the interface indirection was deleted as premature abstraction.
`FileMerger`'s constructor (`public FileMerger(MergeInventory inventory)`) now builds its
own private `DiffPlexMergeEngine` field directly — there's no engine-selection step at
startup in either host anymore.

## Vortex-fork parity fixes (`mods.settings` "VK=" lines, DLC-bundle-folder matching)

Two small parity gaps versus a separate, Vortex-integrated fork of this project
(`IDCs/WitcherScriptMerger`, the fork Vortex's real `game-witcher3` extension actually
drives) were found by direct comparison and fixed here:

- **`LoadOrder/CustomLoadOrder.ProcessLine`** now recognizes and ignores a `VK=`
  (VortexKey) line instead of falling into the catch-all "unrecognized value" branch.
  Vortex's own mod-management integration writes this key into `mods.settings`; without
  this, `ProcessLine` returning `false` aborts `Refresh()`'s entire parse loop
  (`IsValid` stays `false`, `Mods` stays empty) the moment a Vortex-managed
  `mods.settings` is read. Deliberately a narrow, explicit `VK=` check rather than a
  generic "tolerate any unrecognized key" change — the catch-all warning is intentional
  malformed-file detection, and broadening it to accept-all would remove that
  protection for a genuinely broken file. If another mod manager introduces another key
  this parser doesn't know, it fails the same way `VK=` used to, by design; fix it the
  same way, one recognized key at a time, rather than widening acceptance generically.
- **`Inventory/FileMerger.IsVanillaDlcBundleFolder`** (backing `GetUnpackedFiles`'s
  vanilla-bundle lookup) now matches `"bob"` (Blood & Wine's internal DLC folder
  codename) in addition to `DLC[0-9]*`/`ep[0-9]`, and matches case-insensitively. The
  original regex had no `bob` alternative at all — Blood & Wine bundle-content
  conflicts never matched against a vanilla bundle — and was case-sensitive, which
  matters because real on-disk folder names vary in casing across different game/mod
  installs regardless of platform. Exposed as a public static pure function (mirroring
  `DiffPlexMergeEngine.BuildMerge`'s own public/static shape) specifically so it's
  directly unit-testable.

Both are regression-tested in `WitcherScriptMerger.Tests`
(`LoadOrder/CustomLoadOrderTests.cs`, `Inventory/FileMergerTests.cs`) — see
`WitcherScriptMerger.Tests/CLAUDE.md`.

## CLI & MCP orchestration (`Cli/`, `Mcp/`)

`Cli/MergeOperations.cs` is the scan-then-merge sequence shared by both hosts' `merge`
CLI verb and by the MCP tools, so neither duplicates the scan/wait/merge sequence.
`ScanConflicts()` runs `ModFileIndex.BuildAsync` synchronously (via a
`ManualResetEventSlim`) and returns the built index; `RunMerge(inventory, conflicts,
mergedModName, orderOverrides, dryRun)` calls `FileMerger.MergeConflictsHeadless`, which
iterates `ModFileIndex.Conflicts` directly — plain `ModFile`/`FileHash` objects, so no
`TreeNode`/`ConflictTree` is ever constructed on this path.

Per-file mod order defaults to `LoadOrderComparer` (matching the WinForms host's
`ConflictTree`'s own default sort). An `orderOverrides` map (`{"relative\\path.ws":
["modA", "modB"]}` — the CLI's `--order-file` JSON has the identical shape, minus the
file) overrides specific files without requiring every conflict to be listed.
`FileMerger.ResolveMergeOrder` validates a listed file's mod list: no unknown mod names,
no duplicates, at least two entries, and every one of that file's *real* source mods
present at least once — any violation rejects that one file with a clear error via
`AppState.Notifier.ShowError` rather than silently merging an incomplete, self-paired, or
no-op chain. "Real source mods" deliberately excludes the configured merged-mod name
itself, since a file that's already been merged once has its own merged-mod folder
re-enter `conflict.Mods` as if it were a source.

`Mcp/WsmMcpTools.cs` (`[McpServerToolType]` static class) exposes four tools —
`scan_conflicts`, `merge_conflicts` (optional `relativePaths`/`orderOverrides`/`dryRun`;
returns `{merged, skipped, unmatched, dryRun}`), `get_status`, `list_merges` — all
reusing `MergeOperations` and the same `IMergeNotifier` machinery as the CLI verb.
`get_status` reports `textMergeDependenciesValid` and `bundleDependenciesValid` as two
independent fields (plus a combined `dependenciesValid`, kept for existing callers that
only checked one flag) — deliberately split rather than a single boolean, so a host with
no QuickBMS/wcc_lite (e.g. `WitcherScriptMerger.Headless`) doesn't report a
`conflictCount` of 0 just because bundle tooling is missing; `conflictCount` itself only
requires `textMergeDependenciesValid`. State is re-scanned/re-loaded on every call, never
cached server-side, since the mods folder or `MergeInventory.xml` can change between
calls. `merge_conflicts`'s
`relativePaths`/`orderOverrides` keys are validated to resolve inside `Paths.ModsDirectory`
before any scan or merge runs (`EnsureInScope`/`IsWithinModsDirectory`) — defense in
depth, since neither value is actually joined into a filesystem path anywhere today.
`ScanConflicts`/`MergeConflicts` gate on `Paths.ValidateTextMergeDependencies()` only
(via `RequireDependenciesAndModsDirectory`), not the combined
`Paths.ValidateDependencyPaths()` — see "Dependency validation" below for why, and for
the important caveat that this per-tool gate is not the only gate a given host applies;
each host's own `mcp` verb entry point has its own startup-level check, described in that
host's own `CLAUDE.md`. See `Mcp/CLAUDE.md` for the tools' filesystem-footprint and
permissions detail (not duplicated here).

## DiffPlexMergeEngine (the text-merge engine)

`Tools/DiffPlexMergeEngine.cs` is the sole text-merge engine — in-process, built on the
DiffPlex NuGet package (MIT-licensed, 1.9.0), needing no external binary. It builds its
own merge loop (`BuildMerge`) around `DiffPlex.ThreeWayDiffer.CreateDiffs` rather than
calling `ThreeWayDiffer.CreateMerge` directly, so it can intercept
`ThreeWayChangeType.Conflict` blocks itself.

**Whitespace-only auto-resolve.** A conflict whose two sides are equal once whitespace is
collapsed (joined-and-collapsed comparison over the classic ASCII whitespace set —
space/tab/CR/LF/form-feed/vertical-tab, deliberately narrower than .NET regex's
Unicode-aware `\s` so a genuine NBSP-vs-space difference isn't misclassified as
whitespace-only — never applied when either side has zero pieces, since a real deletion
must never be conflated with "the surviving side happens to collapse to empty too")
auto-resolves by taking the first mod's side verbatim, mirroring the retired KDiff3
engine's `--cs "WhiteSpace3FileMergeDefault=2"` behavior (confirmed against the KDiff3
source, preserved in `docs/decisions/kdiff3-retirement.md`).

**Conflict-marker sidecar.** A genuine conflict produces git/diff3-style conflict markers
(`<<<<<<< <mod1 name>` / `||||||| Vanilla` / `=======` / `>>>>>>> <mod2 name>`) written to
a **sidecar** file under `Paths.DiffPlexConflictsDirectory` (a dedicated top-level
`DiffPlexConflicts` folder, via `GetConflictMarkerPath`, keyed by an `XxHash32` of the
full output path plus filename) rather than to `outputPath` itself — writing to
`outputPath` would make `FileMerger`'s pre-merge `File.Exists(_outputPath)` overwrite
guard treat it as an already-completed merge and permanently skip retrying, since
`HeadlessMergeNotifier` always declines the overwrite prompt. The sidecar went through
two prior locations before landing here, each ruled out by direct end-to-end testing
rather than inspection alone: beside `outputPath` (`<outputPath>.conflict` — leaves
untracked litter inside the live mods tree, and a bundle-content conflict's `outputPath`
sits inside `Paths.MergedBundleContent`, which `WccLite.PackBundle` packs *wholesale*
with no filtering, risking a leftover `.conflict` file getting embedded into a
later-packed `blob0.bundle`); then under `Paths.TempBundleContent`, which fixed both of
those but broke for a third reason neither review nor unit tests caught —
`FileMerger.CleanUpTempFiles()` deletes the entire `TempBundleContent` tree wholesale at
the end of every headless run, so the sidecar was gone before a user could ever see it. A
conflict-marker file that later becomes an auto-solve on retry has its stale sidecar
deleted before writing the fresh output. Nothing automatically deletes the
`DiffPlexConflicts` directory itself (the same "accumulates until manually cleared"
property `TempBundleContent` has) — needs the same manual housekeeping between runs, just
without an automated sweep. `Paths.DiffPlexConflictsDirectory` is a relative path,
resolved against `Environment.CurrentDirectory` — each host sets that to
`AppContext.BaseDirectory` before dispatching to `merge`/`mcp` (see that host's own
`CLAUDE.md`), so in CLI/MCP mode sidecars land predictably next to the installed exe; the
WinForms host's GUI path never does that reset, so a GUI-mode conflict's sidecar lands
wherever the process's CWD happened to be at launch (typically the exe's own directory
for a normal double-click launch, but not guaranteed).

**The sidecar is opened for the user, not just written.** `MergeHeadless` writes the
sidecar, then (unless `openConflictMarkers` is `false`) calls `Tools/FileOpener.Open` (a
swappable static `Func<string, bool>`, defaulting to `Process.Start` with
`UseShellExecute = true` so it resolves the OS's file association) on the sidecar path,
and only then reports the skip via `AppState.Notifier.ShowMessage` — the open happens
*before* the message, since the message needs `FileOpener.Open`'s own bool return to say
whether the file actually opened. That bool distinguishes only "`Process.Start` succeeded"
from "`Process.Start` threw" — not a guarantee an editor actually came up; confirmed
empirically that on a machine with no file association for `.conflict`, `Process.Start`
still succeeds by launching the OS's own "how do you want to open this?" picker. `Merge()`
(interactive) just runs `MergeHeadless()` and maps `NeedsManualResolution` to `Failed`
since there's no UI here at all — the sidecar-write-and-open behavior fires identically
whether reached via the GUI's interactive path or the CLI/MCP headless path.
`MergeHeadless`'s `openConflictMarkers` parameter (default `true`) is the one thing that
differs by caller: `FileMerger.MergeTextHeadless` passes `openConflictMarkers: !dryRun`,
so a dry run still writes a genuine conflict's sidecar but never launches anything for it.
Deliberately not the WinForms host's existing `Program.TryOpenFile` helper: that helper's
non-`.exe` branch is a bare `Process.Start(path)` with no `UseShellExecute = true`, which
throws on modern .NET for a non-executable path (silently swallowed by that method's own
`catch`) — `Tools/FileOpener.cs` exists specifically so both hosts' paths can reach a
correctly-implemented version without Core referencing `System.Windows.Forms`.

**Vanilla-less guard.** A 3-way merge with no vanilla version at all (expected mainly on
the bundle-content path, when no matching vanilla bundle is found, but the guard applies
unconditionally to any conflict missing one) is refused outright
(`NeedsManualResolution`, nothing written) rather than attempted with an empty base
string — `DiffPlex.ThreeWayDiffer` degrades silently to zero diff blocks and a
"successful" empty merge in that case, confirmed empirically, which would otherwise
produce a truncated output file. This is a deliberate divergence from the retired KDiff3
engine, which had no equivalent guard and always attempted a real (if vanilla-less,
degraded 2-way) `--auto` merge instead — KDiff3 had a coherent notion of a 2-file merge
that DiffPlex's `ThreeWayDiffer`, as used here, does not.

## Compatibility constraint: DiffPlex's `ThreeWayDiffer` can produce inconsistent diff blocks

Confirmed as a genuine upstream library bug (DiffPlex 1.9.0), not a defect in this
repo's own merge loop: a throwaway scratch console app calling DiffPlex's own
`CreateMerge` directly — with both `LineChunker` (DiffPlex's own default and tested
chunker) and `LineEndingsPreservingChunker` (the one this engine actually uses) —
reproduced the identical failure on the identical input either way. When old-side and
new-side edits interleave/overlap relative to base in certain ways,
`CreateThreeWayDiffBlocks` can emit a block list whose `OldCount`/`NewCount` don't
actually correspond to the real `PiecesOld`/`PiecesNew` arrays. This surfaces two ways:
an outright `ArgumentOutOfRangeException`, or — confirmed via a minimal repro (base
`"a();/b();/c();"`, one side inserts a line after `a()`, the other independently changes
`b()` to `B()`) — no exception at all, but silently wrong output (content lost or
duplicated), because the running `oldIndex`/`newIndex` end up not matching
`PiecesOld.Count`/`PiecesNew.Count` even though no single block's own bookkeeping looked
wrong in isolation.

A large randomized stress test (100,000 total trials, varying edit density and file
length, run against the real, fixed `BuildMerge`) measured combined failure rates of
**0.35%** at one independent single-line edit per side on 50–200 line files (the closest
analogue to a typical two-mod `.ws` conflict), rising to **0.88%** (1–2 edits/side),
**2.65%** (2–3 edits/side), **4.99%** (1–6 edits/side on 50–200 line files), and
**38.89%** on the original dense adversarial case (1–6 edits/side on 1–19 line files) —
zero cases of any exception type other than the one this bug produces.

`BuildMerge` defends against both failure modes: the block-processing loop is wrapped in
`try`/`catch (ArgumentOutOfRangeException)`, and a post-loop check verifies
`oldIndex`/`newIndex` actually reached `PiecesOld.Count`/`PiecesNew.Count` (accounting for
a legitimate trailing-unchanged gap needing the exact same lockstep advance as the
per-block gap-catchup above it — an early, incorrect version of this check that skipped
that trailing advance produced a **~33% false-positive** "inconsistent" rate on
otherwise-benign inputs). Either failure mode throws
`DiffPlexMergeEngine.DiffAlgorithmException`, which `MergeHeadless` catches and reports as
`NeedsManualResolution` **without writing anything, including a conflict-marker
sidecar** — the marker content itself would have been built from the same untrustworthy
piece indices.

This measured, non-negligible failure rate at realistic edit density is a real
reliability gap the retired KDiff3 engine didn't share, and is the primary reason
retiring KDiff3 was a deliberate tradeoff, not a strict improvement — see
`docs/decisions/kdiff3-retirement.md`. Regression-tested via
`DiffPlexMergeEngineTests`'s `BuildMerge_InterleavedIndependentEdits_...`/
`MergeHeadless_InterleavedIndependentEdits_...` fixtures. **Do not "fix" this by
switching chunkers** — the bug reproduces under DiffPlex's own default/tested
`LineChunker` too, just at a somewhat lower rate, so switching would trade a real,
working byte-for-byte line-ending-preservation property for no actual safety gain.

## Text-merge input encoding

Vanilla `.ws` files are UTF-16LE with a BOM; mod authors' files are often plain
UTF-8/ASCII with no BOM (confirmed against real files on a live install). Mismatched
encodings can make an entire file read as unmatchable against its counterpart, turning a
false conflict into "needs manual resolution" that would otherwise have auto-solved
cleanly — empirically confirmed real-world case: `baseEffect.ws` failed to auto-solve
with mismatched encodings against the (now-retired) KDiff3 engine, and succeeded cleanly
once normalized, with correct merged output. `Tools/FileEncoding.cs` handles this:
`ReadAnyEncoding`/`WriteUtf16` give `DiffPlexMergeEngine` UTF-16LE normalization without
needing a temp file, since it merges in-process text directly (`EnsureUtf16File`, an
on-disk-copy variant for a file-based tool, has no in-repo caller since KDiff3's
retirement but is kept, still unit-tested, for any future file-based tool). **Never
normalize toward UTF-8** — the game may not load a merged `.ws` file in that encoding.
`ReadAnyEncoding` deliberately uses `File.ReadAllText(path)`'s built-in BOM
auto-detection rather than decoding raw bytes with a fixed `Encoding` instance — the
latter does not strip a detected BOM, leaving a stray U+FEFF glued to the first line,
confirmed empirically to reproduce the exact `baseEffect.ws`-style false conflict this
mechanism exists to avoid (see `WitcherScriptMerger.Tests`'s
`MergeHeadless_EncodingMismatch_...` fixture).

## Dependency validation

`Paths.ValidateTextMergeDependencies()` always returns `true` — `DiffPlexMergeEngine` is
in-process and needs no external binary (`DiffPlexMergeEngine.ValidateExePath()` always
returns `true` too). It's kept as a named method, rather than removed, because callers
(`WsmMcpTools`, `DependencyForm`) already call it by this name and it documents intent at
each call site. `Paths.ValidateBundleDependencies()` checks QuickBMS's exe + plugin and
wcc_lite's exe actually exist on disk. `Paths.ValidateDependencyPaths()` is just
`ValidateTextMergeDependencies() && ValidateBundleDependencies()`, kept for existing
callers that want the combined check.

The split exists so a host that only supports flat-file (`.ws`/`.xml`) conflicts —
`WitcherScriptMerger.Headless`, which has no QuickBMS/wcc_lite bundled at all — can gate
starting a scan/merge run on just the text-merge engine, without also requiring bundle
tooling it deliberately doesn't ship. Bundle-category conflicts still fail gracefully
per-conflict when attempted without QuickBMS/wcc_lite regardless of which gate a given
entry point uses (see `Tools/QuickBms.cs`'s `IsAvailable` and its callers,
`FileIndex/ModFileIndex.BuildAsync`, `Inventory/FileMerger.GetUnpackedFiles`) — this split
only changes what gates a *run starting at all*, not the per-conflict bundle behavior.
**Which gate a given entry point actually uses differs by host and by call** — see each
host's own `CLAUDE.md` for its own startup-level check; the MCP tools' own per-call gate
(`RequireDependenciesAndModsDirectory`, above) always uses the text-merge-only check
regardless of host.

## Hash format (`MergeInventory.xml`)

**Load-bearing.** `MergeInventory.xml` (including real, already-populated files on
developer machines) stores per-file hashes (`Tools/Hasher.cs`, xxHash32 via
`System.IO.Hashing`) compared by string equality to detect when a mod source file has
changed since it was last merged. Any change to `Hasher.cs` must produce byte-for-byte
identical output to the current implementation, or every existing recorded merge
silently "goes stale." `MergeInventory.HasResolvedConflict` re-checks these hashes on
refresh to detect merges made stale by upstream mod file changes. Verify any change with
the synthetic-edge-cases + real-recorded-hash cross-check pattern described in
`WitcherScriptMerger.Tests/CLAUDE.md`.

## Settings & persistence

- App settings: `AppSettings.cs` wraps `System.Configuration.ConfigurationManager` over
  `App.config`'s `<appSettings>` block (`Get<T>`/`Get`/`Set`/`Save`, cached
  `Configuration` object) — deliberately *not* `Properties.Settings` (removed during the
  SDK-style migration). Settings are cached and require an explicit `Save()` call.
- Merge history: `MergeInventory.xml`, via `XmlSerializer` (`Inventory/MergeInventory.cs`).
- Game load order: `LoadOrder/CustomLoadOrder.cs` reads the game's own `mods.settings`
  file.

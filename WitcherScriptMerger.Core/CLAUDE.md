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
  `MergeProgressInfo.cs`, `MergeInventoryHygiene.cs` (stale-record detection — see
  "Inventory hygiene" below).
- `LoadOrder/` — mod load-order logic: `CustomLoadOrder.cs`, `LoadOrderComparer.cs`,
  `LoadOrderValidator.cs`, `ModLoadSetting.cs`.
- `Tools/` — wrappers that shell out to bundled external executables, plus the sole
  text-merge engine: `QuickBms.cs`, `WccLite.cs`, `Hasher.cs`, `FileEncoding.cs`
  (UTF-16LE+BOM normalization — see "Text-merge input encoding" below),
  `DiffPlexMergeEngine.cs` (see below), `FileOpener.cs` (portable "open in the OS's
  default associated app" helper; the only call site is
  `DiffPlexMergeEngine.MergeHeadless`, opening a genuine conflict's marker sidecar),
  `ScriptUnitExtractor.cs`/`UnitAligner.cs`/`FunctionLevelMergeEngine.cs` (the
  function-level merge fallback — see "Function-level merge engine" below),
  `StaleBuildDetector.cs` (the pre-flight counterpart to that engine's
  vanilla-declaration invariant — see "Stale-build pre-flight" below).
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
caller overrides it via `defaultResult`. Its static `RouteAllOutputToStandardError` flag,
set by both hosts' `mcp` verb before the server starts, forces **every** line to stderr:
in MCP mode stdout carries the JSON-RPC frame stream, and a notifier line printed there
lands mid-stream and corrupts the transport — the client sees unparseable JSON rather
than a message from WSM. Ordinarily only `Error`/`Warning`/`Exclamation` route to stderr,
which meant any default-icon `ShowMessage` a scan can reach corrupted the stream:
`ModFileIndex.BuildAsync`'s "Can't find any mods in the Mods directory.", and the
disabled-mod skip notice (see below). Caught by a real `scan_conflicts` round-trip
returning a payload the client couldn't parse, with the notice spliced between two
protocol frames. CLI mode is unaffected — the flag stays `false` there. The WinForms host's `MainForm` implements this
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

## Config-extensible vanilla-DLC-folder allowlist (`AdditionalVanillaDlcFolderNames`)

`IsVanillaDlcBundleFolder` has a second overload,
`IsVanillaDlcBundleFolder(string path, IEnumerable<string> additionalFolderNames)` — the
single-arg overload now just forwards to it with `Array.Empty<string>()`. The two-arg
overload ORs the built-in regex match with an exact, case-insensitive match of the
extracted trailing folder-name segment against `additionalFolderNames`, populated at
`GetUnpackedFiles`' one call site by parsing the `"AdditionalVanillaDlcFolderNames"`
App.config setting (comma-separated, trimmed of whitespace and stray trailing directory
separators — the same parse shape `FileIndex/ModFileIndex.GetIgnoredModNames` already
uses for the sibling `"IgnoreModNames"` setting). This exists so a future DLC/expansion
whose folder codename isn't recognized by the built-in regex yet (e.g. CD Projekt Red's
"Songs of the Past", announced in 2026 with no public folder name at time of writing)
doesn't need a code change — just a config entry.

**Deliberately stays an exact-match allowlist, never existence-based auto-discovery.**
Vortex's own `witcher3dlc` mod type deploys ordinary user mods into
`<GameDir>\DLC\<modname>\content\...` — the identical on-disk shape as real vanilla DLC
content — so treating "any folder under DLC" as a vanilla merge baseline would risk
silently merging a conflict against a mod's own bundle instead of vanilla's. The two-arg
overload deliberately takes the extra names as a plain parameter rather than reading
`AppState.Settings` itself, keeping it a pure, static, directly unit-testable function
with no config/`AppState` dependency (settings are read exactly once, at the
`GetUnpackedFiles` call site) — see this file's own "AppState & IMergeNotifier" section
above for why touching `AppState.Settings` from code a test exercises is a real hazard.

**The built-in regex itself is matched against the extracted folder-name segment, not
the raw path, and is anchored at both ends (`^...$`).** An earlier version matched an
end-anchor-only pattern (`"(DLC[0-9]*|ep[0-9]|bob)$"`) against the full path — since
.NET `Regex.IsMatch` has no implicit start anchor, that matched *any* folder name merely
*ending* in one of those substrings, not just a folder name that *is* one of them
(optionally + digits): e.g. `"ImmersiveDLC"` or `"Step1"` would have incorrectly
qualified as vanilla. Caught in code review while adding the allowlist above, since it's
exactly the same collision-with-an-arbitrary-mod-folder-name risk the allowlist's own
"never auto-discovery" rule exists to prevent. Fixed by extracting the folder-name
segment once, up front, and running both the regex check and the allowlist check against
that same normalized value (an earlier version also normalized the two checks
inconsistently — full path for the regex, trimmed segment for the allowlist — a second,
related bug caught in the same review). Regression-tested via
`FileMergerTests.IsVanillaDlcBundleFolder_FolderNameMerelyEndsInPattern_ReturnsFalse`.

## The merged mod is excluded from the conflict scan

`ModFileIndex.BuildAsync` enumerates `Directory.GetDirectories(ModsDirectory, "mod*")`
and filters the result through `GetIgnoredModNames()`. That filter honors the
`IgnoreModNames` setting **and** always excludes the merged mod itself
(`MergedModName`, `mod0000_MergedFiles` by default).

Excluding it is not cosmetic. The merged mod is this tool's own *output*, but its
directory name starts with `mod`, so it matches the same glob as any source mod. Left in
the scan it becomes a merge input alongside the very mods it was built from, and each
subsequent run re-applies those mods' edits on top of already-merged text — **a
re-merge becomes cumulative instead of idempotent**. Inserted blocks accumulate one fresh
copy per run, and a losing most-distinct-from-vanilla tiebreak can additionally revert an
edit a previous run had kept. Confirmed on a real 249-mod install before the fix: a single
`modBloodAndSteel` insertion present 6× in `actor.ws` and a `modCriSlowMoCR` one 6× in
`damageManagerProcessor.ws` (each appears exactly once in the mod's own file), 37
duplicated mod-added lines across 11 of 42 merged files, and one `modTTMutagenSwap` edit
reverted outright. Nothing surfaced this as an error — the output stayed syntactically
valid and merged "successfully" every time, which is why it went unnoticed across
repeated merges.

The name-matching lives in `Paths.NormalizeMergedModName(string)` — a non-interactive,
argument-taking counterpart to `Paths.RetrieveMergedModName()`. The scan path must not use
the latter: it can prompt via `ConfirmInvalidModName` and message through
`AppState.Notifier`, neither of which may fire just because mod directories are being
enumerated. Both apply the same `Paths.MergedModNameMaxLength` (64) truncation, which is
what decides the directory name a merge actually writes — the two must agree or a scan
would fail to recognize the very directory the merge creates. `NormalizeMergedModName`
additionally trims, deliberately: its result is compared against a `DirectoryInfo.Name`,
which never carries surrounding whitespace.

`ModFileIndex.BuildIgnoredModNames(ignoreModNamesSetting, mergedModNameSetting)` is the
pure function behind `GetIgnoredModNames()`, split out so it's unit-testable without
touching `AppState.Settings` — see `WitcherScriptMerger.Tests/CLAUDE.md`'s
"`AppState.Settings`-safety constraints" and `FileIndex/ModFileIndexTests.cs`.

## Disabled mods are excluded from the conflict scan

`ModFileIndex.BuildAsync` also drops any mod folder that `mods.settings` marks
`Enabled=0`, via `ExcludeDisabledMods` → `CustomLoadOrder.IsModDisabledByName`.

A scan is a filesystem glob over `Paths.ModsDirectory`, so before this a deployed-but-
disabled mod looked exactly like an active one and its files counted as full conflict
participants. That is not a cosmetic over-count — **a disabled mod can make a conflict
permanently unmergeable**. Observed live: a disabled `modFearlessRoach` ships a
pre-next-gen whole-file copy of `game\vehicles\horse\states\exploration.ws` (missing
`CheckVector`/`DoHorseKick`/`OnHorseKick` plus member declarations), which trips
`FunctionLevelMergeEngine`'s vanilla-declaration invariant, so every single run reported
"needs manual resolution" for a file the game was never going to load that mod's version
of anyway. On that install the change took the conflict count from 44 to 43, with the
remaining 43 all resolving cleanly.

**Only an explicit `Enabled=0` excludes a mod.** `IsModDisabledByName` returns `false`
for a mod absent from `mods.settings` entirely, which is the correct reading — the game
appends unknown mod folders on next launch, enabled. A missing or unreadable
`mods.settings` likewise disables nobody (`CustomLoadOrder.Refresh` leaves `Mods` empty),
so this can never make a scan miss conflicts on a fresh install or on a Linux host with
no `Documents\The Witcher 3` at all.

The `MergeDisabledMods` App.config setting opts out (scan every deployed mod regardless),
the useful case being pre-merging a mod that's staged but not switched on yet. It is
deliberately named for the opt-**out** so that its absence from an older `App.config`
yields the new, wanted behavior: `AppSettings.Get<bool>` returns `false` for a key that
isn't there. Skipped folders are reported through `AppState.Notifier` and exposed as
`ModFileIndex.DisabledModsSkipped` (and the MCP `merge_conflicts` tool's
`disabledModsSkipped`) — never silent, since a conflict disappearing from a scan should
be visible rather than inferred from a changed count.

`ModFileIndex.ExcludeDisabledModPaths(paths, isModDisabled, out skipped)` is the pure
function behind it, split out to be unit-testable without `AppState.Settings` or a real
`mods.settings` — same shape and same reason as `BuildIgnoredModNames` above.

## Stale-build pre-flight (`Tools/StaleBuildDetector.cs`)

The pre-flight counterpart to `ValidateWholeFileMergeOutput`'s vanilla-declaration
invariant (see "Function-level merge engine" above). That invariant is the safety net —
it fires *after* a merge has produced output that would have dropped vanilla
declarations, and its message correctly guesses the cause ("...usually means that mod
ships a whole-file copy taken from an older game build"). But by then the user is looking
at a "Skipped — needs manual resolution" line, with the actionable fact (which mod is out
of date, and that the remedy is to update or disable *that mod* rather than hand-merge a
2500-line file) buried inside a sentence about a DiffPlex bug.

`FindMissingVanillaDeclarations(vanillaText, modText)` runs the same comparison up front,
straight off the files with no merge involved: every `ScriptUnitExtractor` scoped name the
installed vanilla file has that a given mod's copy does not. Using the extractor's own
`ScopedName` identity is what makes a pre-flight warning and a post-hoc violation name the
same thing. An unscannable file on either side yields **no** finding — returning an empty
set instead would make every vanilla declaration look missing and turn one malformed mod
file into a wall of false warnings.

**A diagnostic, never a gate.** Nothing here changes what does or doesn't merge: a mod
*may* legitimately delete a vanilla function, and the check runs before any merge so it
cannot know whether this particular conflict will actually fail. The message says so —
it reports the drift and its usual consequence rather than asserting an outcome. That
distinction is load-bearing: on a real install one mod was missing 1 of 224 declarations
and its conflict auto-solved every time, while another missing 13 of 224 reliably tripped
the invariant.

Surfaced via both hosts' `merge` CLI output (`stale mod build:` lines), the MCP
`scan_conflicts` tool's per-conflict `staleBuildWarnings`, and `merge_conflicts`'s
top-level `staleBuildWarnings`. Only `.ws` conflicts are examined — `ScriptUnitExtractor`
is WitcherScript-specific, exactly as the function-level rescue itself is gated — and the
merged-mod folder is skipped, since it routinely appears among a conflict's sources once
a file has been merged once and an "older game build" verdict on this tool's own output
would be meaningless.

## Inventory hygiene (`Inventory/MergeInventoryHygiene.cs`)

Three staleness rules the WinForms GUI has always applied in
`MainForm.RefreshMergeTree()` — merged file missing, source mod file missing, source mod
disabled — lifted out as pure, promptless predicates so the headless CLI, the MCP tools
and the Vortex extension see the same staleness the GUI does.

**`MergeInventory.HasResolvedConflict` now requires the merged output to exist**
(`HasMergedFile`). Without that check the record was self-certifying: every hash it
verifies belongs to a *source* mod, all of which are present and unchanged when it's the
*output* that has been deleted, so it answered "resolved" forever and nothing ever
re-merged. Observed live for `game\vehicles\horse\states\exploration.ws` — record
present, merged file absent, `alreadyResolved: true` on every scan, and the game
therefore loading exactly one of the two conflicting mods with nothing indicating a
problem.

Bundle-content records are exempt from that existence check: their `GetMergedFile()`
resolves under `Paths.MergedBundleContent`, which is working-directory-relative scratch
space cleared between runs, so absence there says nothing about whether the merge is live
— the real artifact is the packed bundle. Only flat files have a stable, absolute output
path under the mods directory that "missing" is meaningful for, and flat files are the
only category either headless host supports anyway.

**Findings, not actions.** Deciding what to *do* about a stale record stays the caller's:
the GUI asks the user (`ConfirmPruneMissingMergeFile` and friends), headless callers
report it (`stale merge record:` CLI lines, `list_merges`'s `mergedFileExists` /
`staleWarnings`). That split is deliberate rather than lazy — the GUI's three prompts all
pass no `defaultResult`, so `HeadlessMergeNotifier` would answer its generic
`YesNo => No` to every one of them and prune nothing, silently. Same defect shape as the
`ConfirmOutputOverwrite` finding in `docs/bugs/function-level-merge-gap-handling.md`.

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
`scan_conflicts` (each conflict additionally carries `staleBuildWarnings`),
`merge_conflicts` (optional `relativePaths`/`orderOverrides`/`dryRun`/`overwrite`;
returns `{merged, skipped, unmatched, dryRun, overwrite, functionLevelDecisions,
staleBuildWarnings, disabledModsSkipped}`), `get_status`, `list_merges` (each record
additionally carries `mergedFileExists` and `staleWarnings`) — all
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

## Function-level merge engine

`Tools/ScriptUnitExtractor.cs`, `Tools/UnitAligner.cs`, and
`Tools/FunctionLevelMergeEngine.cs` are a **fallback**, activated only from inside
`DiffPlexMergeEngine.MergeHeadless` at the two points where the whole-file merge has
already failed for a given pairwise chain step (the `DiffAlgorithmException` catch and
the `HasConflicts` branch) — never a parallel code path, so every conflict that already
auto-solves via the whole-file engine is unaffected. The idea: split vanilla and both
sides of a pairwise merge into individual function/field units, resolve each
independently, then reassemble — most of a real `.ws` file's line-level "conflict"
surface comes from whitespace/comment noise around a handful of actually-edited
functions, not from genuine overlapping logic changes, and per-function merging sidesteps
that noise entirely (and, as a side effect, mitigates `DiffAlgorithmException`'s
worse-at-small-inputs failure rate above by attempting DiffPlex's inline 3-way merge at
function granularity only when both sides changed a given function differently, not on
the whole file at once).

**Validated empirically before being built, not just unit-tested.** A throwaway
measurement against a real, live Witcher 3 install's `actor.ws` conflict (vanilla +
6 real overhaul mods) found only 6 of 395 functions were genuine two-mod collisions
(both sides edit the same function differently) — confirming per-function decomposition
was worth building rather than just relocating the same conflict into a smaller,
statistically more `DiffAlgorithmException`-prone box. A follow-up chain-step replay
against the same real install's 5 currently-unresolved conflicts found that a
naively insertion-only alignment (tolerating a mod adding new functions, but declining
outright the moment any mod deleted a vanilla function) would rescue only 2 of the 5 —
one real mod in that install deletes several vanilla functions outright, and that
deletion was found to persist through the merge chain into later steps even where the
deleting mod isn't a direct input, since an earlier *clean* whole-file merge step
faithfully propagates a one-sided deletion into the accumulated text. `UnitAligner`
handles insertions and deletions symmetrically (an LCS alignment of each side's unit
names against vanilla's) for this reason; final validation against the same 5 real
files, run against an isolated copy (never the live install directly), got all 5 to
merge successfully, including `actor.ws` itself.

**Function identity is name-only** (`(scope-free) name`, no parameter signature) —
confirmed empirically against several large real vanilla files (`actor.ws`, `player.ws`,
`npc.ws`) that WitcherScript function names don't collide within a file in practice, so
overload-aware identity wasn't needed.

**Extraction (`ScriptUnitExtractor`)** is a brace/paren-matching tokenizer, not a full
parser or a binding to the third-party `tree-sitter-witcherscript` grammar — confirmed
via direct research into WitcherScript's grammar that class/state/struct/enum
declarations are top-level only (never nested) and the language has no nested
function-like constructs at all (no lambdas, local functions, or closures), so a
function body only ever gains brace depth from control flow, never another function
declaration. That structural simplicity is what makes plain brace/paren counting
sufficient, as long as it's string/comment-aware (a single masking pass shared by both
the brace-safe extraction path and the public `StripComments` helper) so a brace or
paren inside a string literal or comment can never be mistaken for real syntax.
`ScriptUnitExtractor` itself does no file I/O at all — `Extract`/`StripComments` take
already-read `string` text — encoding normalization is the caller's job:
`DiffPlexMergeEngine.MergeHeadless` reads via `Tools/FileEncoding.cs` before ever
reaching this class, the same `ReadAnyEncoding` call every other text-merge path already
uses (mod files are inconsistently encoded even though vanilla is always UTF-16LE+BOM —
see "Text-merge input encoding" below). Any future caller that reaches `Extract`/
`StripComments` directly with raw file bytes, rather than through that existing
encoding-normalized path, would need to normalize first itself.

**Per-function resolution (`FunctionLevelMergeEngine.TryMerge`)** tries cheap one-sided
shortcuts (unchanged, only-one-side-edited, both-sides-made-the-identical-edit) before
ever calling `DiffPlexMergeEngine.BuildMerge` — only a function genuinely edited
differently on both sides reaches a real per-function 3-way merge attempt, falling back
to a whole-function tiebreak (**most distinct from vanilla wins**, scored via
`DiffPlex.Differ`'s plain 2-way line diff over comment-stripped, whitespace-ignored text
— deliberately not the buggy `ThreeWayDiffer`) if that merge attempt itself conflicts or
throws `DiffAlgorithmException`. A vanilla function deleted on one side and edited on the
other resolves as **edit wins** (a deletion never silently overrides a surviving edit —
losing code silently is unrecoverable if the deletion was wrong, keeping an unwanted
edit is not) — every non-mechanical resolution (a tiebreak, an edit surviving a
competing deletion, a mod's gap comment not making it into the output) is recorded in a
`Decisions` audit trail, never applied silently. That trail is threaded all the way out:
`DiffPlexMergeEngine.LastFunctionLevelDecisions` → `FileMerger`'s
`HeadlessMergeSummary.FunctionLevelDecisions` → both hosts' CLI output and the MCP
`merge_conflicts` tool's `functionLevelDecisions` field. Not yet surfaced in the WinForms
GUI's `MergeReportForm` — a deliberate deferral, not an oversight, since that's UI work
on the host side rather than engine work here.

**Scope note on non-unit content ("gaps"), revised by gap-handling v2** (see
`docs/bugs/function-level-merge-gap-handling.md` for the two real, compile-breaking
defects the original design produced on a live install):

- **Plain member declarations are units now, not gap content.** `[specifiers] var a, b
  : T;`, `default x = value;`, and `autobind` declarations extract as
  `ScriptUnitKind.MemberDeclaration` units, so a mod adding/editing one participates in
  per-unit resolution instead of being silently reverted to vanilla's gap text (that
  revert dropped mod-added declarations while the code referencing them survived —
  defect 2). Unit identity is **scope-qualified** (`ScriptUnit.ScopedName`,
  `"CR4Player::mCSMCR"`, states as `"Combat@CR4Player::phase"`) via a top-level
  type-range prescan, because member names — unlike function names — recur across the
  several classes a real `.ws` file contains; `UnitAligner` matches on `ScopedName`.
- **Insertion slots emit the inserting side's own span.** For a slot where exactly one
  side inserts units, reassembly emits that side's contiguous text (its gaps + inserted
  units, verbatim) between its two anchor units, instead of vanilla's gap followed by
  bare concatenated unit texts — vanilla's gap can contain a class-closing brace, and
  the old emission placed mod-added class members *after* it, at global scope, with
  their separators eaten (defect 1). When the span's anchors aren't well-defined (a
  neighboring vanilla unit deleted on the inserting side) or both sides insert at the
  same slot, emission falls back to vanilla-gap-plus-line-break-synthesized units —
  unless the gap carries a structural brace, in which case the file **declines**
  (placement would be a guess).
- **A post-reassembly sanity gate** (`PassesReassemblySanityGate`, public so external
  validation tooling can reuse it) walks the output's structural mask and declines the
  rescue on any member-shaped declaration at brace depth 0 (an access modifier,
  `default`, or `var`/`autobind` line — invalid WitcherScript, the exact
  "`'public' has no sense for global function`" class of compile error), on negative
  or nonzero final brace depth, or on output that no longer scans cleanly. Validated
  against the real broken output preserved in `docs/bugs/artifacts/` (gate fails it on
  exactly the orphaned accessor the game rejected) with zero false positives across
  real vanilla `r4Player.ws`/`player.ws`/`actor.ws`/`baseEffect.ws`.
- **A vanilla declaration kept by either side must survive the whole-file merge**
  (`ValidateWholeFileMergeOutput`'s lost-unit check). This extends the function-level
  engine's own long-standing "a deletion never silently overrides a surviving edit"
  principle (above) to the whole-file path, which did not previously enforce it: a unit
  present in vanilla, kept by one input and absent from the other used to be allowed to
  vanish, on the reading that it was "a legitimate deletion propagating". From text alone
  that shape is **indistinguishable** from the case that actually causes damage — a mod
  shipping a whole-file copy taken from an older game build simply has no copy of
  declarations vanilla added since, and a three-way diff reads that absence as a deletion.
  Observed live on a next-gen install: a pre-4.0 `r4Game.ws` erased
  `CR4Game.OnHDRChangedEvent` (engine-called, calls `GetGuiManager().OnHDRChanged()`) from
  the merged output; the merge reported success, the scripts compiled, and the game
  rendered its menu background with **no main menu at all**. Two other mods in the same
  load order did the same to `mapMenu.ws` (`OnFiltersChanged`, `SetInitialFilters`,
  `m_fxSetInitialFilters`) and `exploration.ws` (`CheckVector`, `DoHorseKick`,
  `OnHorseKick` plus five member variables) — in every case the declaration was present in
  vanilla *and* in the other contributing mod. Since the two cases can't be told apart,
  this errs toward the survivable one: the deliberate-deletion mod loses its deletion,
  rather than an engine-called vanilla event disappearing with nothing saying so. A
  violation doesn't skip the file — it routes to the function-level rescue first, like
  every other violation, and that engine's own edit-survives-competing-deletion policy
  usually keeps the unit. `ValidateWholeFileMergeOutput` takes optional
  `oldDescription`/`newDescription` purely so the message can **name the mod that lacks
  the declaration**, since the fix is almost always "that mod is built for an older game
  version". Only a name *both* sides dropped may still disappear.
- A gap that still exists between two intact units is compared as before —
  whitespace-tolerant, deliberately NOT comment-stripped — producing a `Decisions` note
  when content differs; vanilla's gap text still wins at non-insertion slots.

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

Both hosts' `merge` CLI verbs now use the text-merge-only gate. The WinForms host's verb
previously used the combined `ValidateDependencyPaths()`, which made `merge` refuse to
start at all on an install whose conflicts were entirely flat-file (`.ws`/`.xml`) — the
common case, and the only category either headless path can resolve anyway. Since neither
QuickBMS nor wcc_lite is committed to this repo (see the root `CLAUDE.md`), a plain
clone-and-run hit that refusal every time, with an error message pointing at the GUI's
dependency setup for tooling the run didn't actually need. `ValidateDependencyPaths()` is
still there for callers that genuinely want the combined check.

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

### Vortex-managed sidecar config (`WitcherScriptMerger.exe.config`)

`GetRawValue` resolves a key in three steps, first non-blank wins:

1. `WSM_<key>` environment variable (`GetEnvironmentOverride`).
2. Our own config — `<AssemblyName>.dll.config`, via `ConfigurationManager`.
3. **The Vortex-managed sidecar**, `AppSettings.VortexSidecarFileName`
   (`WitcherScriptMerger.exe.config`) beside the entry assembly.

Step 3 exists because Vortex's bundled `game-witcher3` extension both **reads and writes**
a script-merger config under the .NET Framework `<exe>.exe.config` name this project
stopped using at the .NET 10 modernization. It parses that file for `MergedModName`
(`scriptmerger.ts::getMergedModName`) and writes `GameDirectory`,
`VanillaScriptsDirectory` and `ModsDirectory` into it when configuring a merger install
(`scriptmerger.ts::setMergerConfig`). Without this fallback the two never meet: Vortex
writes a file WSM never reads, so a user who "configures WSM through Vortex" changes
nothing at all — and Vortex logs `failed to ascertain merged mod name - using
"mod0000_MergedFiles"` and silently falls back to a hardcoded guess.

**A fallback, not an override**, deliberately: a non-blank value in our own config is an
explicit choice (the GUI's settings screen writes there via `Set`/`Save`, and Vortex never
writes `MergedModName`), so it must win. The sidecar only fills in what we would otherwise
have to derive — which is exactly the shape of the three keys Vortex writes, since
`GameDirectory`/`ModsDirectory`/`VanillaScriptsDirectory` all ship blank meaning "derive
from the working directory". Env overrides still beat both.

`ParseAppSettingValue(xml, key)` is a pure static over the file's text — no filesystem, no
`AppState` — so it's directly unit-testable (`AppSettingsTests`); it returns `null` for
anything it can't confidently read (malformed XML, missing key, blank value) so every
caller falls through to existing behavior instead of acting on a half-parsed file. The
read is cached after the first attempt and never throws or prompts: it runs inside every
settings read, including scan paths where an exception would surface as a merge failure.
`Settings[key]` is dereferenced with `?.` so a key present *only* in the sidecar still
resolves rather than throwing first.

The WinForms host's csproj emits this file at build and publish (never overwriting an
existing one — Vortex owns it once written); see `WitcherScriptMerger/CLAUDE.md`.
`WitcherScriptMerger.Headless` deliberately does not, since Vortex's extension only ever
looks for a merger named `WitcherScriptMerger.exe`.

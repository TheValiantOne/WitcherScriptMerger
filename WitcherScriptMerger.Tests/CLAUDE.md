# CLAUDE.md — WitcherScriptMerger.Tests

Guidance for working in `WitcherScriptMerger.Tests` (`net10.0`, xunit). This is the
only test project in the repo, and it covers `WitcherScriptMerger.Core` only — it does
**not** reference either host project (no WinForms). Run with
`dotnet test WitcherScriptMerger.Tests/WitcherScriptMerger.Tests.csproj` (or
`dotnet test WitcherScriptMerger.sln`). See the root `CLAUDE.md` for overall project
context and `WitcherScriptMerger.Core/CLAUDE.md` for what the code under test actually
does.

## What's covered

- `Tools/DiffPlexMergeEngineTests.cs` — `DiffPlexMergeEngine`'s `BuildMerge`/`Merge`/
  `MergeHeadless`: whitespace-only auto-resolve, conflict-marker sidecar behavior, the
  vanilla-less guard, and the `DiffAlgorithmException` defense (`BuildMerge_
  InterleavedIndependentEdits_...`/`MergeHeadless_InterleavedIndependentEdits_...` —
  regression tests for the confirmed upstream DiffPlex bug documented in Core's
  `CLAUDE.md`).
- `Tools/FileEncodingTests.cs` — `FileEncoding`'s UTF-16LE normalization, including the
  `MergeHeadless_EncodingMismatch_...` fixture reproducing the `baseEffect.ws`-style false
  conflict that motivated it.
- `Tools/HasherTests.cs` — `Hasher`'s xxHash32 output, including synthetic edge cases.
- `Tools/ScriptUnitExtractorTests.cs` — `ScriptUnitExtractor`'s function-level merge
  splitter: round-trip fidelity (`Reassemble(Extract(x)) == x`) across annotations,
  forward declarations, nested control-flow braces, string/comment-embedded braces, and
  `ExtractionException` on malformed input — see Core's `CLAUDE.md`'s "Function-level
  merge engine" section.
- `Tools/UnitAlignerTests.cs` — `UnitAligner`'s vanilla-vs-one-side LCS alignment:
  matches, insertions, deletions, and both at once.
- `Tools/FunctionLevelMergeEngineTests.cs` — `FunctionLevelMergeEngine.TryMerge`: every
  one-sided shortcut, a genuine collision resolved by `BuildMerge`, the
  most-distinct-from-vanilla tiebreak (including its deterministic tie-break and a
  scaled-down `DiffAlgorithmException` case), edit-survives-competing-deletion, insertion
  reconciliation (including the same-name-different-body decline case), and gap-comment
  detection. Also `ValidateWholeFileMergeOutput`'s lost-unit check, including the
  deliberately reversed expectation for a vanilla declaration kept by one side and
  absent from the other (now a violation, not "a legitimate deletion propagating" -
  see Core's `CLAUDE.md`), that a name dropped by *both* sides is still allowed to go,
  and that the violation message names the stale mod on whichever side it is.
- `Inventory/FileMergerTests.cs` — `FileMerger.IsVanillaDlcBundleFolder`: known vanilla
  DLC-folder names, case-insensitivity, and non-matches (including anchoring) — see
  Core's `CLAUDE.md`'s "Vortex-fork parity fixes" section. Also covers the two-arg
  `IsVanillaDlcBundleFolder(path, additionalFolderNames)` overload backing the
  `AdditionalVanillaDlcFolderNames` config allowlist: extra-name matches (including
  case-insensitivity and a trailing path separator), the empty-list case still matching
  everything the regex alone matches, an extra name absent from the list still returning
  `false` (no accidental wildcard), a `null` extra-names list degrading to regex-only
  instead of throwing, and a regression case for a real folder-name-anchoring bug caught
  in code review (a folder name merely *ending* in a recognized substring, e.g.
  `"ImmersiveDLC"`/`"Step1"`, must not match) — see Core's `CLAUDE.md`'s "Config-extensible
  vanilla-DLC-folder allowlist" section.
- `FileIndex/ModFileIndexTests.cs` — `ModFileIndex.BuildIgnoredModNames`, the pure
  function behind the mod-directory filter `BuildAsync` applies to its `"mod*"` glob.
  Regression coverage for the merged mod being scanned as an ordinary source mod, which
  made every re-merge cumulative rather than idempotent (duplicated insertions, and
  occasionally a reverted edit) — see Core's `CLAUDE.md`'s "The merged mod is excluded
  from the conflict scan" section for the mechanism and the real-install evidence. Covers
  the exclusion happening with no `IgnoreModNames` configured at all (the bug itself), a
  non-default `MergedModName`, user entries surviving alongside it, case-insensitive
  de-duplication when the merged mod is already listed by hand (the pre-fix workaround),
  a blank/unconfigured `MergedModName` adding no phantom entry, and the
  `Paths.MergedModNameMaxLength` truncation and whitespace-trimming that keep the excluded
  name equal to the directory name a merge actually writes.
- `LoadOrder/CustomLoadOrderTests.cs` — `CustomLoadOrder.ProcessLine`'s tolerance for
  `mods.settings` "VK=" (VortexKey) lines, via reflection — see Core's `CLAUDE.md`'s
  "Vortex-fork parity fixes" section.
- `Tools/KDiff3CrossCheckTests.cs` — an auto-solvable-only A/B check of
  `DiffPlexMergeEngine` against a real `KDiff3.exe` binary, when a developer happens to
  have one locally (WSM no longer bundles or requires KDiff3 itself — see
  `docs/decisions/kdiff3-retirement.md`).
- `AppSettingsTests.cs` — `AppSettings`'s `WSM_<key>` environment-variable override, and
  (added alongside the Vortex sidecar interop) `AppSettings.ParseAppSettingValue`: the pure
  parser behind the `WitcherScriptMerger.exe.config` fallback Vortex's bundled
  `game-witcher3` extension reads and writes. Covers key lookup, case-sensitivity, a blank
  value and a missing key both yielding `null` (which is what makes `GetRawValue`'s `??`
  fall through correctly), malformed/truncated/empty XML degrading to `null` rather than
  throwing, null/blank inputs, and that `VortexSidecarFileName` still matches the name
  Vortex hardcodes — see Core's `CLAUDE.md`'s "Vortex-managed sidecar config" section.
- `LiveInstall.cs` — see "Live-install cross-check tests" below.

## `AppState.Settings`-safety constraints

Tests never construct `FileMerger.MergeSource` via `MergeSource.FromFlatFile`/
`FromBundle` (both call `ModFile.GetModNameFromPath` → `Paths.ModsDirectory` →
`AppState.Settings`) or otherwise force `AppState.Settings` to construct outside a real
GUI/CLI/MCP entry point. `AppSettings`'s constructor calls `Environment.Exit(1)` if it
can't find a config file next to `Assembly.GetEntryAssembly().Location`, and in a
`dotnet test` host (`testhost.dll`, no matching `.config`) that kills the entire test
process, not just one test.

`AppState.Settings` is a lazy property specifically so that merely touching
`AppState.Notifier` (which Core code — e.g. `DiffPlexMergeEngine`'s headless skip/guard
messages — legitimately does on its own) doesn't also force `Settings` to construct; see
`WitcherScriptMerger.Core/CLAUDE.md`'s "AppState & IMergeNotifier" section for the full
mechanism. `Paths.cs`'s own properties
(`ScriptsDirectory`/`ModsDirectory`/`IsScriptsDirectoryDerived`/`IsModsDirectoryDerived`)
read `AppState.Settings.Get(...)` on every access rather than caching the result via a
static field initializer, for the identical reason one layer further out — a field
initializer there would've forced `Settings` to construct merely from touching an
unrelated static member of `Paths` (e.g. `GetRelativePath`), via C#'s `beforefieldinit`
semantics, silently undermining `AppState.Settings`'s own laziness.

`Tools/DiffPlexMergeEngine.GetConflictMarkerPath` reads only the compile-time-literal
`Paths.DiffPlexConflictsDirectory` const, which never triggers `Paths`'s type initializer
at all, so it's safe to call from tests unconditionally. Tests that need a
`FileMerger.MergeSource` build it directly via object-initializer syntax instead (its
fields are all public) rather than going through `FromFlatFile`/`FromBundle`.

## Live-install cross-check tests (`WSM_TEST_GAME_DIR`)

A few tests optionally cross-check against a real Witcher 3 + WitcherScriptMerger
install — a live `MergeInventory.xml`'s recorded hashes, or a real `KDiff3.exe` binary a
developer happens to have locally (for `KDiff3CrossCheckTests.cs`'s auto-solvable-only
A/B check against `DiffPlexMergeEngine`) — via `LiveInstall.cs`, gated entirely on the
`WSM_TEST_GAME_DIR` environment variable (unset by default). This is **never** a
hardcoded or scanned path, per the repo's "scrub machine-specific paths" rule (see
`CONTRIBUTING.md`). These tests silently no-op when the variable is unset.

## Beyond what this project covers

For anything not covered here — especially further hash-output or `MergeInventory.xml`
schema changes — the precedent set in the (local, gitignored) `HANDOFF.md` still applies:
a disposable, non-committed `dotnet new console` scratch app, exercising synthetic edge
cases plus a cross-check against a real value already recorded in a live
`MergeInventory.xml`. Follow that pattern rather than assuming API docs alone are
sufficient, particularly for anything hash- or serialization-related — see Core's
`CLAUDE.md`'s "Hash format" section for why that's load-bearing.

# "Manual merges are needed" that never stops recurring

**Status:** fixed (this batch)
**Found:** 2026-08-25, WSM 0.7.0 (`WitcherScriptMerger.Headless mcp` → `scan_conflicts` /
`merge_conflicts`), against a live 350-mod, game-build-4.04 install
**Severity:** medium — no data loss, but three conflicts were reported as needing manual
resolution on every single run, one of them permanently invisible to any headless caller
**Component:** `WitcherScriptMerger.Core/FileIndex/ModFileIndex.cs`,
`Inventory/MergeInventory.cs`, `HeadlessMergeNotifier.cs`

## Summary

A merge run reported three files as "needs manual resolution", every time, with no way to
make it stop. The merge engine was **behaving correctly** in all three cases — it was
refusing to reproduce real damage — but nothing said which mod to fix, and two separate
gaps guaranteed the same three files came back on the next run.

| File | Blocking mod | Enabled? | State of merged output |
|---|---|---|---|
| `game\vehicles\horse\states\exploration.ws` | `modFearlessRoach` | **no (`Enabled=0`)** | **none at all** |
| `game\gui\menus\mapMenu.ws` | `modFastTravelFromAnywhere` | yes | 4 days stale |
| `game\r4Game.ws` | `modAlwaysFullExp` | yes | 4 days stale |

All 41 other conflicts in the same run merged cleanly. The three that didn't were exactly
the three whose source mods ship a whole-file copy of a vanilla script taken from an
**older game build** — the `ValidateWholeFileMergeOutput` case documented in
`WitcherScriptMerger.Core/CLAUDE.md`'s "Function-level merge engine". Verbatim from a dry
run:

```
[Skipped] Skipped modalchemyrequiresmeditation + modAlwaysFullExp: the whole-file merge
silently corrupted content ('CR4Game::OnHDRChangedEvent' is declared in the vanilla file
and kept by modalchemyrequiresmeditation, but is missing from the merged output (lost) -
modAlwaysFullExp has no copy of it, which usually means that mod ships a whole-file copy
taken from an older game build) and the function-level fallback declined. Needs manual
resolution - open the source mod files directly to compare and resolve.
```

That diagnosis is correct, and the invariant is right to decline: taking the stale mod's
side would delete vanilla code the game and the other contributing mod still call. The
already-deployed `r4Game.ws` (merged by a pre-invariant engine build) confirmed it —
`OnHDRChangedEvent` was already missing from the live merged file.

## Why it kept recurring

### 1. The scan ignored `mods.settings`

`ModFileIndex.BuildAsync` globbed `Mods\mod*` off disk, filtered only by
`IgnoreModNames`. `modFearlessRoach` is deployed but `Enabled=0` — the game never loads
it, so it cannot really conflict with anything. WSM counted it anyway, tried to merge its
1.3x-era `exploration.ws` (2447 lines vs vanilla's 2734, missing `CheckVector` /
`DoHorseKick` / `OnHorseKick` and five member declarations), failed the invariant, and
reported "needs manual resolution" — forever. One of the three was a pure phantom.

Fixed: `ExcludeDisabledMods` (see Core's `CLAUDE.md`, "Disabled mods are excluded from
the conflict scan"), opt-out via `MergeDisabledMods`.

### 2. `HasResolvedConflict` never checked that the merged output exists

The record for `exploration.ws` claimed the conflict was resolved while no merged file
existed at all. Every hash it verifies belongs to a *source* mod — all present and
unchanged — so the record was self-certifying: `alreadyResolved: true` on every scan,
never re-merged, and the game silently loading exactly one of the two conflicting mods.

The WinForms GUI already refused to trust such a record (`MainForm.RefreshMergeTree` →
`ConfirmPruneMissingMergeFile`), along with two sibling rules for a missing or disabled
source mod. **All three were GUI-only**, so the CLI, the MCP tools and the Vortex
extension never saw them.

Fixed: `MergeInventoryHygiene` + the existence check in `HasResolvedConflict` (see Core's
`CLAUDE.md`, "Inventory hygiene"). Note the GUI's prompts all pass no `defaultResult`, so
hoisting that code as-is would have had `HeadlessMergeNotifier` answer its generic
`YesNo → No` and prune nothing, silently — the same defect shape as the
`ConfirmOutputOverwrite` finding in `function-level-merge-gap-handling.md`. The headless
rules therefore *report*; only the GUI acts.

### 3. Nothing named the mod to fix

"Needs manual resolution" sent the user toward hand-merging a 2500-line script. The real
remedy was to update or drop one mod — and each mod's *actual* intended change turned out
to be tiny: `modAlwaysFullExp` is **2 lines** (`expModifier = 1.0f;` plus one buff call)
wrapped in a copy missing 68 lines of current vanilla; `modFastTravelFromAnywhere` is 15
lines, missing 28.

Fixed: `Tools/StaleBuildDetector.cs`, a pre-flight run of the same comparison the
invariant makes post-hoc, surfaced in `scan_conflicts`, `merge_conflicts` and both hosts'
CLI output. It is a diagnostic, not a gate — see Core's `CLAUDE.md`, "Stale-build
pre-flight", for why its message deliberately stops short of predicting failure.

## Found while fixing: notifier output corrupts the MCP transport

`HeadlessMergeNotifier.Write` routed only `Error`/`Warning`/`Exclamation` to stderr;
everything else went to **stdout**, which in `mcp` mode carries the JSON-RPC frame stream.
The new disabled-mod notice (default icon) duly appeared spliced between two protocol
frames in a real `scan_conflicts` round-trip, leaving the client with unparseable JSON.

Pre-existing, not introduced here — `ModFileIndex.BuildAsync`'s "Can't find any mods in
the Mods directory." had the same shape. Fixed at the root with
`HeadlessMergeNotifier.RouteAllOutputToStandardError`, set by both hosts' `mcp` verb.

## Verification

Against the live install, before and after:

- **Before:** 44 conflicts, 3 unmergeable, 1 inventory record with no output file.
- **After:** 43 conflicts (the disabled-mod phantom gone), **43/43 resolved, 0 skipped**.
- Re-running with `WSM_MergeDisabledMods=true` brings the phantom back *and* prints the
  stale-build warning naming `modFearlessRoach` and 13 of the 224 declarations it lacks —
  confirming both features on one run.
- The two enabled mods' intended changes were ported onto current vanilla (backups in a
  local, non-committed folder), after which both files merged with zero warnings and zero
  function-level decisions. Verified: no vanilla declaration lost, both mods' changes
  present, brace balance identical to vanilla.

A static pass over all 44 conflicts (136 mod-copy comparisons) found the stale-build
signature in exactly 3 — the same 3 the engine went on to decline. No false positives, no
misses, at the coarse granularity used for that sweep; the shipped detector uses
`ScriptUnitExtractor`'s full unit set and is correspondingly more sensitive (it also flags
a 1-of-224 drift in `modImprovedHorseControls`, whose conflict auto-solves fine — which is
precisely why the warning reports drift rather than predicting failure).

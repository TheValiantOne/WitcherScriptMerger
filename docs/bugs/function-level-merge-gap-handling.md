# Function-level merge fallback corrupts non-unit content

**Status:** open
**Found:** 2026-08-10, WSM 0.6.2 (`WitcherScriptMerger.exe mcp` → `merge_conflicts`)
**Severity:** high — produces merged output that does not compile, silently
**Component:** `WitcherScriptMerger.Core/Tools/FunctionLevelMergeEngine.cs`,
`ScriptUnitExtractor.cs`; entered via `DiffPlexMergeEngine.TryFunctionLevelRescue`
(`DiffPlexMergeEngine.cs:236`, `:274`)

## Summary

When a whole-file merge can't auto-solve and `TryFunctionLevelRescue` takes over,
the *units* (functions/events/`@addField` fields) are merged correctly, but the
**gaps between them are not**. Two distinct failures come out of that, both of
which produce a `.ws` file the game refuses to compile:

1. **Plain member declarations are dropped.** `ScriptUnitExtractor` only promotes
   `@addField`-decorated fields to units, so an ordinary WitcherScript
   `private var x : bool;` / `default y = 4.5f;` lives in a *gap*. On the rescue
   path the accumulated side's gaps are discarded in favour of vanilla's, so
   those declarations vanish while the code that references them survives.
2. **A unit is emitted outside its class, and its separators are eaten.** Two
   functions were reassembled *after* the class's closing brace, and the newlines
   around them were lost, running three declarations together.

Both are reported by the engine's own audit text, but only as neutral-sounding
notes — this is the line that actually means "declarations were lost":

```
content from accumulated merge (…) near this position was not preserved
(vanilla formatting/content kept).
```

## How it was hit

Real load order, 198 mods, game build 4.04. Three files needed the rescue:
`game\player\r4Player.ws`, `game\player\player.ws`,
`game\gameplay\damage\damageManagerProcessor.ws`. All three came out broken;
the other 38 merges in the same run were fine.

```
merge_conflicts(relativePaths: <all 41 script conflicts>)
→ merged: 41, skipped: 0
```

Reordering to dodge the fallback does **not** help. The lossy pair is inherent
(accumulated ⊕ `modSmoothMovement`, accumulated ⊕ `modFatality`); moving the
last mod to the front only changes which mods sit in the accumulated prefix, and
the same content is lost.

## Defect 1 — unit emitted outside the class

`modImmersiveSound` declares two accessors inside `CR4Player`. In the merged
output they landed after the class-closing brace, at global scope:

```
	}
	
}                                                          ← CR4Player closes
                                                           ← blank
	public function GetVoiceSetLastPlayed() : float
	{ 
		return voicesetLastPlayed; 
	}	public function SetVoiceSetLastPlayed( time : float )   ← two decls, one line
	{ 
		voicesetLastPlayed = time;
	}exec function setcam(a:int, b:bool)                        ← runs into the next unit
```

Game output:

```
Error [mod0000_mergedfiles]game\player\r4player.ws(15564): 'public' has no sense for global function 'GetVoiceSetLastPlayed'.
Error [mod0000_mergedfiles]game\player\r4player.ws(15567): 'public' has no sense for global function 'SetVoiceSetLastPlayed'.
```

Note the two symptoms are separable: wrong *position* relative to the class
brace, and lost *separators* between adjacent units. `ScriptUnitExtractor`'s
contract says `Gaps[0] + Units[0].FullText + … ` reassembles byte-for-byte, so
the defect is in how the two documents' gap/unit sequences are interleaved on
the merge path, not in `Reassemble` itself.

## Defect 2 — dropped declarations

Ten declaration lines present in a source mod and in neither vanilla nor the
merged output, across the three rescued files:

| File | Mod | Lost |
|---|---|---|
| `game/player/player.ws` | modCriSlowMoCR | `public var mCSMCR : CCSMCR;` |
| `game/gameplay/damage/damageManagerProcessor.ws` | modCriSlowMoCR | `private var mCSMCR : CCSMCR;` |
| `game/player/r4Player.ws` | modCriSlowMoCR | `private var slowActive : bool;`, `private var isSlowDeathTimer : bool;`, `IsSlowActive()`, `DeactivateSlowMoCam()`, `aardSlowTimer()`, `igniSlowTimer()` |
| `game/player/r4Player.ws` | modBloodAndSteel | `public var basHeavySpeedID : int; default basHeavySpeedID = -1;` |
| `game/player/r4Player.ws` | modImmersiveSound | `private var voicesetLastPlayed : float;`, `default interactDist = 4.5f;` |

Game output (abridged — ~30 lines of the same shape):

```
Error [mod0000_mergedfiles]game\player\r4player.ws(213): I dont know any 'mCSMCR'
Error [mod0000_mergedfiles]game\player\r4player.ws(235): 'mCSMCR' is not a member of 'handle:CR4Player'
Error [mod0000_mergedfiles]game\player\r4player.ws(735): I dont know any 'IsSlowActive'
Error [mod0000_mergedfiles]game\player\r4player.ws(754): I dont know any 'slowActive'
```

The four *functions* in that table are a subtler variant: they were not dropped
outright, they were emitted mangled onto a preceding `}` line —
`}	timer function DeactivateSlowMoCam(dt : float, id : int) {` — i.e. the same
lost-separator failure as Defect 1. They compile, but exact-line comparison
against the source treats them as missing, which is a trap for any repair
tooling (it cost a round of duplicate-definition errors here).

## Suggested regression checks

Both are cheap to assert over merged output and would have caught this run:

1. **No member-shaped declaration at brace depth 0.** Walk the merged file
   tracking depth; flag any line matching
   `^\s+(public|private|protected|editable|saved|timer|event|final)\b|^\s+function\s`
   while depth is 0. Clean output scores 0; the raw fixture scores 1.
2. **No declaration present in a source and absent from the merge.** For each
   source mod of the merged path, every declaration-shaped line that isn't in
   vanilla must appear in the output. Compare on normalised text, not raw lines —
   see the mangling note above, or this check reports false positives.
3. `ScriptUnitExtractor.Reassemble` round-trip on the *merge* path, not just the
   extract path.

## Reproduction fixture

`docs/bugs/artifacts/r4Player.merged-raw.ws` — the untouched engine output from
the failing run (`mod0000_MergedFiles` ⊕ 11 mods, `modSmoothMovement` last).
It contains both defects: the orphaned accessors at the tail, and the ten
missing declarations. That directory is gitignored: the file is CDPR-derived
third-party script content and should not be committed.

## Related, lesser finding

`FileMerger.ConfirmOutputOverwrite` (`FileMerger.cs:827`) calls
`ShowMessage(..., NotifyButtons.YesNo, ...)` with **no `defaultResult`**, so
`HeadlessMergeNotifier` falls through to its generic table (`YesNo → No`). The
call sites at `FileMerger.cs:606` and `:658` run "regardless of dryRun" so a
preview predicts reality — which is right — but the consequence is that headless
and MCP `merge_conflicts` can only ever *create* merged output, never refresh it:
any conflict whose merged file already exists is reported as `skipped`, with the
prompt text on stderr as the only clue.

```
[Overwrite?] The output file below already exists! Overwrite?
G:\…\Mods\mod0000_MergedFiles\content\scripts\game\actor.ws
```

That also makes `dryRun` unable to answer "would this auto-solve?" for any
already-merged file — the skip happens before a merge is attempted. Supplying an
explicit `defaultResult` at that call site (or an opt-in overwrite/force
parameter on `merge_conflicts`) would resolve both.

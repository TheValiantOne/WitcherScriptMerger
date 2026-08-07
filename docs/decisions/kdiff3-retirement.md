# Decision: Retire KDiff3

**Status:** Implemented.
**Type:** Architecture decision + historical record.

## Decision

KDiff3 is no longer a WSM dependency. `WitcherScriptMerger/Tools/KDiff3.cs`,
`WitcherScriptMerger/Tools/KDiff3MergeEngine.cs`, and the `IMergeEngine`
interface that used to sit between them and `FileMerger` are deleted.
`WitcherScriptMerger.Core/Tools/DiffPlexMergeEngine.cs` (in-process, built on
the DiffPlex NuGet package, no external binary) is now the sole and default
text-merge engine, called directly by `FileMerger` — see `CLAUDE.md`'s
"Interactive vs. headless split" section for the current architecture.

This was the repo owner's explicit decision, not something arrived at by
default because DiffPlex happened to be "good enough." It trades away a real
capability: `DiffPlexMergeEngine` has a measured, non-zero failure rate on
dense multi-edit conflicts (see CLAUDE.md's Compatibility constraints for the
numbers) and no fallback for a vanilla-less merge, both of which
`KDiff3MergeEngine` handled — see "What a user's experience actually changes
to" below for what that means in practice. What KDiff3 offered in exchange —
a real interactive 3-way merge GUI, and a slightly more capable engine — came
at a real, ongoing cost: a GPL-licensed external binary dependency, a popup
window that steals foreground focus and can't be suppressed without hanging
the whole merge (see below), and roughly 250 lines of Win32 P/Invoke whose
correctness depended on undocumented, empirically-discovered timing behavior
in a third-party Qt application. That combination was judged not worth
carrying forward now that an in-process, dependency-free alternative exists,
even an imperfect one.

## Why this document exists

This is the only place this institutional knowledge survives. It was
originally written up in this fork's local, gitignored `HANDOFF.md`, and
lived operationally in `CLAUDE.md`'s "Compatibility constraints" section — but
`CLAUDE.md` describes the code as it exists today, and that section is being
removed along with the code it explained. Preserved here instead, before (in
the same change that) the motivating code is deleted, so a future reader
investigating "why does KDiff3 invocation look so strange" — or a future
attempt to reintroduce KDiff3 or a similar external GUI merge tool — has
something more durable than commit-message archaeology to start from.

## What a user's experience actually changes to

- **No more KDiff3 popup for a conflict that needs manual resolution.**
  Previously, a genuine conflict opened KDiff3's own 3-way merge editor
  window for the user to resolve by hand (interactive mode), or — in headless
  CLI/MCP mode — briefly appeared, stole foreground focus, and was killed
  after a timeout with the conflict reported as skipped. Now: a genuine
  conflict writes a git/diff3-style conflict-marker sidecar file
  (`<<<<<<<`/`|||||||`/`=======`/`>>>>>>>`, labeled with the real mod names)
  and opens it in the OS's default associated editor for that file type
  (`Tools/FileOpener.cs`, Core-side, `Process.Start` with
  `UseShellExecute = true`) — in both interactive and headless modes, since
  they're now the same code path underneath. There is no merge UI anymore;
  resolving the conflict means editing the sidecar by hand (or opening the
  three source files yourself) and re-running the merge once satisfied.
- **A small, measured, non-zero chance a real conflict is now reported as
  "needs manual resolution" that KDiff3 might have auto-solved cleanly**, on
  dense multi-edit conflicts specifically — see CLAUDE.md's Compatibility
  constraints for the actual measured rates (from ~0.35% at realistic,
  single-edit-per-side density up to double digits on adversarial dense
  cases). This never produces silently wrong output — DiffPlexMergeEngine
  detects the underlying DiffPlex bug that causes this and refuses to trust
  the result rather than risk writing corrupted merge output — but it does
  mean more conflicts now require the manual sidecar-editing workflow above
  than would have under KDiff3.
- **No more vanilla-less 2-way fallback.** If no vanilla version of a file
  can be found (expected mainly on the bundle-content path when no matching
  vanilla bundle exists), `DiffPlexMergeEngine` refuses the merge outright
  rather than attempting a degraded 2-way diff — KDiff3 had a coherent notion
  of a 2-file merge and always attempted one in this situation; DiffPlex's
  `ThreeWayDiffer`, as used here, does not, and attempting a 2-way fallback is
  new scope this retirement didn't take on.
- **No more `ReviewEachMerge` or `ShowPathsInKDiff3` settings.**
  `ReviewEachMerge` (show the merge UI even for an auto-solvable merge, to
  double-check it) has no equivalent — there's no merge UI to show anymore.
  `ShowPathsInKDiff3` (show real file paths instead of mod names in KDiff3's
  `--L1/--L2/--L3` pane labels) doesn't cleanly transfer either: the closest
  analogue, `DiffPlexMergeEngine`'s conflict-marker labels, already show mod
  names (not paths) on the `<<<<<<<`/`>>>>>>>` lines, and a marker file read
  as plain text just gets noisier with an absolute path where a short mod
  name reads more clearly — so the setting and its checkbox were removed
  rather than repurposed.
- **One fewer external binary to source separately.** `Paths.ValidateDependencyPaths()`
  no longer checks a `KDiff3Path`; a fresh checkout only needs QuickBMS and
  wcc_lite sourced separately to run end-to-end (see CLAUDE.md's "External
  tool dependencies"). KDiff3 itself was GPL-licensed and safe to bundle into
  a release (unlike QuickBMS/wcc_lite, which have no license file and were
  never committed to source control) — that licensing question is now moot
  for this dependency specifically.

## Empirical findings preserved from `KDiff3.cs`

The following was learned through direct, repeated empirical testing during
this fork's development — not from KDiff3's documentation, which (per
`doc/dothemerge.html`) says only that manual interaction always opens a
window, even in KDiff3's own "batch/automation mode," with no fail-fast or
truly headless option. This section is a preservation of that testing, kept
verbatim in substance from `CLAUDE.md`'s former "Compatibility constraints"
bullets and `KDiff3.cs`'s own comments, now that the code itself is gone.

### Window-title polling: `"Conflicts"` vs. `" - KDiff3"`

KDiff3 always briefly shows a plain window titled exactly `Conflicts` on
startup — regardless of whether the merge ultimately auto-solves or not. This
is **not** a "needs manual resolution" signal; it's transient and closes
within a few seconds either way.

Only a genuine, unresolved conflict leaves open a **second** window, titled
`<L1> <-> <L2>[ <-> <L3>] - KDiff3` (e.g. `Vanilla <-> modA <-> modB -
KDiff3`) — the actual comparison/merge editor. That window persists
indefinitely until a human closes it. An auto-solve's process, by contrast,
exits within a few seconds regardless of file size — 3400+ line files exited
in under 3 seconds in testing.

`KDiff3.RunHeadless`'s detection logic (`HasVisibleMergeWindow`) used
`EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible` +
`GetWindowText`, filtering to windows owned by KDiff3's own process ID and
checking `EndsWith(" - KDiff3", StringComparison.Ordinal)` — an ordinal,
suffix-only check, deliberately not matching on the transient `Conflicts`
title at all.

The practical rule this produced: **detect on window persistence past a short
grace period (~2–3 seconds, to let the transient `Conflicts` window close),
never on elapsed time alone, and never by assuming a visible window means
failure.** `RunHeadless` used a 3000ms grace period
(`gracePeriodMs = 3000`) after first observing the `" - KDiff3"` window,
plus a 60-second backstop timeout (`backstopTimeoutMs = 60000`) as an
absolute ceiling regardless of window state.

### The 250ms poll interval was load-bearing

`RunHeadless` polled for the merge window every ~250ms
(`Thread.Sleep(250)`) between `EnumWindows` scans. This number was not
arbitrary or merely "fast enough to feel responsive" — it was discovered to
be a real constraint by accident, while testing window-suppression
techniques (see below): polling every 15ms for the first second — with
**zero** window manipulation, just read-only `EnumWindows`/`GetWindowText`
queries — reliably hung KDiff3 the same way the suppression techniques did.
The identical, untouched launch polled at 200ms auto-solved normally every
time.

The likely mechanism: `GetWindowText` issues a cross-process
`SendMessage(WM_GETTEXT)` to the target window, which is a *blocking* call
that the target thread must service on its own message loop. Polling fast
enough plausibly starves or reorders KDiff3's own message loop during the
window it needs to actually compute the merge. "Poll faster to detect the
conflict window sooner" looks like an obviously-safe optimization and is not
one — it would silently turn every merge into a hang.

### Five window-suppression techniques were tried; all either did nothing or broke the merge

Tested empirically against both a guaranteed auto-solve case and a
guaranteed-conflict case (two synthetic mods editing the identical line
differently):

| Technique | Result |
|---|---|
| `ProcessStartInfo.WindowStyle = Hidden` | Silently ignored by KDiff3/Qt — window shows full-size regardless. Doesn't break anything, doesn't hide anything either. |
| `ProcessStartInfo.WindowStyle = Minimized` | Also silently ignored (confirmed via `IsIconic`). Same as above. |
| `ShowWindow(hwnd, SW_HIDE)` | **Genuinely hides the window** — and reliably makes KDiff3 hang forever at the `Conflicts` splash instead of ever auto-solving. |
| `SetWindowPos` moved off-screen | Same: genuinely hides it, same reliable hang. |
| Launching on a separate, non-interactive Windows desktop (`CreateDesktop`) | Same: genuinely hides it, same reliable hang. |

Control: the identical launch mechanism, completely untouched, auto-solved in
1.6–6.5 seconds every time. The pattern held across three independent
suppression mechanisms (`ShowWindow`, `SetWindowPos`, `CreateDesktop`) — a
strong signal that KDiff3's Qt runtime needs its window genuinely composited
on the real, interactive desktop to make progress at all, not something
fixable from outside the process. **Do not reintroduce any of these three
techniques without re-verifying they still hang** — and if a future KDiff3
version changes this behavior, that verification needs to be redone before
trusting a different result.

### Foreground focus theft, and the never-fully-verified restoration attempt

KDiff3's window steals foreground focus while shown (confirmed via
`GetForegroundWindow()`). Given the suppression techniques above were a dead
end, `KDiff3.RunHeadless` instead accepted the window appearing and tried to
restore focus to whatever had it beforehand:

1. Capture `previousForeground = GetForegroundWindow()` **before** launching
   KDiff3.
2. Launch KDiff3, run the poll loop above.
3. In a `finally` block, once KDiff3's window is confirmed gone — the kill
   path (`proc.Kill(entireProcessTree: true)`) is asynchronous, so it also
   waited up to 2 seconds via `proc.WaitForExit(2000)` first, so the restore
   attempt wouldn't race a window that was still technically alive — call
   `RestoreForegroundWindow(previousForeground)`.

`RestoreForegroundWindow` tried plain `SetForegroundWindow` first, which
Windows' foreground-lock policy **denied every single time** in testing: this
process never owned the foreground to begin with (KDiff3's window did), so by
the time it tried to reclaim it, it wasn't a privileged caller. It then
upgraded to the standard `AttachThreadInput` workaround — temporarily sharing
input state with whichever thread currently owns the foreground, calling
`SetForegroundWindow` under that shared state, then detaching — but this was
**also** observed denied in every test run in the sandboxed automation
environment used for that testing session. Whether that's a fundamental OS
limit in that scenario or an artifact of that specific environment's own
automation harness aggressively reclaiming focus was never resolved — it was
never tested from an ordinary, interactive desktop session. **This
restoration was never verified to actually work in practice; it was always,
at best, a good-faith best-effort mitigation, not a proven one.**

### Invoke via `Process.Start(fileName, argsString)` directly, never through a shell

A prior verification pass tested KDiff3 invocation through Git Bash/MSYS2 and
concluded a real conflicting file (`damageManagerProcessor.ws`) still needed
manual GUI resolution even after fixing an unrelated encoding mismatch.
Re-tested later through .NET's own `Process.Start(fileName, argsString)` two-
string overload (`UseShellExecute = false` by default on modern .NET) — the
actual code path WSM used — the identical file, both raw and
encoding-normalized, auto-solved cleanly every time. The bash-based test had
been an invocation-environment artifact, not real KDiff3 behavior. Any future
tool invoked the way KDiff3 was should be tested through the actual
`Process.Start` overload the app uses, not through an interactive shell,
before drawing conclusions about its behavior.

### Encoding normalization

Vanilla `.ws` files are UTF-16LE with a BOM; mod authors' files are often
plain UTF-8/ASCII with no BOM. KDiff3 had no command-line flag to specify
per-input encoding, and a mismatch could make it treat an entire file as
unmatchable, falling back to manual GUI resolution instead of auto-solving —
confirmed against a real file, `baseEffect.ws`, which failed to auto-solve
with mismatched encodings and succeeded cleanly, with correct merged output,
once both inputs were normalized to UTF-16LE+BOM. This normalization
requirement outlived KDiff3 itself — `Tools/FileEncoding.cs` (Core) still
normalizes every merge engine's input the same way, since the underlying
reason (matching vanilla's own encoding so the game will load a merged file
at all) has nothing to do with which merge engine is active.

### Command-line flags used

For reference, `KDiff3.BuildArgs` invoked KDiff3 with:

```
"<vanillaPath>" "<source1Path>" "<source2Path>" -o "<outputPath>"
--cs "WhiteSpace3FileMergeDefault=2"
--cs "CreateBakFiles=0"
--cs "LineEndStyle=1"
--cs "FollowFileLinks=1"
--cs "FollowDirLinks=1"
[--L1 Vanilla --L2 "<mod1 name>" --L3 "<mod2 name>"]
[--auto]
```

`WhiteSpace3FileMergeDefault=2` (verified against KDiff3's own source, not
assumed) means "always pick input B" for a conflict that's purely
whitespace once whitespace differences are ignored — given KDiff3's file
order of vanilla/source1/source2 mapping to inputs A/B/C, input B is always
`source1` (the first mod in merge order). `LineEndStyle=1` means DOS-style
(`\r\n`) line endings, matching vanilla `.ws` files. Both of these semantics
were carried forward deliberately into `DiffPlexMergeEngine.BuildMerge`,
which mirrors them (whitespace-only conflicts auto-resolve to the first mod's
side verbatim; synthetic conflict-marker lines use `\r\n`) — see
`DiffPlexMergeEngine.cs`'s own comments for where each of these still applies
today. `--auto` was appended only for non-interactive (headless, or
interactive-without-`ReviewEachMerge`) runs.

### Scratch-output-then-copy pattern

`RunHeadless`'s `-o` target was never the real output path directly — it was
always a scratch path under `Paths.TempBundleContent\HeadlessOutput\<random
guid>.<ext>`, only copied to the real output path after a **confirmed clean
exit** (`proc.ExitCode == 0 && File.Exists(scratchOutputPath)`). A killed
process (the "needs manual resolution, timed out" case) could therefore never
leave a partial or corrupt file at the real output path — worst case, nothing
happened at all, and the conflict was reported as skipped. This same
"never touch the real output except on confirmed success" principle carries
forward into `DiffPlexMergeEngine.MergeHeadless`, which never writes to
`outputPath` on anything other than a clean auto-solve, and routes conflict
markers to a separate sidecar location precisely so a failed/retried merge
can never be mistaken for a completed one (see `DiffPlexMergeEngine.cs`'s
comment on `GetConflictMarkerPath`).

## What was *not* carried forward, and why

- **A real interactive merge UI.** DiffPlex has no equivalent to KDiff3's
  3-way merge editor. The conflict-marker-sidecar-plus-default-editor
  workflow (see above) is the replacement, and it is a strictly less
  guided experience — there's no pane-by-pane visual diff, no click-to-choose
  resolution, just a text file with git-style markers. This was accepted as
  part of the retirement decision, not an oversight.
- **The vanilla-less 2-way fallback.** See "What a user's experience
  actually changes to" above.
- **Any window-suppression or focus-management logic.** None of it is needed
  anymore, since DiffPlexMergeEngine never opens a window in the first place.

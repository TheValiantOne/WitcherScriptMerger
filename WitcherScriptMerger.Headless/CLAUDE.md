# CLAUDE.md — WitcherScriptMerger.Headless

Guidance for working in `WitcherScriptMerger.Headless` (`net10.0` — no `-windows`
suffix, `Exe`), the Linux-capable CLI/MCP-only host. Only the `merge` CLI verb and `mcp`
server mode exist here — no WinForms reference anywhere in the project, no GUI code path
to fall back to. A first concrete step toward "true headless operation for CLI/Agent
interaction, focused on modded-gaming + Vortex workflows." References
`WitcherScriptMerger.Core` only — see `WitcherScriptMerger.Core/CLAUDE.md` for the domain
logic (`FileMerger`, `DiffPlexMergeEngine`, `Cli/MergeOperations`, `Mcp/WsmMcpTools`) this
project only adds thin routing around. See the root `CLAUDE.md` for overall project
context.

The whole project is just `Program.cs` and its own `App.config`.

## Routing (`Program.cs`)

Mirrors `WitcherScriptMerger/Program.cs`'s `args[0] == "merge"` / `args[0] == "mcp"`
dispatch, but with no third (no-args-launches-GUI) branch — no args, or an unrecognized
first argument, prints usage to stderr and exits 1.

`Environment.CurrentDirectory = AppContext.BaseDirectory` is set as the very first
statement in `Main`, before touching `AppState.Settings`/`Paths` at all — several Core
paths are relative to it (`Paths.Inventory`, `Paths.TempBundleContent`,
`Paths.DiffPlexConflictsDirectory`, `Paths.MergedBundleContentAbsolute`'s field
initializer; see Core's `CLAUDE.md`). This mirrors the WinForms host's `Program.RunCli`
doing the same as its own first statement, except unconditionally as the very first
thing here — this host has no no-args-launches-GUI branch to worry about leaving
unreset.

## What this host deliberately omits

- **No GUI.** No `System.Windows.Forms` reference at all, so there's nothing that
  *could* launch one.
- **No console-attach machinery.** No `[STAThread]`, no `AttachConsole`/
  `MaybeAttachConsole` P/Invoke (`kernel32.dll`, Windows-only) — this project has
  nothing console-attach-shaped to do (it's always launched as a plain console app, and
  stdout is reserved for MCP protocol frames in `mcp` mode regardless), so that
  Windows-specific mechanism was left out entirely rather than ported.
- **No KDiff3 — and never had it.** KDiff3 is retired repo-wide (see
  `docs/decisions/kdiff3-retirement.md`), so this is true of the WinForms host too now,
  but worth stating explicitly here: this project never had a `Tools/KDiff3.cs`-style
  Win32 P/Invoke dependency to begin with, since it postdates the retirement. `Program.cs`
  sets no engine at all — `FileMerger` builds its own `DiffPlexMergeEngine` directly,
  identically to the WinForms host, with no `MergeEngine` App.config switch on either
  host.

The actual scan/merge/MCP-tool orchestration is unchanged, shared Core code
(`Cli/MergeOperations.cs`, `Mcp/WsmMcpTools.cs`) — this project only replicates the thin
CLI argument-parsing/dispatch glue around it, which was small enough not to warrant
extracting into Core too.

## Dependency gating: text-merge engine only

Both `RunMerge` and `RunMcp` here gate on `Paths.ValidateTextMergeDependencies()` only —
**not** the combined `Paths.ValidateDependencyPaths()` that the WinForms host's `merge`
and `mcp` verbs both require (see `WitcherScriptMerger/CLAUDE.md`). This host has no
QuickBMS/wcc_lite bundled at all (see "External tool dependencies" in the root
`CLAUDE.md` and `docs/decisions/bundle-format-replacement-spike.md` — no cross-platform
replacement was found to exist), so requiring the full combined check would mean this
host could never merge even its supported flat-file (`.ws`/`.xml`) conflicts.

**Flat-file conflicts only — bundle-content conflicts are unsupported, by design, not by
oversight.** `App.config`'s `CheckBundleContents` defaults to `false` here (unlike the
WinForms host's `true`) specifically so a normal run never touches bundle scanning at
all. If a user turns it on anyway (or points `QuickBmsPath`/`WccLitePath` at real,
sourced-separately Windows binaries — those settings still exist here and work if this
host happens to be run on Windows), bundle-category conflicts fail gracefully rather than
crashing: `FileIndex/ModFileIndex.BuildAsync` (Core) checks `Tools/QuickBms.IsAvailable`
once per scan, not once per bundle, and if unavailable, prints one clear message and
skips bundle scanning entirely for that run instead of attempting it.

This replaced a real crash found by code inspection when this host was first built:
`Tools/QuickBms.GetBundleContentPaths` used to return `null` when QuickBMS couldn't be
found, and both `ModFileIndex.BuildAsync` and `FileMerger.GetUnpackedFiles` enumerated
that return value directly — unreachable on the WinForms host (which always gates
bundle-category scanning behind the combined `ValidateDependencyPaths()`, guaranteeing
real QuickBMS present), but reachable here for the first time. Fixed at the source:
`GetBundleContentPaths` now returns `Array.Empty<string>()` instead of `null`.
`FileMerger.GetUnpackedFiles`'s vanilla-bundle search (`Directory.GetDirectories` over
`Paths.BundlesDirectory`/`Paths.DlcDirectory`) is also now guarded against a missing
`content`/`DLC` directory (`DirectoryNotFoundException` otherwise), since a
scratch/incomplete game tree can reach this code here without a full real Witcher 3
install backing it. Both fixes live in Core, not this project, but neither was
reachable before this host existed.

Verified end-to-end in a scratch tree: a mod folder containing a junk `.bundle` file,
with `CheckBundleContents=true` and no QuickBMS configured, scans and merges cleanly
(flat-file conflicts still merge/skip correctly; the bundle file is never opened at all)
with one clear warning message and no exception, on both Windows and real Linux (see
below).

## Two real cross-platform path-separator bugs, found via real Linux testing

Found and fixed only by actually running the `linux-x64` publish under WSL2 (a real
Linux kernel, not just a cross-compile target check) — building/publishing for
`linux-x64` alone would not have caught either. Both live in shared
`WitcherScriptMerger.Core` code, not in this project, but neither was reachable before
this host existed, since the WinForms host is Windows-only:

- `FileIndex/ModFile.GetModNameFromPath` used a hardcoded `'\\'` to find the
  mod-folder-name segment of a full path. On Linux, `Path.Combine`-built paths use `/`,
  so `IndexOf('\\')` always returned `-1`, and the subsequent `Substring(0, -1)` threw
  `ArgumentOutOfRangeException` on the first flat-file merge attempted — a hard crash on
  every `merge` invocation. Fixed to use `Path.DirectorySeparatorChar`.
- `Mcp/WsmMcpTools.cs`'s `merge_conflicts` normalized a client-supplied
  `relativePaths`/`orderOverrides` key by replacing `/` with a hardcoded `'\\'` to match
  `ModFile.RelativePath`'s separator convention — correct on the WinForms host (always
  Windows), silently wrong on Linux, where `ModFile.RelativePath` itself uses `/`: a
  client sending a `/`-separated path (the natural style on any OS) would get
  "normalized" to `\`-separated, never match, and land in `unmatched` looking like it
  wasn't a real conflict at all. Fixed to normalize both possible separators to
  `Path.DirectorySeparatorChar` instead of assuming `\`.

The rest of Core was grepped for the same hardcoded-`'\\'`/`"\\"` pattern after finding
these two; no other occurrences remained.

## Publishing

No existing `.pubxml` profiles in this repo — these commands are the documented
convention instead. Cross-compiling for `linux-x64` works fine from Windows — producing
the binary doesn't require a Linux machine, only *running* it does:

```
dotnet publish WitcherScriptMerger.Headless/WitcherScriptMerger.Headless.csproj -r win-x64 --self-contained -p:PublishSingleFile=true -c Release
dotnet publish WitcherScriptMerger.Headless/WitcherScriptMerger.Headless.csproj -r linux-x64 --self-contained -p:PublishSingleFile=true -c Release
```

Each publish's `<AssemblyName>.dll.config` (the `App.config` copy
`System.Configuration.ConfigurationManager` actually reads) lands next to the executable
— copy it there if deploying the exe on its own. Confirmed empirically that this
resolves correctly even in a single-file publish, despite
`Assembly.GetEntryAssembly().Location` being documented (and confirmed here too, via a
real build's `IL3000` warning) to always return `""` for a single-file-bundled assembly:
`ConfigurationManager.OpenExeConfiguration("")` still finds and reads the real
`<AssemblyName>.dll.config` sitting beside the actual running executable, both with and
without that file present (missing-config still correctly triggers `AppSettings`'s
existing `Environment.Exit(1)` path) — no `AppSettings.cs` change was needed for
single-file publishing to work.

## Verification status

`dotnet build WitcherScriptMerger.sln` and `dotnet test` (the `WitcherScriptMerger.Tests`
suite) both pass with this host's code present. Self-contained single-file `win-x64` and
`linux-x64` publishes both succeed.

This host was verified against a **real Linux runtime**, not just a successful
cross-compile: WSL2 (Ubuntu 20.04, genuine Linux kernel, both `/mnt/c`-mounted and
native `ext4`-backed scratch trees) was used to actually run the published `linux-x64`
binary. Confirmed there:

- The `merge` verb against synthetic scratch mods (one auto-solvable conflict, correctly
  merged with UTF-16LE+BOM output matching vanilla's encoding; one genuine conflict,
  correctly skipped with git/diff3-style conflict markers written under
  `DiffPlexConflicts/` and surviving process exit).
- The `mcp` verb's full stdio round-trip (`initialize` → `tools/list` → `tools/call` for
  all four tools, including a `merge_conflicts` call using a forward-slash
  `relativePaths` entry specifically to exercise the separator-normalization fix above).
- The bundle-graceful-degradation path (junk `.bundle` file, `CheckBundleContents=true`,
  no QuickBMS — clean warning, no crash, exit code 2).

The equivalent Windows-side checks (both verbs, both self-contained single-file
publishes) were also run and matched in shape.

**Not verified**: an actual bare-metal/native Linux distribution outside WSL2, and the
bundle path was only exercised with a junk (non-`POTATO70`-format) `.bundle` file — a
real bundle-vs-bundle conflict was judged impractical to construct without
QuickBMS/wcc_lite (matching the WinForms host's own "bundle path is code-reviewed but not
round-tripped" status — see `WitcherScriptMerger/CLAUDE.md`), so that specific scenario
relies on code inspection of the fixes above, not an end-to-end run.

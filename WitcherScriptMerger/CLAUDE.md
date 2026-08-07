# CLAUDE.md — WitcherScriptMerger (WinForms host)

Guidance for working in `WitcherScriptMerger`, the WinForms host project
(`net10.0-windows7.0`, `WinExe`, `UseWindowsForms=true`). This is the original,
full-featured entry point: GUI + CLI + MCP, all three dispatched from this project's
`Program.cs`. It references `WitcherScriptMerger.Core` for all domain logic — see
`WitcherScriptMerger.Core/CLAUDE.md` for `FileMerger`, `DiffPlexMergeEngine`,
`AppState`/`IMergeNotifier`, and the CLI/MCP orchestration shared with
`WitcherScriptMerger.Headless`. See the root `CLAUDE.md` for overall project context.

There is no MVC/MVP split here — `Forms/MainForm.cs` (~1000 lines) is a monolithic
orchestrator that directly owns the tree controls, constructs `ModFileIndex`, drives
merges via `InteractiveMergeRunner`, and wires up async callbacks.

## Build, run & publish

- Run (GUI, no args): launch the built `WitcherScriptMerger.exe`, or
  `dotnet run --project WitcherScriptMerger/WitcherScriptMerger.csproj`.
- Run (CLI/MCP): see "CLI mode" / "MCP mode" below.
- **Publish** (self-contained single-file, `win-x64` only — it's a WinForms app, never
  makes sense on Linux; no existing `.pubxml` profile in this repo, this command is the
  documented convention instead):
  ```
  dotnet publish WitcherScriptMerger/WitcherScriptMerger.csproj -r win-x64 --self-contained -p:PublishSingleFile=true -c Release
  ```
  The publish's `<AssemblyName>.dll.config` (the `App.config` copy
  `System.Configuration.ConfigurationManager` actually reads, via Core's
  `AppSettings.cs`) lands next to the executable — copy it there if deploying the exe on
  its own. This resolves correctly even in a single-file publish despite
  `Assembly.GetEntryAssembly().Location` being documented (and confirmed via a real
  build's `IL3000` warning) to always return `""` for a single-file-bundled assembly —
  `ConfigurationManager.OpenExeConfiguration("")` still finds and reads the real
  `<AssemblyName>.dll.config` sitting beside the actual running executable, both with
  and without that file present (missing-config still correctly triggers `AppSettings`'s
  `Environment.Exit(1)` path). This finding applies identically to
  `WitcherScriptMerger.Headless` — see that project's own `CLAUDE.md`'s "Publishing"
  section for its own (`win-x64`/`linux-x64`) publish commands; the underlying mechanism
  is shared Core behavior (`AppSettings.cs`), verified independently on both hosts.

## Folder map

- `Forms/` — WinForms screens: `MainForm.cs` (the hub, also implements
  `IMergeNotifier`), `OptionsForm.cs`, `DependencyForm.cs` (startup blocker if tool
  paths are invalid), `MergeReportForm.cs`, `PackReportForm.cs`, `PriorityPrompt.cs`,
  `MessageBoxManager.cs` (see "MessageBoxManager" below).
- `Controls/` — custom `TreeView` subclasses: `SMTree.cs` (base, metadata/context-menu
  logic), `ConflictTree.cs` (detected conflicts), `MergeTree.cs` (existing merges),
  `SMTreeSorter.cs`, `ToolStripRegion.cs`.
- `Inventory/` — `InteractiveMergeRunner.cs`: the host-side counterpart to Core's
  `FileMerger` for the interactive path (see "Interactive merge flow" below).
- `Tools/` — empty as of KDiff3's retirement (`docs/decisions/kdiff3-retirement.md`);
  used to hold `KDiff3.cs` (Win32 P/Invoke for window-title polling) and
  `KDiff3MergeEngine.cs`. The sole text-merge engine, `DiffPlexMergeEngine`, lives in
  Core.
- Root: `Program.cs` (entry point: GUI, CLI, and MCP — see "Startup flow" below),
  `Extensions.cs` (WinForms-specific `TreeNode`/`TreeView` helpers and Win32 P/Invoke;
  pure string helpers live in Core's `StringExtensions.cs` instead), `TaskbarProgress.cs`,
  `App.config`, `Properties/AssemblyInfo.cs` (see the TFM compatibility constraint below).

## `MainForm`'s `IMergeNotifier` translation

`MainForm` implements `IMergeNotifier`, translating Core's neutral
`NotifyResult`/`NotifyButtons`/`DialogIcon` types to/from real `MessageBox.Show(...)`/
`DialogResult` calls. This is **not** a behavior-identical passthrough: one real prompt
(`LoadOrderValidator.PromptToPrioritizeMergedMod`'s "Custom Load Order Problem" dialog,
Core) lost a `MessageBoxManager`-based custom button caption that used to mark its
Cancel option as destructive/permanent (that relabeling mechanism was likely already
silently broken pre-split — see "MessageBoxManager" below — but the loss is real either
way; the warning is now spelled out in the message body instead, since `IMergeNotifier`
has no hook for relabeling a button). `ShowModal(Form)` isn't part of `IMergeNotifier` at
all — every call site is GUI-only, interactive code in this project, which calls
`MainForm.ShowModal` directly instead of going through the notifier abstraction.

## MessageBoxManager

`Forms/MessageBoxManager.cs` (from a 2010s CodeProject article) hooks
`SetWindowsHookEx(WH_CALLWNDPROCRET, ...)` on the current thread to relabel a standard
`MessageBox`'s buttons before it displays. It's still actively used at one call site —
`MainForm.PromptToDeleteForChangedHash`, a direct `MessageBox.Show(...)` call (not routed
through `IMergeNotifier`, since it's UI-only code) that relabels Cancel to `"Ne&ver"` via
`MessageBoxManager.Register()`/`.Unregister()` around the call. Its hook mechanism keys
off `AppDomain.GetCurrentThreadId()`, a deprecated API that doesn't reliably return the
real Win32 thread ID `SetWindowsHookEx` needs — it was likely already silently broken
before the Core/host split, independent of anything this split changed. This is why the
equivalent `LoadOrderValidator` prompt (see above) doesn't try to route a relabeled
button through `IMergeNotifier`: rather than propagate a mechanism that may not actually
work, that prompt spells the "Cancel is permanent" warning out in the message text
instead.

## Interactive merge flow (`InteractiveMergeRunner`)

`Inventory/InteractiveMergeRunner.cs` is the host-side counterpart to Core's
`Inventory/FileMerger.cs` for the interactive (GUI) path — see
`WitcherScriptMerger.Core/CLAUDE.md`'s "FileMerger: interactive vs. headless split" for
the Core side. Its public API (constructor shape, `MergeByTreeNodesAsync`,
`RepackBundleAsync`) deliberately mirrors the pre-split `FileMerger` so `MainForm`'s call
sites needed only a type-name change.

1. `FileIndex/ModFileIndex.BuildAsync` (Core) scans `Paths.ModsDirectory`, groups files
   by relative path, flags conflicts.
2. `MainForm` feeds the results into `ConflictTree`/`MergeTree`; the user checks nodes to
   merge.
3. `InteractiveMergeRunner.MergeByTreeNodesAsync` extracts one `FileMerger.
   InteractiveMergeRequest` per checked `TreeNode` (`ExtractRequest`) **inside its
   `BackgroundWorker`'s `DoWork`**, not before starting it — this matches the pre-split
   threading model, and matters beyond fidelity: `ExtractRequest` dereferences node
   metadata and casts `Tag` with no null/type check, and `BackgroundWorker` captures any
   exception from `DoWork` into `RunWorkerCompletedEventArgs.Error` instead of letting it
   propagate. Extracting outside `DoWork` would let that same exception throw
   synchronously on the UI thread instead, which modern .NET WinForms terminates the
   process for by default (unlike .NET Framework's more forgiving behavior).
4. `FileMerger.MergeFilesInteractive` (Core) builds/reuses a `Merge` record per file and
   dispatches to `MergeFlatFileInteractive` (plain `.ws`/`.xml`) or
   `MergeBundleFileInteractive` (bundle-packed files, first unpacked via
   `Tools/QuickBms.UnpackFile`).
5. `FileMerger.MergeTextInteractive` calls `DiffPlexMergeEngine.Merge(...)` (Core;
   in-process, auto-solving, or a conflict-marker sidecar for a genuine conflict — see
   Core's `CLAUDE.md`).
6. On success, `MergeInventory.AddModToMerge` (Core) hashes the result and persists the
   merge record; bundle content changes additionally go through `FileMerger.
   PackNewBundle` → `WccLite.PackBundle` + `GenerateMetadata` to repack `blob0.bundle`.
7. `InteractiveMergeRunner` owns the `BackgroundWorker` and supplies the `OnMergeReport`/
   `OnPackReport` callbacks that `FileMerger` invokes: `ShowMergeReport`/`ShowPackReport`
   optionally play a completion sound (`Program.Settings.Get<bool>("PlayCompletionSounds")`)
   and optionally pop up `MergeReportForm`/`PackReportForm` via `Program.MainForm.ShowModal`
   (gated on `ReportAfterMerge`/`ReportAfterPack` settings).

## Startup flow (`Program.cs`)

`Program.Notifier`/`Settings`/`LoadOrder`/`Inventory` are pass-through properties onto
Core's `AppState` (see `WitcherScriptMerger.Core/CLAUDE.md`) — every pre-existing call
site in this project kept working unchanged.
`static readonly bool _consoleAttached = MaybeAttachConsole();` runs as a field
initializer, ahead of everything, so early startup failures are visible in the invoking
terminal when there are CLI args. `Program` has an explicit (empty) static constructor
for the same `beforefieldinit`-determinism reason `AppState` does (see Core's
`CLAUDE.md`) — now load-bearing here specifically because nothing in `Main()` necessarily
touches a `Program`-owned field anymore (its former fields became pass-through
properties), so without the explicit constructor the CLR could defer
`_consoleAttached`'s initializer arbitrarily; confirmed empirically with a minimal repro
mirroring this exact shape.

`[STAThread] Main(string[] args)`: if `args` is non-empty, hands off entirely to
`RunCli(args)` and returns — the GUI is never touched. Otherwise:
`Application.EnableVisualStyles()` → check `Settings.HasConfigFile` →
`Paths.ValidateDependencyPaths()` (the **combined** check — QuickBMS *and* wcc_lite,
not just the text-merge engine; shows `DependencyForm` if either is missing) → construct
`MainForm`, reassign `Program.Notifier = MainForm`, `Application.Run(MainForm)`.

### CLI mode (this host)

`WitcherScriptMerger.exe merge [--order-file <path.json>]` merges every auto-solvable
conflict without opening any merge-tool window, then exits (a conflict needing manual
resolution opens its conflict-marker sidecar in the default editor instead — see Core's
`CLAUDE.md`). No-args still launches the GUI unchanged; passing `merge` (or any argument)
is what selects the CLI path.

`RunCli` sets `Environment.CurrentDirectory = AppContext.BaseDirectory` as its first
statement (several Core paths are relative to it — see Core's `CLAUDE.md`'s
`DiffPlexConflictsDirectory` note), then dispatches on `args[0]`. **The `merge` verb
requires the full combined `Paths.ValidateDependencyPaths()`** (QuickBMS *and* wcc_lite,
not just the text-merge engine) before doing anything else — this host refuses to start
a merge run at all without full bundle tooling configured, unlike
`WitcherScriptMerger.Headless`'s `merge` verb, which only requires the text-merge engine
(see `WitcherScriptMerger.Headless/CLAUDE.md`). `--order-file <path.json>` is parsed into
the `orderOverrides` shape Core's `ResolveMergeOrder` expects (`{"relative\\path.ws":
["modA", "modB"]}`). Exit codes: 0 = every conflict merged, 1 = couldn't even start (bad
args/config/deps), 2 = ran, but one or more conflicts were skipped.

- **Verification status**: the flat-file path (`Categories.Script`/`Categories.Xml`) is
  verified end-to-end against real conflicting files (including the encoding-mismatch
  scenario) and a synthetic guaranteed-conflict, in a scratch game/mods tree — never
  against a live install. The bundle path (`Categories.BundleText`) is code-reviewed and
  mirrors the proven flat-file orchestration, and its two building blocks
  (`GetUnpackedFiles`, `PackNewBundle`) are unchanged, already-exercised code — but it
  hasn't been round-tripped through a real bundle-vs-bundle conflict. If
  `tempbundlecontent` accumulates across many CLI runs during development, clear it
  between runs — one debugging session saw a single very slow run after a long buildup
  that didn't reproduce once it was cleared.

### MCP mode (this host)

`WitcherScriptMerger.exe mcp` runs an MCP server over stdio (`ModelContextProtocol`
NuGet package, `Host.CreateApplicationBuilder().Services.AddMcpServer()
.WithStdioServerTransport().WithToolsFromAssembly(typeof(WsmMcpTools).Assembly)` — the
assembly must be passed explicitly since `WsmMcpTools` lives in Core, not this calling
assembly; the parameterless overload only scans the calling assembly and would silently
register zero tools). `RunMcp` **also gates on the full combined
`Paths.ValidateDependencyPaths()`** before starting the server at all — this host won't
even start an MCP server without QuickBMS/wcc_lite configured, regardless of whether the
client ever calls a bundle-touching tool. This is a stricter gate than
`WsmMcpTools.RequireDependenciesAndModsDirectory` itself applies per-call (text-merge
engine only — see Core's `CLAUDE.md`) and stricter than `WitcherScriptMerger.Headless`'s
own `mcp` verb (also text-merge-only) — the finer-grained per-tool distinction in Core
only actually matters for a host whose own startup gate doesn't already guarantee both.

stdout must stay reserved for MCP protocol frames: `builder.Logging.AddConsole(o =>
o.LogToStandardErrorThreshold = LogLevel.Trace)` keeps the SDK's own logging on stderr.
`MaybeAttachConsole()` reads its own `Environment.GetCommandLineArgs()` (whose index 0 is
the exe path itself, so index 1 is `Main`'s `args[0]`) and skips `AttachConsole`
specifically when that first real argument is `"mcp"` — there's no parent console to
attach to in this scenario (an MCP client spawns this process with its own redirected
pipes), and it would be pointless at best.

- **Verification status**: smoke-tested end-to-end against a scratch game/mods tree — a
  hand-rolled stdio client (`initialize` → `tools/list` → `tools/call` for each tool),
  including a `merge_conflicts` call that exercised the (since-retired) `KDiff3.
  RunHeadless` path at the time it was tested (detected a genuine conflict, killed the
  stuck process, returned it in `skipped`) — the underlying engine has since changed to
  `DiffPlexMergeEngine`, but the MCP-level behavior verified (a conflict comes back in
  `skipped`, not silently dropped) is unaffected. The directory-allow-listing and
  `dryRun` additions (Core's `EnsureInScope`/`IsWithinModsDirectory`, `MergeConflicts`'s
  `dryRun` parameter) were separately verified via the same stdio-client approach plus an
  in-process harness against `WitcherScriptMerger.Core` directly against a fake merge
  engine — both that harness's fake-engine seam and the real KDiff3/QuickBMS/wcc_lite
  binaries used at the time are historical now (the seam it used, `IMergeEngine`, no
  longer exists post-retirement — see `docs/decisions/kdiff3-retirement.md`). Never run
  against a live install.

## Compatibility constraint: TFM must keep the explicit `7.0` OS-version suffix

This project uses `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` (to keep the
hand-written `Properties/AssemblyInfo.cs`), which also suppresses the SDK's
auto-generated `[assembly: SupportedOSPlatform("windows")]` attribute — that attribute is
added manually in `AssemblyInfo.cs` instead. The TFM must stay `net10.0-windows7.0` (not
bare `net10.0-windows`); dropping the `7.0` suffix reintroduces ~800 spurious `CA1416`
platform-compatibility warnings.

## Other host-only helpers

`Program.TryOpenFile`/`TryOpenFileLocation`/`TryOpenDirectory` are this host's own
"open in the OS's default app" helpers, used for opening merged output files and mod
folders from the GUI. `TryOpenFile`'s non-`.exe` branch is a bare `Process.Start(path)`
with no `UseShellExecute = true`, which throws on modern .NET for a non-executable path
(silently swallowed by that method's own `catch`) — a known wart, not fixed here. Core's
`Tools/FileOpener.cs` (used by `DiffPlexMergeEngine` to open conflict-marker sidecars) is
a separately, correctly implemented equivalent — see Core's `CLAUDE.md`; the two are not
unified.

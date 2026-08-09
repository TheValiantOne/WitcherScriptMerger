# Vortex Extension Design (Unit 4)

**Note (post-KDiff3-retirement, and refreshed since):** this document originally
described WSM's architecture as of its own first writing, when KDiff3 was still a
required on-disk dependency alongside QuickBMS/wcc_lite, no self-contained publish
existed, and there was no Linux-capable host. All three have since changed: KDiff3 was
retired in favor of an in-process, external-binary-free merge engine (see
`docs/decisions/kdiff3-retirement.md`); a self-contained single-file publish convention
now exists for both hosts (`dotnet publish -r <rid> --self-contained
-p:PublishSingleFile=true`, documented in each host's own `CLAUDE.md`); and
`WitcherScriptMerger.Headless` (`net10.0`, no GUI, CLI+MCP only) now exists and is
verified against a real Linux runtime. §2.2 below has been updated to treat these as
current fact rather than depended-upon-but-unbuilt. Only QuickBMS/wcc_lite remain
outside WSM's own source control (see "External tool dependencies" in the root
`CLAUDE.md`); read any KDiff3-specific mentions below as historical context, not
current fact.

**Status: design document only, now with settled decisions.** No TypeScript/Node
scaffolding exists in this repository yet, and this refresh does not add any — actual
implementation remains a separate, later batch. What *has* changed since the first
draft: a planning effort (research, an advisor consult, and explicit owner decisions)
has settled several structural questions that were previously open, most importantly
**scope** and **location** (both below). This document is still the starting point for
that later implementation, updated to reflect what's now decided rather than left as
open options.

**Scope (decided):** a **companion extension** to Vortex's existing, actively-maintained
built-in Witcher 3 game extension (`game-witcher3` — see §0 for its current location).
The new extension will **not** call `context.registerGame` and will **not** reimplement
game registration, mod deployment, load-order sorting, or config-matrix merging — all of
which `game-witcher3` already does correctly. It only replaces/supersedes the
script-merger-specific slice: tool discovery/invocation, config editing, conflict
notification, and merge history. Every registration this extension adds gates on
`selectors.activeGameId(state) === 'witcher3'` being Vortex's active game, exactly like
`game-witcher3`'s own registrations do.

**Location (decided):** the extension's TypeScript/Node code will live in a new
top-level `vortex-extension/` folder in *this* repo — not a separate sibling repo — kept
fully outside `WitcherScriptMerger.sln`, `dotnet build`, and `dotnet format`'s reach.
See §1 for what that means for tooling boundaries.

**What remains open**: everything not covered by the two decisions above — see §6 for
the current state of each open question, several of which are now resolved by
WSM-side and Vortex-side facts uncovered since the first draft, and several of which
are genuinely still open.

---

## 0. Context: Vortex already has a Script Merger integration today

Before designing anything new, it's worth being precise about what already exists,
because a new extension has to coexist with it, not pretend it doesn't exist — and,
per the scope decision above, *deliberately builds alongside it rather than replacing
it*.

**Citation correction from the first draft**: Vortex's official Witcher 3 game
extension no longer lives where this document originally cited it.
`Nexus-Mods/vortex-games` (the repo the first draft cited, `game-witcher3/index.js`) is
now **archived** (confirmed via `gh api repos/Nexus-Mods/vortex-games` →
`"archived": true`). The actively-maintained source now lives in the
`Nexus-Mods/Vortex` monorepo, at
[`extensions/games/game-witcher3/src/`](https://github.com/Nexus-Mods/Vortex/tree/master/extensions/games/game-witcher3/src) —
split across several files instead of one (`index.ts`, `scriptmerger.ts`,
`mergeInventoryParsing.ts`, `mergeBackup.ts`, `eventHandlers.ts`, `modTypes.ts`, and
more). The following is re-verified directly against that current source (fetched via
`gh api repos/Nexus-Mods/Vortex/contents/...`), not carried over from the stale
citation:

- It registers Script Merger as a discovered **tool** via `addDiscoveredTool`
  (`scriptmerger.ts`), ID `W3ScriptMerger`, with `requiredFiles:
  ['WitcherScriptMerger.exe']`, and can **auto-download** a build from GitHub releases
  at `https://api.github.com/repos/IDCs/WitcherScriptMerger` — a *different* fork from
  the one this repo forked from (`AnotherSymbiote/WitcherScriptMerger`; see this repo's
  `CLAUDE.md` "Project overview"). It prompts the user to run it, with consent, when
  script conflicts are detected. (There is no `registerTool` API in `vortex-api` at
  all — see §1's correction — so `addDiscoveredTool` is the only mechanism either
  extension can use, and the two can register distinctly-branded tool IDs without
  fighting over the same one; there's no API to hide or disable another extension's
  existing tool registration either way.)
- **Running it launches the GUI, not a headless merge.** `runScriptMerger()` calls
  `api.runExecutable(tool.path, [], { suggestDeploy: true })` — an *empty* argument
  list. Per this repo's own `Program.cs` (`args.Length > 0` is what selects the
  CLI/MCP path at all; no args means the GUI), that's a GUI launch, not a headless
  `merge` invocation. The `IDCs/WitcherScriptMerger` fork Vortex actually downloads is
  also a different codebase from this repo, and predates this repo's CLI/MCP additions
  (see this repo's own commit history) — it likely has no headless mode to invoke even
  if Vortex wanted one. So Vortex's existing flow today is "launch the GUI, let the
  user drive merge conflicts by hand, then read the result back afterward" — **not** a
  precedent for unattended/headless invocation. That distinction matters directly for
  §3 below.
- It reads and rewrites WSM's own config file at the OS level: `setMergerConfig()`
  parses `WitcherScriptMerger.exe.config` as XML and overwrites the `GameDirectory`,
  `VanillaScriptsDirectory`, and `ModsDirectory` `<add key="..." value="..."/>` entries
  in its `<appSettings>` block with paths derived from Vortex's own knowledge of the
  game install, then writes the file back — called both at initial tool setup and
  before running the merger. **This is the same fragile, race-prone, unlocked pattern
  §4.1 below used to propose for this extension** — it isn't a novel idea, it's an
  already-shipping pattern in `game-witcher3`'s own codebase, which is reassuring
  precedent that the *approach* works, but §4.1 now recommends a first-class
  alternative for this extension instead of copying the hand-edit, once that
  alternative exists WSM-side.
- `getMergeInventory()`/`mergeInventoryParsing.ts` parses `MergeInventory.xml` directly
  (`<MergedModName>`, `<IncludedMod>` elements) — the same file this repo's
  `Inventory/MergeInventory.cs` owns via `XmlSerializer`.
- It expects the merged-output mod folder to be named with a `mod0000_`-style locked
  prefix (`LOCKED_PREFIX = "mod0000_"` in the source) so Vortex pins it to load-order
  slot 1 (ahead of everything it merges). WSM's own default `MergedModName` in
  `App.config` is already `mod0000_MergedFiles` — the two conventions already agree by
  default, with no translation needed, as long as the setting isn't changed to
  something that no longer starts with the locked prefix Vortex expects.
- It has both `exportScriptMerges()` (Vortex Collections: validates merged files
  reference only mods present in the collection before letting a collection upload
  proceed) and `importScriptMerges()` (installing a collection that bundles script
  merges) paths. Installing such a collection shows a warning dialog — "importing
  these will overwrite any existing script merges you may have effectuated" — with a
  Cancel option, then proceeds to overwrite on confirmation. That's a real coexistence
  hazard worth carrying into §4.3 and §6 below: it's not just "two integrations might
  both prompt the user," it's "installing a Collection through the existing
  integration can overwrite this extension's own prior merge work if the user clicks
  through the warning without realizing what it means for a WSM-based workflow."
- **A second, distinct coexistence hazard, found this round**: `eventHandlers.ts`
  wires `context.api.events.on("profile-will-change", onProfileWillChange(context.api))`,
  and `onProfileWillChange` (also in `eventHandlers.ts`) calls into `mergeBackup.ts`'s
  exported `storeToProfile(api, previousProfileId)` then `restoreFromProfile(api,
  newProfileId)` — backing up and restoring merged-script content **per Vortex
  profile** on every profile switch. `MergeInventory.xml` has no profile concept at
  all; it's one global file, unaware Vortex profiles exist. This is a second, distinct
  hazard from the Collections-import one above — it fires on ordinary profile
  switching, not just on installing a collection — and a later implementation unit
  will need an explicit answer for it (see §6, Open Question 1).
- Its `witcher3dlc` mod type (`modTypes.ts`/`index.ts`, `registerModType("witcher3dlc",
  ...)`) deploys mods Vortex itself manages directly into `<GameDir>\DLC\<modname>\`.
  WSM's own scanner never looks there — `FileIndex/ModFileIndex.cs`'s `BuildAsync`
  enumerates only `Directory.GetDirectories(Paths.ModsDirectory, "mod*", ...)`, never
  `Paths.DlcDirectory` — so conflicts *between* mods Vortex deploys as `witcher3dlc` are
  invisible to WSM today. That's a real scope boundary, addressed directly in §2.2.

This means a brand-new Vortex extension isn't filling a total void; it's a companion
integration point that has to coexist with the built-in one on purpose (see the open
questions in §6, most of which this document can now partially answer). It also means
the config-file-editing and load-order-locking "hard problems" already have a proven
answer in `game-witcher3`'s own codebase (leaned on directly in §4 below) — but
headless/unattended invocation of WSM specifically does **not** have an existing
precedent in Vortex's codebase; that part is genuinely new ground for §3's
recommendation to reckon with honestly.

*(Sources: [`Nexus-Mods/Vortex`, `extensions/games/game-witcher3/src/`](https://github.com/Nexus-Mods/Vortex/tree/master/extensions/games/game-witcher3/src)
(`index.ts`, `scriptmerger.ts`, `mergeInventoryParsing.ts`, `mergeBackup.ts`,
`eventHandlers.ts`, `modTypes.ts`), fetched and read directly via `gh api`;
[`Nexus-Mods/vortex-games`](https://github.com/Nexus-Mods/vortex-games) confirmed
archived via `gh api repos/Nexus-Mods/vortex-games`;
[Nexus Mods wiki, "Modding The Witcher 3 with Vortex"](https://wiki.nexusmods.com/index.php/Modding_The_Witcher_3_with_Vortex);
[Vortex Wiki, "Tool Setup: Witcher 3 Script Merger"](https://wiki.nexusmods.com/index.php/Tool_Setup:_Witcher_3_Script_Merger).)*

---

## 1. Tech stack

Vortex extensions are **TypeScript/Node**, built against the
[`vortex-api`](https://github.com/Nexus-Mods/vortex-api) package and Vortex's own
extension conventions: an `info.json` manifest, an entry point exporting a single
**`init(context: IExtensionContext)`** function as the module's default export (the
first draft called this `activate(context)` — corrected here against the Vortex wiki's
own "General Introduction to Vortex extensions" page, re-fetched this round: `function
init(context) {...}; exports.default = init;` is the documented convention).
`context.registerAction`/`registerModType`/etc. remain as before, but **there is no
`context.registerTool` API** — re-checked directly against `vortex-api`'s published
`lib/api.d.ts` typings, which define no such method. Tool discovery/registration goes
through `actions.addDiscoveredTool(gameId, toolId, toolDetails: IDiscoveredTool,
isCustom: boolean)`, dispatched via `context.api.store.dispatch(...)` — exactly how
`game-witcher3` registers `W3ScriptMerger` today (§0), and it works identically for a
companion extension registering a distinctly-branded, different tool ID. This is a
completely different toolchain from this repo's .NET/WinForms solution — there is no
sensible way to fold it into `WitcherScriptMerger.sln`.

**Consequence for repo layout (decided):** the extension's code lives in a new
top-level `vortex-extension/` folder in *this* repo — not a separate sibling repo, and
not one of two "candidate options" as the first draft framed it. It stays fully outside
`WitcherScriptMerger.sln`, `dotnet build`, and `dotnet format`'s reach: no Node tooling,
`package.json`, or `node_modules` should ever need to appear anywhere `dotnet build
WitcherScriptMerger.sln` looks, and conversely nothing under `vortex-extension/` should
need `dotnet` for its own build/lint/test cycle.

---

## 2. Install / setup flow

### 2.1 Installing the extension itself

Vortex has a built-in Extensions page that installs from a community-maintained
registry with one click, and also accepts a manually-dropped extension folder under
`%APPDATA%\Vortex\plugins\<extension-id>`. Either distribution path is viable; getting
listed in the in-app registry is a separate, later decision (see §6) distinct from the
extension existing at all.

### 2.2 Locating the WSM CLI binary

WSM is not bundled with Vortex, and the extension needs a WSM executable capable of
running `merge` and/or `mcp` mode (see §3). Two artifacts the first draft flagged as
"depended-upon-but-unbuilt" are now real, current fact rather than assumptions:

- A **self-contained single-file publish convention** exists for both hosts —
  `dotnet publish <project>.csproj -r <rid> --self-contained
  -p:PublishSingleFile=true -c Release`, documented in each host's own `CLAUDE.md`
  (`WitcherScriptMerger/CLAUDE.md`: `win-x64` only, full GUI+CLI+MCP;
  `WitcherScriptMerger.Headless/CLAUDE.md`: `win-x64` and `linux-x64`, CLI+MCP only).
  This is what the extension would most plausibly bundle or download — a single `.exe`
  with the .NET runtime baked in, no separate .NET install required on the user's
  machine.
- **`WitcherScriptMerger.Headless`** (`net10.0`, no WinForms reference, CLI+MCP only)
  now exists and is verified against a real Linux runtime (WSL2, not just a
  cross-compile check — see that project's own `CLAUDE.md`). It gates only on
  `Paths.ValidateTextMergeDependencies()`, not the combined QuickBMS+wcc_lite check the
  WinForms host requires — so it can merge flat-file (`.ws`/`.xml`) conflicts with
  **no external binaries at all**, on either Windows or Linux; bundle-content conflicts
  still need QuickBMS/wcc_lite (see below) and degrade gracefully (one warning, no
  crash) when they're absent. Vortex itself is Windows-only today, but Nexus Mods has
  publicly committed to native SteamOS support for Vortex, expected later in 2026 — a
  Linux-capable WSM CLI host is now a real, exercised option for that future, not
  speculative. Whether a future Linux/SteamOS Vortex should actually drive this Linux
  build natively, versus simply keep shelling out to the existing Windows build under
  the same Proton compatibility layer Witcher 3 itself would already be running under,
  remains genuinely undecided — see Open Question 8 in §6, unchanged by anything above.

**v1 bundle-content scope (decided):** full support for **vanilla-baseline matching** —
two ordinary mods altering official DLC/expansion bundle content (e.g. Blood & Wine).
This is exactly the shape the recently-merged case-insensitive DLC-folder-regex fix
already handles (`FileMerger.IsVanillaDlcBundleFolder` /
`VanillaDlcBundleFolderPattern`, documented in `WitcherScriptMerger.Core/CLAUDE.md`'s
"Vortex-fork parity fixes" section), and a parallel unit is making that regex
config-extensible so future DLC (the announced "Songs of the Past" expansion) doesn't
need a code change. **Explicitly not in scope for this round**: detecting conflicts
between mods Vortex itself deploys via its own `witcher3dlc` mod type into
`<GameDir>\DLC\<modname>\` (§0) — WSM's scanner only ever looks at
`Paths.ModsDirectory` ("mod*" folders) as a mod-content scan root, never
`Paths.DlcDirectory`, and extending that is a separate, larger change than this design
covers.

Setup flow:

1. On first activation (or on first use of a script-merge action), the extension
   checks for a cached WSM binary in its own extension-private storage.
2. If absent, it either (a) unpacks a bundled copy shipped inside the extension
   package itself, or (b) downloads the self-contained publish artifact from a WSM
   GitHub release, similar to how `game-witcher3` already downloads the
   `IDCs/WitcherScriptMerger` fork today (see §0) — verify via checksum before trusting
   it. Once the release-workflow/`--version` work described in §6 (Open Question 4)
   lands, this step can also version-check the download against a minimum supported
   WSM release rather than trusting whatever a release tag happens to contain.
3. **QuickBMS/wcc_lite are a separate problem the extension cannot solve by bundling
   WSM alone.** Per the root `CLAUDE.md`, neither is in WSM's own source control —
   their licensing is unresolved, and that constraint doesn't go away just because a
   different project is doing the downloading. The self-contained publish convention
   does not change this: it packages WSM's own managed code, not these two external
   binaries. If the extension only needs flat-file merging (the v1 bundle-content scope
   above doesn't require QuickBMS/wcc_lite at all on the Headless host), this may not
   block a v1 at all; for bundle-content conflicts, the extension has to either point
   at an existing local install of these tools (e.g., detect the
   `IDCs/WitcherScriptMerger` fork Vortex may have already downloaded per §0, and
   reuse its `Tools\` subfolder) or prompt the user to source them the same way WSM's
   own README does. This should not be silently glossed over — see §6, Open Question 2,
   still genuinely open.
4. The extension writes the resolved WSM binary path into its own settings, and
   surfaces it (read-only or editable) in Vortex's per-game settings panel so the user
   can override it if they already have a WSM install they prefer.

---

## 3. Invocation model

WSM exposes two non-GUI surfaces today, documented in each host's own `CLAUDE.md`
(`WitcherScriptMerger/CLAUDE.md`'s "CLI mode (this host)" / "MCP mode (this host)"
sections; `WitcherScriptMerger.Headless/CLAUDE.md` mirrors the same CLI verb and MCP
tool surface, differing only in how strictly it gates on QuickBMS/wcc_lite being
present):

| | CLI (`merge [--order-file <path.json>]`) | MCP (`mcp`, stdio JSON-RPC) |
|---|---|---|
| Lifecycle | One-shot process, exits when done | Long-lived process, one client session per launch |
| Per-file conflict preview (paths, hashes, default order, already-resolved) | **Not exposed** — no `scan`/`status` CLI verb exists | `scan_conflicts` |
| Aggregate status (dependency validation, resolved directories, conflict *count*) | **Not exposed** | `get_status` — note this is aggregate-only (a count), not per-file detail; it doesn't substitute for `scan_conflicts` |
| Merge, restricted to specific files | No — `merge` always acts on every detected conflict (`--order-file` only overrides *ordering*, not *which files*) | `merge_conflicts(relativePaths, orderOverrides)` |
| Structured result | Coarse only: `Program.cs`'s `RunCli` sets a real exit code (`0` = every conflict merged, or none found; `1` = couldn't even start — bad args/config/missing dependency; `2` = ran, but one or more conflicts were skipped), but *which* files merged vs. skipped is only in free-text `Console.WriteLine` output, not machine-parseable JSON | Yes — `{merged: [...], skipped: [...]}` as structured JSON-RPC, naming the actual files |
| History (`MergeInventory.xml` records) | Not exposed by WSM itself (but the file is plain XML — see §4) | `list_merges` |

### Recommendation: CLI `merge` as the default, MCP as a richer follow-on enhancement

Reasoning:

- **Correction against an easy mistake to make here**: it would be tempting to say the
  CLI path "mirrors what Vortex's existing integration already does" and call that
  proven. It doesn't, quite — §0 found that `runScriptMerger()` launches the *GUI*
  (empty argument list to `api.runExecutable`), and the fork it launches likely
  predates this repo's CLI/MCP additions entirely. So headless/unattended WSM
  invocation from Vortex is genuinely new ground, not something already exercised in
  production. What *does* carry over from the existing integration is the shallower,
  still-useful shape: spawn a WSM process, wait for it to finish, then re-read
  `MergeInventory.xml` to see what changed — that part of the pattern is proven, just
  not the "and it was headless" part.
- Given that, the CLI verb still comes out ahead on complexity for a first cut: no new
  client-side protocol work (no JSON-RPC/MCP client to write or import), and no
  persistent child-process lifecycle to manage (no crash/restart handling, no
  orphaned-process cleanup on Vortex exit) — a one-shot process that runs and exits is
  about as simple as a first, unverified headless-invocation path can be, which matters
  precisely *because* it's new ground rather than something to lean on prior art for.
- Critically, a v1 built only on the CLI is **not** as limited on the history/UX front
  as the table above makes it look, because Vortex's own extension already parses
  `MergeInventory.xml` directly for its own purposes (§0) — a new extension can do the
  same read-only parsing itself for a "merge history" view, without needing WSM's
  `list_merges` MCP tool at all. That closes most of the gap between "CLI-only" and
  "has history UX."
- What CLI-only *cannot* do is a genuine **pre-merge conflict preview** — "here's what
  would change, review it, then confirm" — because there is no CLI verb that only
  scans without merging, and nothing in `WitcherScriptMerger/CLAUDE.md` suggests one is
  planned. Building
  that preview by having the extension re-implement WSM's own conflict-scanning logic
  (walking mods, comparing hashes) would duplicate `FileIndex/ModFileIndex.cs` outside
  this repo — exactly the kind of "invent new capability" this design is supposed to
  avoid. The only way to get a real preview without duplicating that logic is to call
  into WSM itself, which means MCP's `scan_conflicts`.
- MCP also gives per-file targeting (`relativePaths`) and per-file order overrides
  (`orderOverrides`) as first-class, structured input/output, versus the CLI's
  all-conflicts-every-time behavior and free-text console output. A "merge just this
  one file, in this order" UX action needs MCP.
- **New finding this round, confirming (not just assuming) that MCP needs its own
  transport path**: `vortex-api`'s published `lib/api.d.ts` typings show
  `api.runExecutable(executable: string, args: string[], options: IRunOptions) =>
  Promise<void>` has no stdio/pipe access whatsoever — `IRunOptions` exposes only
  `cwd`, `env`, `suggestDeploy`, `shell`, `detach`, `expectSuccess`, `onSpawned`,
  `onExit`. That's fine for the one-shot `merge` CLI verb (fire, await the promise,
  then diff `MergeInventory.xml` before/after, exactly the v1 flow this section
  recommends) but it categorically **cannot** carry MCP's JSON-RPC stdio frames. An MCP
  client has no choice but to bypass `vortex-api` for that specific call and use raw
  Node `child_process.spawn(exe, ['mcp'], {stdio: 'pipe'})` directly, hand-rolling the
  JSON-RPC framing itself.

So: ship the CLI-driven "spawn `merge`, wait, refresh the mod list from
`MergeInventory.xml`" flow first, as the low-risk default for the core "resolve script
conflicts" action, built entirely on `api.runExecutable` — no bypass of `vortex-api`
needed for v1. Treat MCP as a v2 enhancement that unlocks conflict preview, per-file
merge actions, and a live dependency/status check (`get_status`) surfaced in Vortex's
UI, built on a hand-rolled `child_process.spawn` MCP client per the finding above.
**Process lifecycle for that client (resolves Open Question 6 in §6):** spawn per
user-initiated workflow, tear down when the relevant panel/dashlet closes — not a
permanent session-long daemon, and not spawn-per-tool-call either. Every MCP tool call
already re-scans and re-loads from scratch server-side (see
`WitcherScriptMerger.Core/Mcp/CLAUDE.md`), so a longer-lived process mainly saves the
stdio handshake cost across a burst of related calls (e.g. `scan_conflicts` then
`merge_conflicts` from the same review session), not server-side work — which is
exactly what a per-workflow process buys without paying for a daemon's crash/restart
and orphaned-process-cleanup complexity.

---

## 4. Data model mapping

WSM's directory configuration (`GameDirectory`, `ModsDirectory`, `MergedModName`) comes
from `App.config`'s `<appSettings>` block only (`AppSettings.cs` /
`Program.Settings.Get(...)`, read by `Paths.cs`) — **neither the CLI `merge` verb nor
any MCP tool accepts a directory override as an argument.** This is the single most
important constraint for this section, and it shapes everything below.

### 4.1 Mods directory

Vortex already manages Witcher 3 mod installation directly into
`<gameRoot>\Mods\<modName>\...` (the same layout WSM expects; `Paths.ModsDirectory`
defaults to `<GameDirectory>\Mods` when the `ModsDirectory` setting is blank). Since
Vortex "will only pick up on mods that you have installed via Vortex" (i.e. it deploys
into the real game mods folder, not some Vortex-private staging area, for a
non-symlink-deployment game like this), **no translation of Vortex's internal mod
state into a separate WSM-readable format should be necessary** — WSM can scan the
same physical folder Vortex deploys into, exactly as it does today when a human
installs mods by hand. What the extension *does* need to do is make sure the invoked
WSM instance's `GameDirectory`/`ModsDirectory` settings actually point at the game
install Vortex is managing (Vortex already knows the exact game install path — that's
central to what a game extension does).

**Recommended mechanism (resolves Open Question 5 in §6, supersedes this section's
original proposal):** a generic `WSM_<KeyName>` environment-variable override for any
`App.config` `<appSettings>` key, landing alongside this doc's own refresh in a
parallel WSM-side unit — `AppSettings.Get`/`Get<T>` will check the corresponding env
var (e.g. `WSM_GameDirectory`, `WSM_ModsDirectory`, `WSM_MergedModName`) before falling
through to `ConfigurationManager`. Since the extension controls the environment it
spawns a child process into (`IRunOptions.env`, per §3's typings review, or plain
`child_process.spawn`'s own `env` option for the MCP path), it can set these per-launch
without touching any file on disk at all — no shared mutable state, no write-then-race
window. This is a first-class, supported alternative for every caller (this extension,
and eventually `game-witcher3` itself), not something this extension has to
independently reimplement.

The original proposal — writing `GameDirectory`/`ModsDirectory` keys directly into the
deployed `WitcherScriptMerger.exe.config` XML file before invoking WSM — is not a novel
idea invented for this design; it's exactly what `game-witcher3`'s existing
`setMergerConfig()` already does in production (§0), down to the same file name and the
same `GameDirectory`/`VanillaScriptsDirectory`/`ModsDirectory` keys, which was good
evidence the *approach* works in practice. But it's also exactly the fragile,
race-prone pattern the env-var mechanism above exists to replace: `AppSettings.cs`
caches its `Configuration` object in memory and only persists changes on an explicit
`Save()` call, so if the extension hand-edited `WitcherScriptMerger.exe.config` while a
WSM process (GUI or CLI/MCP) was already running against the old, cached config,
results would be undefined — and `setMergerConfig()` itself doesn't guard against this
either (it's a best-effort file write with a bare try/catch). **This extension should
use the `WSM_<KeyName>` env-var mechanism once it lands, not replicate the
config-file-hand-edit pattern**, even though that pattern has real production
precedent. **If implementation of this extension begins before that mechanism lands**
and the hand-edit pattern is used as a stopgap, the same operative rule
`game-witcher3` itself doesn't enforce still applies: only edit
`WitcherScriptMerger.exe.config` immediately before spawning a fresh WSM process,
never while one (GUI or CLI/MCP) is already running against it — that ordering is the
only mitigation available for this specific race until the env-var mechanism removes
the shared mutable state entirely.

### 4.2 Load order / `mods.settings`

WSM's `LoadOrder/CustomLoadOrder.cs` reads the game's own `mods.settings` file
directly — it doesn't care what wrote it. Independently confirmed from Vortex's own
documentation: "Vortex automatically generates the `mods.settings` file to reflect your
[Vortex-managed] load order," and it's the same physical file. So **no translation
layer is needed here either** — a Vortex-managed load order is, by construction,
already sitting in the exact file WSM already knows how to read. The one thing to keep
consistent is `MergedModName`, per the `LOCKED_PREFIX` convention already covered in
§0: if a user (or this extension) ever changes WSM's `MergedModName` setting away from
a `mod0000_`-prefixed value, the merged mod could stop loading first under Vortex's
load-order locking.

### 4.3 Write direction: WSM's merge output vs. Vortex's deployment bookkeeping

§4.1–4.2 only cover Vortex → WSM (WSM reading a mods folder and a `mods.settings` file
Vortex produced). The other direction is not symmetric and is easy to miss.

When WSM merges conflicts, it writes the result into a new mod folder —
`mod0000_MergedFiles\` by default — *inside the same physical mods directory Vortex
deploys into* (`Inventory/Merge.cs`'s output paths are all rooted at
`Paths.ModsDirectory`). Vortex did not create that folder and has no deployment record
of it; from Vortex's own bookkeeping, it's an unmanaged, foreign addition to a
directory Vortex otherwise considers fully under its control. This is very likely
*why* Vortex's built-in integration doesn't just "refresh a file list" after running
Script Merger — it specifically parses `MergeInventory.xml` and special-cases the
locked `mod0000_` slot (§0) rather than treating the merge output as an ordinary
externally-added file. A new extension inherits the same problem and should follow the
same pattern: after a merge, register/import the merged-mod folder as a
Vortex-tracked mod (or otherwise reconcile it with Vortex's deployment state) rather
than assuming Vortex will notice it on its own. Also worth remembering here: Vortex's
built-in integration can independently overwrite that same merged-mod folder via
`importScriptMerges()` when a Collection bundling script merges is installed (§0) — a
new extension's reconciliation logic needs to survive that happening underneath it,
not just the "Vortex never touches this folder" case.

### 4.4 Sequencing: WSM only sees what's already deployed

A mod that's installed in Vortex but not yet **deployed** exists only in Vortex's own
staging area, not in `Paths.ModsDirectory` — WSM has no visibility into Vortex's
internal state and can only scan the real mods folder on disk. So "detect/merge
conflicts" has to run *after* deployment, not at install time or based on Vortex's
in-memory mod list. This is a real ordering constraint on when the extension's actions
are meaningful (matches §5's choice of "after a deployment" as the natural hook point),
not just an implementation nicety.

### 4.5 Summary

Reading Vortex's load order and mod layout needs no translation layer — both already
converge on the same on-disk files (`mods.settings`, the mods directory) regardless of
which tool produced them. What *does* need explicit handling is the write side: point
the invoked WSM process at the right `GameDirectory`/`ModsDirectory` for every launch
(via the `WSM_<KeyName>` env-var mechanism, §4.1), don't let `MergedModName` drift from
whatever prefix Vortex's load-order locking expects, only scan/merge after deployment
(§4.4), and reconcile WSM's merge output back into Vortex's own deployment/mod tracking
afterward (§4.3) rather than assuming Vortex will pick it up automatically.

---

## 5. UX

Proposed surface, roughly in order of how load-bearing each piece is:

- **Notification / badge when conflicts exist.** This one is genuinely gated on which
  invocation model is wired up (§3), and the two versions aren't the same feature:
  - v1 (CLI only) cannot know in advance whether conflicts exist without merging them
    — there's no scan-only CLI verb. So a v1 notification can only mirror what Vortex's
    built-in integration already does today (§0): prompt unconditionally after every
    deployment ("check for script conflicts?"), not a badge that's conditional on
    conflicts actually being present.
  - v2 (MCP) can do the real thing: call `scan_conflicts` after deployment and only
    surface a dashboard notification when it actually returns unresolved conflicts.
  Don't build the v1 flow as if it were doing v2's job by quietly calling `merge` in
  the background to "check" — that both surprises the user (files get merged before
  they asked) and doesn't even get a preview out of it, since `merge`'s output is the
  free-text console log described in §3, not a structured conflict list.
- **A "Resolve Script Conflicts" action**, presented in Vortex's UI the same place its
  own built-in tool-launch action is today (§0), but driving this extension's headless
  flow instead of (or in addition to) the plain GUI-tool spawn. v1: click → spawn
  `merge` headlessly → use the exit code only for its coarse success/failure/partial
  category (§3's table: `0`/`1`/`2`, not a count) → get the actual per-file
  merged/skipped detail from a `MergeInventory.xml` diff taken before and after the
  run, since the CLI's own console output isn't structured enough to parse reliably.
  v2 (MCP): click → open a panel listing `scan_conflicts` results (per-file mod
  hashes, default order, already-resolved flag) → let the user pick specific files
  and/or override merge order → call `merge_conflicts` with `relativePaths`/
  `orderOverrides` → show the returned `{merged, skipped}` directly.
- **A merge history view**, backed by parsing `MergeInventory.xml` directly (as
  Vortex's own extension already does, §0) or, once available, `list_merges` over MCP
  for parity/simplicity. Show relative path, which mod folder holds the merge, and
  per-source-mod hashes — enough for a user to tell "this merge is stale" the same way
  WSM's own `MergeInventory.HasResolvedConflict` does internally.
- **A dependency/status tile**, once MCP's `get_status` is wired up: whether
  QuickBMS/wcc_lite are found (only required for bundle-content conflicts; the
  DiffPlex-based text-merge engine needs no external binary at all — see
  `docs/decisions/kdiff3-retirement.md`), resolved game/mods directories, configured
  merged-mod name, live conflict count. Useful as a single place to tell the user "your
  script-merge tooling isn't set up" before they hit a confusing failure mid-deploy.
- **Skipped/manual-resolution reporting.** Both CLI and MCP `merge_conflicts` can leave
  conflicts unresolved (DiffPlex couldn't auto-solve them). The extension should
  surface these distinctly from "nothing to do" — WSM's headless paths never open any
  GUI for these; instead they write a git/diff3-style conflict-marker sidecar file
  under `DiffPlexConflicts/` for each genuine conflict (see
  `WitcherScriptMerger/CLAUDE.md`'s "CLI mode" section). From the extension's point of
  view, a skipped file needs the user to either open that sidecar directly in their own
  editor (git-conflict-marker syntax) or launch WSM's actual GUI, which resolves the
  same conflict interactively. The extension should probably offer both as fallback
  actions rather than trying to reproduce manual conflict resolution itself.

---

## 6. Open questions

Updated against the planning effort's findings — several of the original 8 are now
resolved (marked **Resolved**), one is **Partially resolved**, and the rest remain
genuinely open (marked **Open**) because nothing found so far settles them.

1. **Relationship to `game-witcher3`'s existing built-in Script Merger integration
   (§0). Partially resolved.** The scope decision at the top of this document settles
   the headline question: this is a **companion extension**, not a replacement — it
   does not call `context.registerGame` and does not compete for `game-witcher3`'s own
   registrations. What remains genuinely open: whether the long-term plan should also
   include getting `game-witcher3` itself pointed at builds from *this* repo instead of
   the `IDCs/WitcherScriptMerger` fork it downloads today (a Vortex-core PR, not
   something this extension can do unilaterally), and how the two now-known
   coexistence hazards get handled in practice — not just "coexist" as a UX annoyance
   (the user prompted twice, or two different WSM forks/binaries on the same machine),
   but two concrete correctness hazards: `importScriptMerges()` overwriting this
   extension's merge output on Collection install (§0, §4.3), and `mergeBackup.ts`'s
   per-profile backup/restore having no concept of `MergeInventory.xml` at all (§0, new
   this round). Both need an explicit answer before real implementation, not just
   acknowledgment that they exist.
2. **Open.** Packaging/distribution strategy for QuickBMS/wcc_lite. The root `CLAUDE.md`
   is explicit that both have unresolved licensing and must never enter source
   control. Does that same caution block this extension from ever auto-downloading
   them on the user's behalf, even from a third-party mirror? Or is "detect and reuse
   whatever `game-witcher3` already fetched" (§2.2 step 3) the sanctioned answer,
   permanently, regardless of how good WSM's own self-contained-publish story gets?
   Nothing found this round resolves this — it's still a licensing/policy call for the
   repo owner.
3. **Open.** Does this become a public, Nexus-Mods-registry-listed Vortex extension,
   or stay a manually-installed/internal tool? This affects branding, support burden,
   and whether Nexus Mods' own extension review process applies. Nothing found this
   round resolves this either.
4. **Resolved.** Minimum supported WSM CLI/MCP version. A GitHub Actions release
   workflow producing self-contained single-file builds attached to GitHub Releases,
   plus a `--version` CLI flag and an MCP server-info version string, are landing
   alongside this doc's refresh (a parallel WSM-side unit — not yet in `main` as of
   this writing, so treat as imminent rather than already-available). Once live, the
   extension has something concrete to version-check a download or an already-resolved
   binary against, rather than trusting whatever a release tag happens to contain.
5. **Resolved (design-level; not yet landed in code).** Should WSM itself grow a
   config-override mechanism, instead of requiring an external caller to hand-edit
   `WitcherScriptMerger.exe.config` XML? Yes — the `WSM_<KeyName>` environment-variable
   override described in §4.1 is the answer, and is that first-class, supported
   mechanism once it exists: available to this extension, `game-witcher3`, and any
   other caller, without anyone independently reimplementing XML surgery against an
   internal config format that could change. **As of this writing, that mechanism does
   not yet exist in `WitcherScriptMerger.Core/AppSettings.cs`** — it's landing
   alongside this doc's refresh in a parallel WSM-side unit (same status as Open
   Question 4's release-workflow/`--version` work), not already merged to `main`. Until
   it lands, an implementation starting today has to fall back to the hand-edit pattern
   and its interim safety rule described in §4.1.
6. **Resolved.** Process lifecycle for MCP mode (§3): spawn per user-initiated
   workflow, tear down when the relevant panel/dashlet closes. Not a permanent
   session-long daemon, and not spawn-per-tool-call either — see §3's reasoning
   (every MCP tool call already re-scans server-side, so a longer-lived process mainly
   saves the stdio handshake across a burst of related calls). The exact
   panel/dashlet boundary is still an implementation detail for whoever writes the
   TypeScript client, not something this document needs to nail down further.
7. **Open, and now with a second concrete instance.** Concurrent-access safety with a
   running WSM GUI. This extension adds a potential concurrent writer
   (Vortex-triggered CLI/MCP invocations) to the same `MergeInventory.xml` a running
   GUI instance could also be writing to — `AppSettings.cs`'s cache-until-`Save()`
   model (§4.1) means the same class of race applies to configuration too, at least
   for any caller still hand-editing the config file instead of using the env-var
   mechanism. §0's newly-found profile-switch hazard (`mergeBackup.ts`'s
   `storeToProfile`/`restoreFromProfile`) is a related but distinct concern — that one
   is triggered by Vortex's own profile switching, not by this extension's own
   invocations, but it touches the same file. Does any of this need an explicit
   lock/mutex convention, or is "don't run WSM's GUI and this extension against the
   same install at the same time" an acceptable documented limitation for now? Nothing
   found this round resolves it either way.
8. **Open.** Linux/SteamOS timing and approach. Nexus Mods has publicly committed to
   native SteamOS support for Vortex, expected later in 2026, but that build doesn't
   exist yet and Vortex today is Windows-only. Two different questions still bundle
   together here: (a) should this extension's design assume Windows-only for its first
   real implementation and revisit Linux once Vortex-on-SteamOS actually exists, rather
   than designing for both simultaneously now; and (b), when that time comes, should a
   Linux/SteamOS Vortex drive the now-real, Linux-verified `WitcherScriptMerger.Headless`
   build natively, versus simply continuing to shell out to the existing Windows build
   under the same Proton compatibility layer Witcher 3 itself would already be running
   under (which sidesteps needing Linux-native QuickBMS/wcc_lite entirely, a bigger
   unknown than WSM's own managed-code portability, which is no longer in question —
   the Headless host is already verified on real Linux, see §2.2)? (b) is really a
   question for whoever owns Vortex's eventual SteamOS support, not this document, but
   it directly determines whether this extension ever needs to target the Linux
   Headless build specifically or can stay Windows-only forever and still serve a
   future SteamOS Vortex via Proton.

---

## Sources consulted

- This repo's root `CLAUDE.md` and each project's own `CLAUDE.md`
  (`WitcherScriptMerger/CLAUDE.md`'s CLI/MCP mode sections,
  `WitcherScriptMerger.Headless/CLAUDE.md`, `WitcherScriptMerger.Core/CLAUDE.md` and its
  `Mcp/CLAUDE.md`) — authoritative for everything WSM-side in this document. Split
  across per-project files since an earlier commit federated what used to be one
  combined `CLAUDE.md`; citations above point at the specific file now responsible for
  each claim rather than a single generic `CLAUDE.md` reference.
- [`Nexus-Mods/Vortex`, `extensions/games/game-witcher3/src/`](https://github.com/Nexus-Mods/Vortex/tree/master/extensions/games/game-witcher3/src)
  (`index.ts`, `scriptmerger.ts`, `mergeInventoryParsing.ts`, `mergeBackup.ts`,
  `eventHandlers.ts`, `modTypes.ts`) — `game-witcher3`'s current Script Merger tool
  registration, auto-download, `MergeInventory.xml` parsing, load-order locking, and
  per-profile merge-backup logic; fetched and read directly via `gh api
  repos/Nexus-Mods/Vortex/contents/...`, not inferred from a summary.
- [`Nexus-Mods/vortex-games`](https://github.com/Nexus-Mods/vortex-games) — the repo
  this document originally cited for `game-witcher3`'s source; confirmed **archived**
  via `gh api repos/Nexus-Mods/vortex-games` (`"archived": true`) this round, hence the
  citation switch above.
- [Nexus Mods Wiki — "Modding The Witcher 3 with Vortex"](https://wiki.nexusmods.com/index.php/Modding_The_Witcher_3_with_Vortex)
- [Nexus Mods Wiki — "Tool Setup: Witcher 3 Script Merger"](https://wiki.nexusmods.com/index.php/Tool_Setup:_Witcher_3_Script_Merger)
- [`Nexus-Mods/vortex-api`](https://github.com/Nexus-Mods/vortex-api), specifically its
  published `lib/api.d.ts` typings (fetched via `gh api
  repos/Nexus-Mods/vortex-api/contents/lib/api.d.ts`) — confirms no `registerTool`
  method exists, `addDiscoveredTool`'s action-creator shape, and `IRunOptions`' full
  field list (no stdio/pipe option), all cited directly in §0/§1/§3 above rather than
  asserted from memory.
- [`Nexus-Mods/Vortex` wiki — "General Introduction to Vortex extensions"](https://github.com/Nexus-Mods/Vortex/wiki/MODDINGWIKI-Developers-General-Introduction-to-Vortex-extensions) —
  re-fetched this round to confirm the documented extension entry-point convention is
  `init(context)`, not `activate(context)` as the first draft had it.
- Reporting on Nexus Mods' 2026 SteamOS/Steam Deck commitment for Vortex (PC Gamer,
  Steam Deck HQ, OpenCritic coverage of the Nexus Mods roadmap announcement).

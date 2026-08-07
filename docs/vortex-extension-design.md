# Vortex Extension Design (Unit 4)

**Status: design document only.** Nothing in this file is implemented. There is no
TypeScript/Node scaffolding anywhere in this repository, and this unit does not add
any — that work is explicitly deferred to a later, separate implementation batch. This
document exists so that future work has a starting point instead of a blank page.

**Scope**: how a Vortex extension could drive WitcherScriptMerger's (WSM's) existing
CLI and MCP interfaces. It does not propose any new WSM-side functionality beyond what
`CLAUDE.md` already documents as done today. §2.2 below also depends on two *sibling*
units of this same re-architecture batch (a self-contained single-file publish, and a
headless-only build) — those are **not** yet reflected in `CLAUDE.md` as of this
writing (confirmed by reading it in full; `HANDOFF.md` is gitignored and wasn't present
in this checkout to check), because they haven't landed yet. They come from this unit's
own task brief, not from repo documentation, and are flagged as depended-upon-but-unbuilt
everywhere they're used below, not treated as already-true facts.

---

## 0. Context: Vortex already has a Script Merger integration today

Before designing anything new, it's worth being precise about what already exists,
because a new extension has to coexist with it, not pretend it doesn't exist.

Vortex's official Witcher 3 game extension
([`Nexus-Mods/vortex-games`, `game-witcher3/index.js`](https://github.com/Nexus-Mods/vortex-games/blob/master/game-witcher3/index.js))
already integrates with a Script Merger build today. The following is verified
directly against that extension's actual source
(`gh api repos/Nexus-Mods/vortex-games/contents/game-witcher3/index.js`), not inferred
from a summary:

- It registers Script Merger as a discovered **tool** (`registerTool`/`addDiscoveredTool`,
  ID `W3ScriptMerger`), with `requiredFiles: ['WitcherScriptMerger.exe']`, and can
  **auto-download** a build from GitHub releases at
  `https://api.github.com/repos/IDCs/WitcherScriptMerger` — a *different* fork from the
  one this repo forked from (`AnotherSymbiote/WitcherScriptMerger`; see this repo's
  `CLAUDE.md` "Project overview"). It prompts the user to run it, with consent, when
  script conflicts are detected.
- **Running it launches the GUI, not a headless merge.** `runScriptMerger()` calls
  `api.runExecutable(tool.path, [], { suggestDeploy: true })` — an *empty* argument
  list. Per this repo's own `Program.cs` (`args.Length > 0` is what selects the
  CLI/MCP path at all; no args means the GUI), that's a GUI launch, not a headless
  `merge` invocation. The `IDCs/WitcherScriptMerger` fork Vortex actually downloads is
  also a different codebase from this repo, and predates this repo's CLI/MCP additions
  (see this repo's own commit history) — it likely has no headless mode to invoke even
  if Vortex wanted one. So Vortex's existing flow today is "launch the GUI, let the
  user drive KDiff3 and merge conflicts by hand, then read the result back
  afterward" — **not** a precedent for unattended/headless invocation. That distinction
  matters directly for §3 below.
- It reads and rewrites WSM's own config file at the OS level: `setMergerConfig()`
  parses `WitcherScriptMerger.exe.config` as XML and overwrites the `GameDirectory`,
  `VanillaScriptsDirectory`, and `ModsDirectory` `<add key="..." value="..."/>` entries
  in its `<appSettings>` block with paths derived from Vortex's own knowledge of the
  game install, then writes the file back — called both at initial tool setup and
  before running the merger. **This is exactly the "hand-edit the deployed
  `.exe.config`" mechanism §4.1 below proposes** — it isn't a novel idea invented for
  this design, it's an already-shipping pattern in Vortex's own codebase, which is
  reassuring precedent rather than untested ground.
- `getMergeInventory()` parses `MergeInventory.xml` directly (`<MergedModName>`,
  `<IncludedMod>` elements) — the same file this repo's `Inventory/MergeInventory.cs`
  owns via `XmlSerializer`.
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

This means a brand-new Vortex extension isn't filling a total void; it's a second,
more capable integration point that has to decide its relationship to the built-in one
(see the open questions in §6). It also means the config-file-editing and
load-order-locking "hard problems" already have a proven answer in Vortex's own
codebase (leaned on directly in §4 below) — but headless/unattended invocation of WSM
specifically does **not** have an existing precedent in Vortex's codebase; that part
is genuinely new ground for §3's recommendation to reckon with honestly.

*(Sources: [`game-witcher3/index.js`](https://github.com/Nexus-Mods/vortex-games/blob/master/game-witcher3/index.js),
fetched and read directly via `gh api`;
[Nexus Mods wiki, "Modding The Witcher 3 with Vortex"](https://wiki.nexusmods.com/index.php/Modding_The_Witcher_3_with_Vortex);
[Vortex Wiki, "Tool Setup: Witcher 3 Script Merger"](https://wiki.nexusmods.com/index.php/Tool_Setup:_Witcher_3_Script_Merger).)*

---

## 1. Tech stack

Vortex extensions are **TypeScript/Node**, built against the
[`vortex-api`](https://github.com/Nexus-Mods/vortex-api) package and Vortex's own
extension conventions (an `info.json` manifest, an entry point exporting a single
`activate(context)` function, `context.registerAction`/`registerTool`/etc.). That is a
completely different toolchain from this repo's .NET/WinForms solution — there is no
sensible way to fold it into `WitcherScriptMerger.sln`.

Consequence for repo layout: **this must live in a separate package/repo**, not a
folder inside `WitcherScriptMerger/`. Candidate options (to be decided when this unit
is actually implemented, not now):

- A new sibling repo, e.g. `witcherscriptmerger-vortex`, with its own `package.json`,
  its own CI, its own release cadence tied to (but independent of) WSM's own releases.
- A `vortex-extension/` top-level folder in *this* repo, kept fully outside the `.sln`
  and `dotnet build`'s reach, if the owner prefers single-repo convenience over clean
  separation.

Either way: no Node tooling, `package.json`, or `node_modules` should ever need to
appear anywhere `dotnet build WitcherScriptMerger.sln` looks.

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
running `merge` and/or `mcp` mode (see §3). Today, this means the full Windows build —
`WitcherScriptMerger.exe`, which still requires KDiff3/QuickBMS/wcc_lite on disk per
`CLAUDE.md`'s "External tool dependencies" — since no unit in this batch has shipped a
KDiff3/QuickBMS/wcc_lite-free build yet.

Two other units in this same re-architecture batch are directly relevant here, and
this design explicitly depends on them without assuming either is done:

- A **self-contained single-file publish profile** for WSM (full GUI+CLI+MCP build).
  This is what the extension would most plausibly bundle or download — a single `.exe`
  with the .NET runtime baked in, no separate .NET install required on the user's
  machine.
- A **lighter-weight headless-only build** (CLI+MCP, no WinForms/GUI), explicitly
  called out as a candidate for eventual Linux support. Vortex itself is Windows-only
  today, and Nexus Mods has publicly committed to native SteamOS support for Vortex,
  expected to land later in 2026 — so a Linux-capable WSM CLI host is *plausibly*
  relevant to this extension eventually, not purely speculative. That said, don't
  over-read this as an established requirement: KDiff3/QuickBMS/wcc_lite would still
  need Linux-native builds or a compatibility layer for a truly native Linux merge
  pipeline to work at all, and a simpler alternative might make a dedicated Linux WSM
  build unnecessary in the near term — a Linux/SteamOS Vortex could plausibly just keep
  shelling out to the existing Windows WSM build the same way SteamOS already runs
  unmodified Windows games via Proton, the same compatibility layer Witcher 3 itself
  would already be running under on that platform. Which path is actually right isn't
  something this document can resolve — flagged as an open question in §6 rather than
  assumed here.

Setup flow, once those artifacts exist:

1. On first activation (or on first use of a script-merge action), the extension
   checks for a cached WSM binary in its own extension-private storage.
2. If absent, it either (a) unpacks a bundled copy shipped inside the extension
   package itself, or (b) downloads the self-contained publish artifact from a WSM
   GitHub release, similar to how `game-witcher3/index.js` already downloads the
   `IDCs/WitcherScriptMerger` fork today (see §0) — verify via checksum before trusting
   it.
3. **KDiff3/QuickBMS/wcc_lite are a separate problem the extension cannot solve by
   bundling WSM alone.** Per `CLAUDE.md`, none of the three are in WSM's own source
   control — QuickBMS and wcc_lite specifically because their licensing is unresolved,
   and that constraint doesn't go away just because a different project is doing the
   downloading. The self-contained publish profile does not change this: it packages
   WSM's own managed code, not these three external binaries. The extension has to
   either point at an existing local install of these tools (e.g., detect the
   `IDCs/WitcherScriptMerger` fork Vortex may have already downloaded per §0, and
   reuse its `Tools\` subfolder) or prompt the user to source them the same way WSM's
   own README does. This should not be silently glossed over — see §6.
4. The extension writes the resolved WSM binary path into its own settings, and
   surfaces it (read-only or editable) in Vortex's per-game settings panel so the user
   can override it if they already have a WSM install they prefer.

---

## 3. Invocation model

WSM exposes two non-GUI surfaces today (`CLAUDE.md` "CLI mode" / "MCP mode"):

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
  scans without merging, and nothing in `CLAUDE.md` suggests one is planned. Building
  that preview by having the extension re-implement WSM's own conflict-scanning logic
  (walking mods, comparing hashes) would duplicate `FileIndex/ModFileIndex.cs` outside
  this repo — exactly the kind of "invent new capability" this design is supposed to
  avoid. The only way to get a real preview without duplicating that logic is to call
  into WSM itself, which means MCP's `scan_conflicts`.
- MCP also gives per-file targeting (`relativePaths`) and per-file order overrides
  (`orderOverrides`) as first-class, structured input/output, versus the CLI's
  all-conflicts-every-time behavior and free-text console output. A "merge just this
  one file, in this order" UX action needs MCP.

So: ship the CLI-driven "spawn `merge`, wait, refresh the mod list from
`MergeInventory.xml`" flow first, as the low-risk default for the core "resolve script
conflicts" action. Treat MCP as a v2 enhancement that unlocks conflict preview,
per-file merge actions, and a live dependency/status check (`get_status`) surfaced in
Vortex's UI — gated on someone actually writing (or importing) a TypeScript MCP client
and deciding on the child-process lifecycle model (spawn per action-and-tear-down vs.
spawn-once-per-session; `CLAUDE.md` notes every MCP tool call already re-scans and
re-loads from scratch server-side, so a long-lived process mainly saves the stdio
handshake, not server-side work — a "spawn per user-initiated workflow, tear down when
the panel closes" middle ground is probably the sweet spot, not a permanent
session-long daemon).

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
installs mods by hand. What the extension *does* need to do is make sure the deployed
WSM instance's `GameDirectory`/`ModsDirectory` settings actually point at the game
install Vortex is managing. Since WSM has no CLI/MCP flag for this, the only way to do
that today is for the extension to write those two keys directly into the deployed
`WitcherScriptMerger.exe.config` XML file before invoking WSM (Vortex already knows the
exact game install path — that's central to what a game extension does). **This isn't
a novel proposal** — it's exactly what Vortex's existing `setMergerConfig()` already
does in production (§0), down to the same file name and the same `GameDirectory`/
`VanillaScriptsDirectory`/`ModsDirectory` keys, which is good evidence the approach
works in practice, not just in theory.

That precedent doesn't retire the concurrency question, though: `AppSettings.cs`
caches its `Configuration` object and only persists on an explicit `Save()`, and
`CLAUDE.md`'s MCP section already flags a documented risk of a concurrently-running
GUI WSM instance clobbering `MergeInventory.xml`. The same class of race applies here —
if the extension hand-edits `WitcherScriptMerger.exe.config` while a WSM process (GUI
or CLI/MCP) is already running against the old config, results are undefined. Vortex's
own `setMergerConfig()` doesn't appear to guard against this either (it's a
best-effort file write with a bare try/catch), so this is an inherited risk, not a
solved one — the extension should still only edit the config file when it's about to
spawn a fresh WSM process, never while one is already running.

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
which tool produced them. What *does* need explicit handling is the write side: keep
WSM's `App.config` pointed at the right `GameDirectory`/`ModsDirectory` before each
invocation, don't let `MergedModName` drift from whatever prefix Vortex's load-order
locking expects, only scan/merge after deployment (§4.4), and reconcile WSM's merge
output back into Vortex's own deployment/mod tracking afterward (§4.3) rather than
assuming Vortex will pick it up automatically.

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
  KDiff3/QuickBMS/wcc_lite are all found, resolved game/mods directories, configured
  merged-mod name, live conflict count. Useful as a single place to tell the user "your
  script-merge tooling isn't set up" before they hit a confusing failure mid-deploy.
- **Skipped/manual-resolution reporting.** Both CLI and MCP `merge_conflicts` can leave
  conflicts unresolved (KDiff3 couldn't auto-solve). The extension should surface these
  distinctly from "nothing to do" — WSM's headless paths never open KDiff3's GUI for
  these (`CLAUDE.md`'s CLI/MCP sections), so from the extension's point of view a
  skipped file needs the user to run WSM's actual GUI to resolve it manually. The
  extension should probably offer a "launch WSM GUI" fallback action here, rather than
  trying to reproduce manual conflict resolution itself.

---

## 6. Open questions

For the repo owner to answer before any real implementation starts:

1. **Relationship to Vortex's existing built-in Script Merger integration (§0).**
   Should this new extension replace it, coexist alongside it, or should the long-term
   plan instead be to get Vortex's *own* `game-witcher3` extension pointed at builds
   from *this* repo instead of the `IDCs/WitcherScriptMerger` fork it uses today (a
   Vortex-core PR, not something this extension can do unilaterally)? "Coexist" isn't
   just a UX annoyance (the user prompted twice, or two different WSM
   forks/binaries downloaded onto the same machine) — §0 and §4.3 found a concrete
   correctness hazard too: Vortex's existing `importScriptMerges()` path can overwrite
   this extension's own merge output when a Collection bundling script merges is
   installed. Any coexistence answer needs to account for that, not just the
   double-prompt annoyance.
2. **Packaging/distribution strategy for KDiff3/QuickBMS/wcc_lite.** WSM's own
   `CLAUDE.md` is explicit that QuickBMS and wcc_lite have unresolved licensing and
   must never enter source control. Does that same caution block this extension from
   ever auto-downloading them on the user's behalf, even from a third-party mirror?
   Or is "detect and reuse whatever Vortex's existing integration already fetched"
   (§2.2 step 3) the sanctioned answer, permanently, regardless of how good WSM's own
   self-contained-publish story gets?
3. **Does this become a public, Nexus-Mods-registry-listed Vortex extension**, or stay
   a manually-installed/internal tool? This affects branding, support burden, and
   whether Nexus Mods' own extension review process applies.
4. **Minimum supported WSM CLI/MCP version.** Once the extension exists, it needs a
   compatibility contract with WSM releases — does it pin to a specific tag, accept
   any build advertising the `merge`/`mcp` verbs, or version-negotiate somehow? Nothing
   in WSM today exposes a `--version` flag or an MCP server-info version string beyond
   whatever the `ModelContextProtocol` SDK provides by default — worth checking before
   committing to a specific compatibility mechanism.
5. **Should WSM itself grow a config-override mechanism** (CLI flags, environment
   variables, or a `--config` path) for `GameDirectory`/`ModsDirectory`/
   `MergedModName`, instead of requiring an external caller to hand-edit
   `WitcherScriptMerger.exe.config` XML (§4.1)? Note this isn't blocked on unproven
   ground — Vortex's own `setMergerConfig()` already does the hand-edit today (§0), so
   "it works" isn't really in question. The actual question is whether WSM should offer
   a first-class, supported alternative so every caller (this extension, Vortex's
   existing integration, anyone else) isn't independently reimplementing XML surgery
   against an internal config format that could change. That's WSM-side follow-on
   work, not something this extension can substitute for.
6. **Process lifecycle for MCP mode** (§3): spawn-per-action-and-tear-down vs.
   spawn-once-and-keep-alive for the extension's lifetime vs. something in between.
   Needs a decision once someone is actually writing the TypeScript client, informed by
   real measurements of WSM's own startup/dependency-validation cost, not guessed here.
7. **Concurrent-access safety with a running WSM GUI.** `CLAUDE.md` already flags this
   risk for MCP-vs-GUI concurrency; this extension adds a third potential concurrent
   writer (Vortex-triggered CLI/MCP invocations) to the same `MergeInventory.xml` and
   `App.config`. Does this need an explicit lock/mutex convention across all three, or
   is "don't run WSM's GUI and this extension against the same install at the same
   time" an acceptable documented limitation for now?
8. **Linux/SteamOS timing and approach.** Nexus Mods has publicly committed to native
   SteamOS support for Vortex, expected later in 2026, but that build doesn't exist yet
   and Vortex today is Windows-only. Two different questions bundle together here: (a)
   should this extension's design assume Windows-only for its first real
   implementation and revisit Linux once Vortex-on-SteamOS actually exists, rather than
   designing for both simultaneously now; and (b), when that time comes, should a
   Linux/SteamOS Vortex drive a native headless-Linux WSM build at all, versus simply
   continuing to shell out to the existing Windows build under the same Proton
   compatibility layer Witcher 3 itself would already be running under (which sidesteps
   needing Linux-native KDiff3/QuickBMS/wcc_lite entirely, a much bigger unknown than
   WSM's own managed-code portability)? (b) is really a question for whoever owns the
   Linux-support unit of this batch, not this document, but it directly determines
   whether the headless-only build this section depends on ever needs to target Linux
   specifically or can stay Windows-only forever and still serve a future SteamOS
   Vortex via Proton.

---

## Sources consulted

- This repo's `CLAUDE.md` (CLI mode, MCP mode, Settings & persistence, External tool
  dependencies sections) — authoritative for everything WSM-side in this document.
- [`Nexus-Mods/vortex-games`, `game-witcher3/index.js`](https://github.com/Nexus-Mods/vortex-games/blob/master/game-witcher3/index.js) —
  Vortex's existing Script Merger tool registration, auto-download, `MergeInventory.xml`
  parsing, and load-order locking logic.
- [Nexus Mods Wiki — "Modding The Witcher 3 with Vortex"](https://wiki.nexusmods.com/index.php/Modding_The_Witcher_3_with_Vortex)
- [Nexus Mods Wiki — "Tool Setup: Witcher 3 Script Merger"](https://wiki.nexusmods.com/index.php/Tool_Setup:_Witcher_3_Script_Merger)
- [`Nexus-Mods/vortex-api`](https://github.com/Nexus-Mods/vortex-api) — Vortex extension
  API/typings.
- [`Nexus-Mods/Vortex` wiki — "General Introduction to Vortex extensions"](https://github.com/Nexus-Mods/Vortex/wiki/MODDINGWIKI-Developers-General-Introduction-to-Vortex-extensions)
- Reporting on Nexus Mods' 2026 SteamOS/Steam Deck commitment for Vortex (PC Gamer,
  Steam Deck HQ, OpenCritic coverage of the Nexus Mods roadmap announcement).

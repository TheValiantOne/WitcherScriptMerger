# witcherscriptmerger-vortex

A [Vortex](https://www.nexusmods.com/about/vortex/) (Nexus Mods' mod manager) companion
extension for WitcherScriptMerger (WSM). It is **not** a replacement for Vortex's own
built-in Witcher 3 game extension (`game-witcher3`) or that extension's built-in Script
Merger integration - it's a companion that adds a second, distinctly-branded discovered
tool and its own conflict-scanning/merge/history/status UI, gated on Witcher 3 being the
active game, alongside whatever `game-witcher3` already does. It never calls
`context.registerGame` and never touches `game-witcher3`'s own registrations. See
`docs/vortex-extension-design.md` at the repo root for the full design rationale (section
0 in particular, for exactly what `game-witcher3` already does on its own).

**Not yet published to Vortex's in-app extension registry.** Installing it today means
building it yourself and dropping it into your own Vortex plugins folder - see "Install"
below. Whether this ever becomes a publicly-listed extension is still an open decision
(design doc, section 6, Open Question 3) - nothing here should be read as implying
otherwise.

## What it does

Everything below is gated on Witcher 3 being Vortex's currently active game - none of it
does anything for any other game.

- **Acquires a WSM build automatically** (`src/toolAcquisition.ts`): downloads the
  `WitcherScriptMerger.Headless-<version>-win-x64.zip` release asset from this repo's own
  GitHub Releases (`TheValiantOne/WitcherScriptMerger`), verifies the downloaded byte
  count against what GitHub's API reported (a transfer-completeness check, not a
  cryptographic signature), extracts it into this extension's own private storage
  (under Vortex's `userData` folder), and registers it as a Vortex discovered tool -
  `WitcherScriptMergerEnhanced` ("WitcherScriptMerger (Enhanced)" / "WSM+"), a
  deliberately different tool ID from `game-witcher3`'s own `W3ScriptMerger` (which
  downloads and launches a different, older WSM fork's GUI). **As of this writing, no
  version tag has been pushed to this repo, so no GitHub Release actually exists yet** -
  the download logic itself is real and unit-tested against a mocked HTTP client, and the
  rest of the pipeline (extract, install-marker, tool registration, `WSM_<Key>`
  environment-variable configuration reaching a real spawned process) is proven end-to-end
  in `test/toolAcquisition.integration.test.ts` using a locally-built binary standing in
  for a downloaded one - but the actual GitHub-Releases download step has never been
  exercised against a real release.
- **Scans for script conflicts after every deployment** (`src/conflictScan.ts`,
  `src/conflictNotifications.ts`): once a WSM build has been acquired, every Vortex
  `did-deploy` for Witcher 3 triggers a short-lived WSM MCP process, calls its
  `scan_conflicts` tool, and shows (or updates/dismisses) a dashboard notification when
  the set of *unresolved* conflicts has actually changed since the last check that Vortex
  session - not an unconditional "check for conflicts?" prompt on every deploy.
  Suppressed while a mod/dependency install (e.g. installing a Collection) is still in
  progress, so a burst of deploy-per-mod cycles doesn't spawn a WSM process per mod or
  show a stale mid-install notification.
- **A "Resolve Script Conflicts" action** (`src/resolveAction.ts`,
  `src/mergePanel.ts`): a button on the Mods page toolbar. Clicking it spawns a WSM MCP
  process for a dry-run preview (`merge_conflicts({dryRun: true})`), shows a dialog with
  merged/skipped/unmatched counts plus any function-level merge decisions (cases where a
  whole-file merge failed but merging function-by-function succeeded), and - only on
  confirmation - spawns a second process to run the real merge and shows its result. v1
  scope, deliberately: merges every detected conflict in one pass; there's no per-file
  selection or custom merge-order override yet. Both `merge_conflicts` calls get their own
  ten-minute deadline (`MERGE_CALL_TIMEOUT_MS`) rather than `mcpClient.ts`'s
  general-purpose 30s `DEFAULT_REQUEST_TIMEOUT_MS` — a merge's runtime scales with the
  load order, and the dry-run preview costs the same as the merge it previews (it does the
  full three-way merge and only skips the writes). The deadline is passed per call, so the
  `initialize` handshake keeps the short default and a WSM process that fails to start
  still fails fast.
- **A merge-history dashboard tile** (`src/mergeHistoryDashlet.ts`): lists every merge
  WSM has already recorded (via its MCP `list_merges` tool) - relative path, which merged
  mod folder holds the result, and each source mod's recorded hash - with a manual
  Refresh button.
- **A dependency/status dashboard tile** (`src/statusTile.ts`,
  `src/wsmStatusSummary.ts`): shows whether WSM's text-merge engine and bundle-content
  tooling (QuickBMS/wcc_lite) are ready, the resolved mods directory (and whether it
  exists), the configured merged-mod name, and a live conflict count - so a setup problem
  shows up here instead of as a confusing failure mid-deploy. Also offers a "Get wcc_lite
  from Nexus Mods" button - see the next section for exactly what that does.

### First-run setup (both former "known gaps" are closed)

- **The initial WSM download has an in-Vortex trigger now**: the status dashboard tile's
  "Download WitcherScriptMerger v\<version\>" button (shown whenever no WSM build is
  resolved yet) runs the full download/verify/extract/register pipeline
  (`src/toolAcquisition.ts`) against this repository's own GitHub release, pinned to
  the version in `src/githubRelease.ts`'s `DEFAULT_WSM_VERSION`. Downloads stay an
  explicit user action - nothing downloads automatically at startup.
- **You can point the extension at an existing WSM install instead**: the same tile's
  "Use an existing install..." button stores an override path
  (`src/wsmToolPath.ts`; persisted as `tool-path-override.txt` in the extension's
  private storage). The override must name a `WitcherScriptMerger*.exe` with `mcp`
  support - either host from this fork works; the original 2016 Script Merger does not.
  An override always wins over the extension-managed install; if its file later
  disappears, the tile says so and offers to clear it (never a silent fallback to a
  different binary than the one you chose). **The install's `.dll.config` file needs to
  sit right next to whichever exe is used** - WSM reads settings via
  `ConfigurationManager` against that file, and its `AppSettings` constructor calls
  `Environment.Exit(1)` with no further diagnostic if it can't find one, so a bare exe
  fails silently on launch.
- Manual placement still works too: an already-built `WitcherScriptMerger.Headless.exe`
  (plus its `.dll.config`) under `<Vortex userData>\witcherscriptmerger-vortex\tool\`
  (Vortex's `userData` is typically `%APPDATA%\Vortex`) is re-registered as a
  discovered tool on every load and game-mode switch, network-free.

## Being transparent about what gets downloaded, from where, and by whom

Two different automatic downloads exist, and neither is this extension (or WSM)
bundling/redistributing anything itself:

- **The WSM build itself** (`src/githubRelease.ts`/`src/toolAcquisition.ts`) is a plain
  HTTPS GET against `api.github.com`, fetching a build produced by *this same repository's
  own* `.github/workflows/release.yml` - i.e. WSM downloading itself, essentially, the
  same way any tool auto-updater would.
- **wcc_lite** (`src/wccLiteAcquisition.ts`, `src/nexusDownloader.ts`) - needed only for
  `.bundle`-content (DLC/expansion) conflicts, never for flat-file `.ws`/`.xml` conflicts
  - is fetched differently: through Vortex's *own* Nexus Mods integration
  (`api.ext.nexusDownload`), using the user's own already-authenticated Nexus session,
  from the "Official ModKit" mod page (Nexus mod id 3173 on the `witcher3` domain,
  published by CD Projekt RED) - the same official tool WSM's own GUI
  (`DependencyForm.cs`) already points users at manually. It's downloaded with
  **`allowInstall: false`**, specifically so Vortex never deploys or load-orders it as a
  mod - it lands in this extension's own private storage
  (`<Vortex userData>\witcherscriptmerger-vortex\bundle-tools\wcc_lite\`), not the game's
  Mods folder. This extension does not host, mirror, or repackage wcc_lite anywhere; it
  only automates the same manual "go get it from Nexus" step a user would otherwise do by
  hand, through Vortex's own download machinery.
  - **This has not been independently confirmed against Nexus Mods'/CD Projekt Red's own
    redistribution terms** beyond "it's an official tool hosted on an official Nexus mod
    page" - the root `CLAUDE.md` already treats QuickBMS/wcc_lite packaging/licensing as
    an open decision requiring the repo owner's sign-off, and auto-fetching at runtime
    into a Vortex-managed location (rather than committing to source control, which this
    still never does) is a related but distinct question that is *also* still open. See
    `docs/vortex-extension-design.md`, section 6, Open Question 2.
- **QuickBMS** is never downloaded automatically by this extension at all (no canonical
  Nexus-hosted release was found for it, and its redistribution terms are murkier than
  wcc_lite's) - the status tile only detects an existing local install or links to
  QuickBMS's own homepage, mirroring WSM's own GUI for this exact dependency.

## Install (manual - not yet published anywhere)

```
cd vortex-extension
npm install
npm run package
```

`npm run package` runs the typecheck + webpack build (`npm run build`) and then stages
the result into a distributable zip: `dist/index.js`(`.map`) plus the root `info.json`
manifest, copied flat (no nested subfolder) into
`release/witcherscriptmerger-vortex-<version>.zip` (and an equivalent unzipped
`release/witcherscriptmerger-vortex/` folder, if you'd rather copy files directly).
`release/` is gitignored - nothing under it is ever committed.

To install: extract that zip's contents (or copy the staged folder's contents) so
`index.js` and `info.json` land directly inside

```
%APPDATA%\Vortex\plugins\witcherscriptmerger-vortex\
```

The folder name under `plugins\` is arbitrary - `info.json` declares no explicit `id`
field (`@nexusmods/vortex-api`'s own `IExtension` typing marks it optional), so nothing
in Vortex's own manifest format requires a specific folder name. `witcherscriptmerger-vortex`
above just matches this project's own `package.json` name and its npm/git identity, for
consistency with everything else this project already calls itself. Restart Vortex (or
use its "Extensions" page reload, if available) afterward to pick it up.

If you'd rather do this by hand without the packaging script: `npm run build` alone
produces `dist/index.js` (+ `dist/index.js.map`), and you'd need to copy both of those
plus the root `info.json` into the same plugins subfolder yourself.

## Requirements

- **A WSM build capable of `mcp` mode** - either the CLI/MCP-only
  `WitcherScriptMerger.Headless.exe` the status tile's download button acquires for you
  (see "First-run setup" above, which also covers pointing at an existing install or
  placing one manually), or the full WinForms `WitcherScriptMerger.exe`, which also
  supports `mcp` mode. Either way, this is a Windows-only requirement today, matching
  Vortex itself being Windows-only.
- **QuickBMS and wcc_lite are only needed for `.bundle`-content (DLC/expansion)
  conflicts** - ordinary flat-file `.ws`/`.xml` conflicts merge with neither installed,
  via WSM's in-process DiffPlex-based merge engine. See "Being transparent..." above for
  exactly how (and whether) this extension can get wcc_lite for you; QuickBMS is always
  a manual, user-sourced install (see that section).

## This is a separate toolchain

Everything under this folder is TypeScript/Node, built with its own `package.json`,
independent of the rest of this repository's .NET solution
(`WitcherScriptMerger.sln`). `dotnet build`/`dotnet format` at the repo root never look
inside this folder, and nothing here is reachable from them.

## Dev workflow

```
npm run typecheck           # tsc --noEmit
npm run build               # typecheck + webpack bundle -> dist/index.js
npm run package             # build + stage/zip for manual install - see "Install" above
npm run lint
npm test                    # fast, Node-only unit tests
npm run test:integration    # slower, real-process integration tests (needs the .NET SDK)
```

Two-tier test convention:

- `src/**/*.test.ts` (run by `npm test`) - fast, mocked-dependency unit tests. No .NET
  SDK needed, so a Node-only environment (a contributor machine or CI runner without
  `dotnet` on `PATH`) can iterate on this extension's own TypeScript without ever
  touching the .NET side.
- `test/**/*.integration.test.ts` (run by `npm run test:integration`, `--no-file-parallelism`)
  - real, no-mocks tests that spawn an actual `WitcherScriptMerger.Headless` process.
  Two different invocations are involved: `test/mcpClient.integration.test.ts` runs a
  plain `dotnet build` itself if the exe isn't already present (framework-dependent,
  fast); `test/toolAcquisition.integration.test.ts` instead runs
  `dotnet publish -c Release -p:PublishProfile=win-x64` (self-contained, single-file,
  matching `.github/workflows/release.yml`'s own publish step exactly) if that specific
  publish output isn't already present - slower on a cold run (produces a large,
  standalone exe), since it stands in for a downloaded-and-extracted release asset that
  the plain `dotnet build` output doesn't represent.

## License

GPLv2, matching the root `LICENSE` file - this folder isn't separately licensed.

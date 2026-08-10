import * as fs from 'fs';
import * as path from 'path';
import { selectors, types } from 'vortex-api';
import { WITCHER3_GAME_ID } from './gating';
import { getBundleToolsDir } from './storage';

/**
 * Local-only (no network) detection for WSM's two bundle-content dependencies -
 * QuickBMS (quickbms.exe + witcher3.bms) and wcc_lite (wcc_lite.exe), neither of which
 * is committed to this repo's own source control (see the root CLAUDE.md's "External
 * tool dependencies & licensing"). Checks two locations, matching what WSM's own GUI
 * `DependencyForm`/this repo's own `App.config` default paths already encode as the
 * conventional on-disk layout for these tools:
 *
 *   1. This extension's own managed install, under `getBundleToolsDir(api)` (storage.ts)
 *      - `wcc_lite/bin/x64/wcc_lite.exe`, `QuickBMS/quickbms.exe` +
 *      `QuickBMS/witcher3.bms` - the exact relative layout
 *      `WitcherScriptMerger/App.config`'s own `WccLitePath`/`QuickBmsPath`/
 *      `QuickBmsPluginPath` defaults already assume (`Tools\wcc_lite\bin\x64\wcc_lite.exe`,
 *      `Tools\QuickBMS\quickbms.exe`, `Tools\QuickBMS\witcher3.bms`).
 *   2. A prior install of the separate `IDCs/WitcherScriptMerger` fork - the one
 *      Vortex's own built-in `game-witcher3` extension already downloads as its
 *      `W3ScriptMerger` discovered tool (see `discoveredTool.ts`'s own doc comment, and
 *      `docs/vortex-extension-design.md` §0/§2.2 Open Question 2's "detect and reuse
 *      whatever game-witcher3 already fetched" suggestion). That fork's own `Tools\`
 *      subfolder, sitting beside whichever `WitcherScriptMerger.exe` `game-witcher3`
 *      downloaded, uses this exact same relative layout - not a guess, it's this
 *      repo's own `App.config` default paths, inherited from the same WSM lineage.
 *
 * wcc_lite additionally falls back to a depth-bounded search under this extension's own
 * `wcc_lite/` subfolder (see `BUNDLE_TOOL_SEARCH_MAX_DEPTH` below) - `wccLiteAcquisition.ts`
 * downloads wcc_lite from its real Nexus Mods "Official ModKit" release, whose exact
 * internal zip layout was never verified against a live download (no Nexus API key or
 * scraping access in this environment - see that module's own doc comment), so it may
 * not land at the exact canonical relative path above. QuickBMS is never auto-downloaded
 * by this extension at all (see `QUICKBMS_HOMEPAGE_URL` below), so there's no
 * "unknown archive layout" case to hedge against for it - only the two exact,
 * known-convention locations are checked.
 */

const IDCS_SCRIPT_MERGER_TOOL_ID = 'W3ScriptMerger';

const QUICKBMS_EXE_RELATIVE = path.join('QuickBMS', 'quickbms.exe');
const QUICKBMS_PLUGIN_RELATIVE = path.join('QuickBMS', 'witcher3.bms');
const WCC_LITE_EXE_RELATIVE = path.join('wcc_lite', 'bin', 'x64', 'wcc_lite.exe');
export const WCC_LITE_EXE_FILENAME = 'wcc_lite.exe';
export const WCC_LITE_SUBDIR = 'wcc_lite';

/** How many directory levels deep the wcc_lite fallback search descends - bounded so a
 *  large extracted ModKit tree (likely far more than just wcc_lite.exe - see
 *  `wccLiteAcquisition.ts`) can't make every detection call slow. */
export const BUNDLE_TOOL_SEARCH_MAX_DEPTH = 6;

/** QuickBMS's own homepage - never redistributed by this extension (see this module's
 *  own doc comment); `WitcherScriptMerger/Forms/DependencyForm.cs` (the WinForms host's
 *  own GUI) links here too, verbatim. */
export const QUICKBMS_HOMEPAGE_URL = 'http://aluigi.altervista.org/quickbms.htm';

export interface DetectedBundleTools {
  quickBmsPath?: string;
  quickBmsPluginPath?: string;
  wccLitePath?: string;
}

export function isEnoent(err: unknown): boolean {
  return typeof err === 'object' && err !== null && (err as NodeJS.ErrnoException).code === 'ENOENT';
}

/** Mirrors `toolAcquisition.ts`'s own `pathExists` exactly (including the rationale for
 *  rethrowing anything other than ENOENT - a permission/lock error must not be
 *  silently treated as "not installed", which would both hide the real problem and
 *  risk offering to re-download/re-extract a tool that's actually already present).
 *  Not imported from `toolAcquisition.ts` since that file doesn't export it and this
 *  unit's own instructions treat that file's *existing* logic as off-limits to modify -
 *  but exported from here (unlike that file's private copy) specifically so
 *  `wsmStatusSummary.ts` can reuse *this* one instead of adding a third duplicate. */
export async function fileExists(target: string): Promise<boolean> {
  try {
    await fs.promises.access(target);
    return true;
  } catch (err) {
    if (isEnoent(err)) {
      return false;
    }
    throw err;
  }
}

/**
 * Depth-bounded, level-order (breadth-first) search for a file named
 * (case-insensitively) `targetFileName` under `rootDir`. Every directory at the
 * current depth is checked for a direct match before any directory at the next depth
 * is examined at all, so a shallower match always wins over a deeper one regardless of
 * sibling-directory iteration order (which `fs.readdir` does not guarantee is
 * alphabetical). Returns `undefined` (rather than throwing) when `rootDir` doesn't
 * exist yet (or a subdirectory disappears mid-search - a benign race, not a real
 * problem) or nothing matches within `maxDepth` levels. Any *other* `readdir` failure
 * (EACCES/EPERM/EBUSY - e.g. an antivirus lock on a freshly-extracted tree) propagates
 * rather than being silently treated as "nothing here": a caller like
 * `wccLiteAcquisition.ts` that gets a false "not found" from a transient error, rather
 * than a real one, could go on to wipe and re-download a perfectly good install (see
 * that module's own `fs.promises.rm` call after this function reports no existing
 * install).
 *
 * When more than one match exists at the same (shallowest) depth level, prefers a path
 * containing an `x64` path segment - this repo's own `App.config` default
 * (`Tools\wcc_lite\bin\x64\wcc_lite.exe`) specifically targets the x64 build, and a
 * general-purpose Witcher 3 modding-tools archive could plausibly ship both x86 and
 * x64 builds side by side at the same depth, where directory-listing order alone
 * (`fs.readdir` gives no ordering guarantee) would otherwise pick between them
 * arbitrarily.
 */
export async function findFileByNameBounded(
  rootDir: string,
  targetFileName: string,
  maxDepth: number,
): Promise<string | undefined> {
  const targetLower = targetFileName.toLowerCase();
  let currentLevelDirs = [rootDir];

  for (let depth = 0; depth <= maxDepth && currentLevelDirs.length > 0; depth++) {
    const nextLevelDirs: string[] = [];
    const matchesAtThisLevel: string[] = [];

    for (const dir of currentLevelDirs) {
      let entries: fs.Dirent[];
      try {
        entries = await fs.promises.readdir(dir, { withFileTypes: true });
      } catch (err) {
        if (isEnoent(err)) {
          continue;
        }
        throw err;
      }

      for (const entry of entries) {
        if (entry.isFile() && entry.name.toLowerCase() === targetLower) {
          matchesAtThisLevel.push(path.join(dir, entry.name));
        } else if (entry.isDirectory()) {
          nextLevelDirs.push(path.join(dir, entry.name));
        }
      }
    }

    if (matchesAtThisLevel.length > 0) {
      const x64Match = matchesAtThisLevel.find((match) => /(^|[\\/])x64([\\/]|$)/i.test(match));
      return x64Match ?? matchesAtThisLevel[0];
    }

    currentLevelDirs = nextLevelDirs;
  }

  return undefined;
}

/** Sibling `Tools\` folder for a prior `IDCs/WitcherScriptMerger` install that Vortex's
 *  own `game-witcher3` extension may have already downloaded as its `W3ScriptMerger`
 *  discovered tool - `undefined` if no such tool has been discovered.
 *
 *  Loosely typed rather than `types.IDiscoveryResult` on purpose: the runtime `vitest`
 *  stub (`test/testUtils/vortexApiStub.ts`) backing `selectors.discoveryByGame` in
 *  tests returns a deliberately simplified fake shape (see that stub's own doc
 *  comment) with no `tools` field at all - reading `.tools` off it at runtime is still
 *  safe (a plain missing-property read, not a type error), so this only needs a shape
 *  loose enough that both the real and fake return values satisfy it. */
function getIdcsForkToolsDir(api: types.IExtensionApi): string | undefined {
  const discovery = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID) as
    | { tools?: Record<string, { path?: string } | undefined> }
    | undefined;
  const toolPath = discovery?.tools?.[IDCS_SCRIPT_MERGER_TOOL_ID]?.path;
  return toolPath ? path.join(path.dirname(toolPath), 'Tools') : undefined;
}

function candidateRoots(api: types.IExtensionApi): string[] {
  const roots = [getBundleToolsDir(api)];
  const idcsToolsDir = getIdcsForkToolsDir(api);
  if (idcsToolsDir) {
    roots.push(idcsToolsDir);
  }
  return roots;
}

/**
 * Detects an already-installed QuickBMS (exe + plugin, both required to count as
 * "found") - never downloads anything. Per the root CLAUDE.md, QuickBMS's
 * redistribution terms are murkier than wcc_lite's (no canonical Nexus-hosted release
 * found), so this extension only ever detects an existing install or points the user at
 * QuickBMS's own homepage (`QUICKBMS_HOMEPAGE_URL`) to source it themselves - mirroring
 * `WitcherScriptMerger/Forms/DependencyForm.cs`'s own behavior for this exact
 * dependency.
 */
export async function detectQuickBms(
  api: types.IExtensionApi,
): Promise<{ exePath: string; pluginPath: string } | undefined> {
  // Checked as a pair *within the same root*, not independently across roots - an
  // earlier version resolved exePath/pluginPath via two separate firstExisting() scans
  // over the same root list, which could report a "found" result pairing one root's
  // exe with a *different* root's plugin (e.g. an exe-only own-install alongside a
  // plugin-only IDCs-fork install) - a mismatched pair that was never actually
  // installed together. Iterating roots in priority order and requiring both files to
  // exist in the *same* root avoids that.
  for (const root of candidateRoots(api)) {
    const exePath = path.join(root, QUICKBMS_EXE_RELATIVE);
    const pluginPath = path.join(root, QUICKBMS_PLUGIN_RELATIVE);
    const [exeFound, pluginFound] = await Promise.all([fileExists(exePath), fileExists(pluginPath)]);
    if (exeFound && pluginFound) {
      return { exePath, pluginPath };
    }
  }
  return undefined;
}

/** Detects an already-installed wcc_lite - see `wccLiteAcquisition.ts` for the
 *  auto-download path that populates `getBundleToolsDir(api)`'s `wcc_lite/` subfolder
 *  when this returns `undefined`. */
export async function detectWccLite(api: types.IExtensionApi): Promise<string | undefined> {
  const bundleToolsDir = getBundleToolsDir(api);

  const canonicalPath = path.join(bundleToolsDir, WCC_LITE_EXE_RELATIVE);
  if (await fileExists(canonicalPath)) {
    return canonicalPath;
  }

  // The IDCs-fork fallback root always uses the exact, well-known layout (this repo's
  // own App.config defaults *are* that layout) - no search needed there, unlike the
  // freshly-downloaded-by-us case below.
  const idcsToolsDir = getIdcsForkToolsDir(api);
  if (idcsToolsDir) {
    const idcsPath = path.join(idcsToolsDir, WCC_LITE_EXE_RELATIVE);
    if (await fileExists(idcsPath)) {
      return idcsPath;
    }
  }

  // Fallback: a previously-downloaded wcc_lite Nexus archive (see wccLiteAcquisition.ts)
  // may not lay wcc_lite.exe out at the exact canonical relative path above - see this
  // module's own top comment.
  const wccLiteRoot = path.join(bundleToolsDir, WCC_LITE_SUBDIR);
  return findFileByNameBounded(wccLiteRoot, WCC_LITE_EXE_FILENAME, BUNDLE_TOOL_SEARCH_MAX_DEPTH);
}

/**
 * Combines both detections into the shape `wsmEnv.ts`'s `buildWsmEnv` expects -
 * `WsmEnvConfig`'s `quickBmsPath`/`quickBmsPluginPath`/`wccLitePath` fields. Local-only,
 * no network - safe to call unconditionally (mirrors `toolAcquisition.ts`'s
 * `ensureWsmToolRegistered`'s own "safe on every load" property).
 *
 * **Consumers**: this unit's own `wsmStatusSummary.ts` (feeding `statusTile.ts`'s
 * dashlet) is the only caller wired up so far - it's the only WSM-process spawn site
 * this unit is allowed to touch (`toolAcquisition.ts`'s own `registerAcquiredTool` is
 * explicitly off-limits per this unit's task instructions, and this unit doesn't build
 * the merge panel/resolve action). The sibling units that actually drive
 * bundle-content merges (units G/H/I) are the spawn sites that most need these three
 * env vars populated - they should call this function too, rather than re-deriving the
 * same two detection locations independently.
 */
export async function detectBundleTools(api: types.IExtensionApi): Promise<DetectedBundleTools> {
  const [quickBms, wccLitePath] = await Promise.all([detectQuickBms(api), detectWccLite(api)]);
  return {
    quickBmsPath: quickBms?.exePath,
    quickBmsPluginPath: quickBms?.pluginPath,
    wccLitePath,
  };
}

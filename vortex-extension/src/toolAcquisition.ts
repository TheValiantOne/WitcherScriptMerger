import * as fs from 'fs';
import * as path from 'path';
import { selectors, types, util } from 'vortex-api';
import { ArchiveExtractor, createVortexArchiveExtractor } from './archiveExtractor';
import { buildWsmDiscoveredTool, registerWsmDiscoveredTool } from './discoveredTool';
import { WITCHER3_GAME_ID } from './gating';
import { buildAssetFileName, DEFAULT_WSM_REPO, downloadReleaseAsset, HttpClient, resolveReleaseAsset } from './githubRelease';
import { getDownloadCacheDir, getWsmToolDir, INSTALLED_VERSION_FILENAME } from './storage';
import { buildWsmEnv } from './wsmEnv';

/**
 * Orchestrates the full acquisition pipeline (download from GitHub Releases -> verify ->
 * extract -> register as a discovered tool) and a lighter local-only re-registration
 * path used at every Vortex startup. See this unit's PR description for exactly what's
 * verified end-to-end (a locally-built WSM binary standing in for a downloaded one, per
 * `test/toolAcquisition.integration.test.ts`) versus what's real-but-unexercised code
 * (the actual GitHub download - no release exists on this repo yet).
 */

export const WSM_HEADLESS_EXE_NAME = 'WitcherScriptMerger.Headless.exe';

export interface AcquireWsmToolOptions {
  api: types.IExtensionApi;
  /** e.g. `"0.6.2"` - no leading "v" (matches `WitcherScriptMerger.Headless.csproj`'s
   *  own `<Version>`; the release tag itself, per `release.yml`, is this value with a
   *  "v" prefixed back on). */
  version: string;
  repo?: string;
  /** Test-only seam - see `githubRelease.ts`'s `HttpClient`. */
  client?: HttpClient;
  /** Test-only seam - see `archiveExtractor.ts`'s `ArchiveExtractor`. */
  extractor?: ArchiveExtractor;
}

export interface AcquiredWsmTool {
  exePath: string;
  version: string;
  installDir: string;
}

function isEnoent(err: unknown): boolean {
  return typeof err === 'object' && err !== null && (err as NodeJS.ErrnoException).code === 'ENOENT';
}

async function pathExists(target: string): Promise<boolean> {
  try {
    await fs.promises.access(target);
    return true;
  } catch (err) {
    if (isEnoent(err)) {
      return false;
    }
    // Anything other than "doesn't exist" (permission denied, locked by another process,
    // a transient antivirus scan, etc.) is a real problem the caller needs to see, not
    // something that should be silently treated as "nothing installed yet" - that would
    // both hide the actual error and risk kicking off a doomed, unnecessary re-download.
    throw err;
  }
}

/** `<repo>@<version>` - GitHub owner/repo names never contain "@", so this is an
 *  unambiguous, trivially-parseable single-line marker; no JSON needed for two fields. */
function formatInstalledMarker(repo: string, version: string): string {
  return `${repo}@${version}`;
}

async function readInstalledMarker(installDir: string): Promise<{ repo: string; version: string } | undefined> {
  let content: string;
  try {
    content = await fs.promises.readFile(path.join(installDir, INSTALLED_VERSION_FILENAME), 'utf8');
  } catch (err) {
    if (isEnoent(err)) {
      return undefined;
    }
    throw err;
  }

  const trimmed = content.trim();
  const separatorIndex = trimmed.lastIndexOf('@');
  if (separatorIndex <= 0) {
    // Malformed or from an older marker format - treat as "no confident match", which
    // safely falls through to a fresh acquire/overwrite rather than trusting a value we
    // can't actually parse.
    return undefined;
  }

  return { repo: trimmed.slice(0, separatorIndex), version: trimmed.slice(separatorIndex + 1) };
}

/**
 * Downloads (if not already present locally at the requested repo+version), verifies,
 * extracts, and installs the WSM Headless build, then registers it as a discovered
 * Vortex tool. Idempotent on the download/extract step: if `getWsmToolDir(api)` already
 * contains this exact repo+version (per `INSTALLED_VERSION_FILENAME`), no network
 * activity happens at all. Registration always happens regardless, even on that
 * idempotent path - the exe being present locally doesn't guarantee it's currently
 * registered (e.g. a fresh Vortex session after a restart whose discovered-tools
 * persistence didn't survive this tool's non-serializable `executable` field - see
 * `discoveredTool.ts`), and registering an already-registered tool is a harmless no-op
 * dispatch.
 *
 * **Concurrency**: calls sharing the same `api` (and therefore the same `installDir`)
 * that overlap in time coalesce onto whichever call started first - a second call
 * arriving while the first is still in flight gets the *first* call's result rather than
 * starting an independent download/extract into the same directory (which would race
 * both the download and the extraction). This is a deliberate simplification, not a
 * full per-argument dedup: if the second call actually requested a different
 * `version`/`repo` than the first, it silently receives the first call's result instead
 * of its own request. Acceptable for this unit's only real trigger shape (a user
 * re-clicking the same "Get/Update WitcherScriptMerger" action while a request is
 * already in flight) - a later unit adding that UI action should be aware of this if it
 * ever needs to let a user cancel/redirect an in-flight acquisition.
 */
export async function acquireWsmTool(options: AcquireWsmToolOptions): Promise<AcquiredWsmTool> {
  const installDir = getWsmToolDir(options.api);
  const existing = inFlightAcquisitions.get(installDir);
  if (existing) {
    return existing;
  }

  const promise = acquireWsmToolUncoordinated(options, installDir);
  inFlightAcquisitions.set(installDir, promise);
  try {
    return await promise;
  } finally {
    inFlightAcquisitions.delete(installDir);
  }
}

/** Keyed by installDir - see `acquireWsmTool`'s own doc comment for the coalescing
 *  behavior this backs. */
const inFlightAcquisitions = new Map<string, Promise<AcquiredWsmTool>>();

async function acquireWsmToolUncoordinated(options: AcquireWsmToolOptions, installDir: string): Promise<AcquiredWsmTool> {
  const { api, version } = options;
  const repo = options.repo ?? DEFAULT_WSM_REPO;
  const exePath = path.join(installDir, WSM_HEADLESS_EXE_NAME);

  const installedMarker = await readInstalledMarker(installDir);
  if ((await pathExists(exePath)) && installedMarker?.repo === repo && installedMarker?.version === version) {
    registerAcquiredTool(api, exePath);
    return { exePath, version, installDir };
  }

  const assetFileName = buildAssetFileName(version);
  const asset = await resolveReleaseAsset({ repo, tag: `v${version}`, assetFileName, client: options.client });

  const cacheDir = getDownloadCacheDir(api);
  await fs.promises.mkdir(cacheDir, { recursive: true });
  const zipPath = path.join(cacheDir, assetFileName);
  await downloadReleaseAsset({
    downloadUrl: asset.downloadUrl,
    destPath: zipPath,
    expectedSize: asset.size,
    client: options.client,
  });

  // Wipe whatever's currently in installDir before extracting: re-acquiring a different
  // version should actually overwrite (per storage.ts's own doc comment), not merge
  // files from two different releases together, and a prior failed/partial extraction's
  // debris shouldn't survive into this attempt either. `force: true` only suppresses the
  // "doesn't exist yet" case (a fresh install with no prior installDir at all) - if a WSM
  // process is actively running out of this directory, removal genuinely fails (Windows
  // won't let a running exe's backing file be deleted), which is the correct outcome
  // here: a clear error, not a silently corrupted running install.
  await fs.promises.rm(installDir, { recursive: true, force: true });
  await fs.promises.mkdir(installDir, { recursive: true });

  // extractAll's own implementations also ensure destDir exists (see archiveExtractor.ts's
  // ArchiveExtractor contract) - that's not redundant with the mkdir just above so much as
  // each call site owning its own precondition: this mkdir exists specifically to leave a
  // fresh, empty directory right after the rm above, independent of whatever a particular
  // extractor implementation does or doesn't assume about its destDir argument.
  const extractor = options.extractor ?? createVortexArchiveExtractor(api);
  await extractor.extractAll(zipPath, installDir);

  if (!(await pathExists(exePath))) {
    throw new Error(
      `Extracted '${zipPath}' into '${installDir}' but expected executable '${WSM_HEADLESS_EXE_NAME}' was not found there afterward. The release asset's internal layout may not match what this extension expects.`,
    );
  }

  await util.writeFileAtomic(path.join(installDir, INSTALLED_VERSION_FILENAME), formatInstalledMarker(repo, version));

  // The downloaded .zip has done its job once extraction succeeded - getDownloadCacheDir
  // is documented (storage.ts) as a disposable cache, so don't leave it accumulating one
  // full release archive per acquisition/upgrade forever. Best-effort: a failure to clean
  // up the cache is not worth failing an otherwise-successful acquisition over.
  try {
    await fs.promises.unlink(zipPath);
  } catch {
    // Ignored - see comment above.
  }

  registerAcquiredTool(api, exePath);

  return { exePath, version, installDir };
}

function registerAcquiredTool(api: types.IExtensionApi, exePath: string): void {
  // Specifically Witcher 3's own discovered install path, not selectors.currentGameDiscovery
  // (whichever game happens to be active *right now*) - this function always registers the
  // tool under WITCHER3_GAME_ID a few lines down, so the environment attached to it must be
  // scoped to that same game, not whatever's currently active (which can race a live
  // game-mode switch, since every caller of this function is async).
  const gameDirectory = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.path;
  const tool = buildWsmDiscoveredTool({ exePath, environment: buildWsmEnv({ gameDirectory }) });
  registerWsmDiscoveredTool(api, tool);
}

/**
 * Registration-only path, with **no network activity whatsoever** - safe to call
 * unconditionally on every extension load (see `index.ts`). If a WSM build was already
 * acquired in a previous session (via `acquireWsmTool` above, triggered by a later
 * unit's own UI action - this unit doesn't add one; see this unit's PR description for
 * why an eager background download at every startup would be actively wrong while no
 * GitHub Release exists yet), this re-registers it as a discovered tool; Vortex's own
 * discovered-tools persistence for an `IDiscoveredTool` carrying a function field
 * (`executable`) is unverified against a real Vortex host (see `discoveredTool.ts`), so
 * re-registering on every startup is the safe, idempotent default rather than assuming
 * a prior registration survived.
 *
 * Returns `false` (not an error) when nothing has been acquired yet - that's the
 * expected, normal state for as long as no GitHub Release exists.
 */
export async function ensureWsmToolRegistered(api: types.IExtensionApi): Promise<boolean> {
  const installDir = getWsmToolDir(api);
  const exePath = path.join(installDir, WSM_HEADLESS_EXE_NAME);

  if (!(await pathExists(exePath))) {
    return false;
  }

  registerAcquiredTool(api, exePath);
  return true;
}

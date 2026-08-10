import * as fs from 'fs';
import * as path from 'path';
import { types } from 'vortex-api';
import { ArchiveExtractor, createVortexArchiveExtractor } from './archiveExtractor';
import { BUNDLE_TOOL_SEARCH_MAX_DEPTH, WCC_LITE_EXE_FILENAME, WCC_LITE_SUBDIR, detectWccLite, findFileByNameBounded } from './bundleTools';
import { WITCHER3_GAME_ID } from './gating';
import { NexusDownloader, createVortexNexusDownloader } from './nexusDownloader';
import { getBundleToolsDir } from './storage';

/**
 * Auto-fetches wcc_lite - the Windows-only official CD Projekt Red "Witcher 3 modding
 * tools" / ModKit binary WSM needs for bundle-content (DLC/expansion) conflicts, see
 * the root CLAUDE.md's "External tool dependencies & licensing" - from its Nexus Mods
 * page, via Vortex's own Nexus-download mechanism (`nexusDownloader.ts`). Detects an
 * existing install first (`bundleTools.ts`'s `detectWccLite` - both this extension's
 * own prior download and a prior `IDCs/WitcherScriptMerger` fork install) and does no
 * network activity at all when one is already present, mirroring `toolAcquisition.ts`'s
 * `acquireWsmTool`'s own idempotent-download shape.
 *
 * **Licensing/EULA caveat - read before enabling this broadly.** wcc_lite is an
 * official CD Projekt Red tool distributed via Nexus Mods, not a WSM-authored artifact,
 * and this extension redistributing/auto-fetching it at runtime (into a
 * Vortex-managed location, never into this repo's own source control) has not been
 * independently confirmed against Nexus Mods' / CD Projekt Red's own redistribution
 * terms beyond "it's an official tool hosted on an official Nexus mod page". The root
 * CLAUDE.md already treats QuickBMS/wcc_lite licensing as an explicitly open decision
 * requiring the repo owner's sign-off before changing the "not bundled in source
 * control" policy - auto-fetching at runtime into a separate, Vortex-managed location
 * is a different question from bundling in source control, but still needs the repo
 * owner's explicit go/no-go before this ships broadly. See this unit's PR description.
 *
 * **Mod id caveat.** `WCC_LITE_NEXUS_MOD_ID` below (3173, "Official ModKit") was
 * corroborated via two independent web searches and cross-referenced against
 * `WitcherScriptMerger/Forms/DependencyForm.cs`'s own wcc_lite link (a Nexus *news*
 * post announcing a ModKit update, itself pointing at this same mod page) - not
 * verified against a live, authenticated Nexus session (this environment has no Nexus
 * API key, and nexusmods.com returns HTTP 403 for unauthenticated scraping). The exact
 * **file id** within that mod is deliberately *not* hardcoded - `resolveFileId` below
 * looks it up live via `api.ext.nexusGetModFiles`, picking the file Nexus itself marks
 * `is_primary`, so this doesn't go stale as the ModKit is updated over time.
 *
 * **Archive-layout caveat.** The "Official ModKit" download may not even be a plain
 * tools archive (it could be an installer, or a much larger archive than just
 * wcc_lite) - if `wcc_lite.exe` isn't found anywhere inside it after extraction, this
 * throws a clear error naming what it looked for and where, the same "degrades to a
 * clear failure, not a silent wrong result" shape as `acquireWsmTool`'s own "expected
 * executable was not found" check.
 */

export const WCC_LITE_NEXUS_GAME_ID = WITCHER3_GAME_ID;
/** "Official ModKit" on the Witcher 3 Nexus, published by CD Projekt RED - see this
 *  module's own "Mod id caveat" above. */
export const WCC_LITE_NEXUS_MOD_ID = 3173;
/** For display/manual-fallback purposes (e.g. a "get it yourself" link in the status
 *  tile if auto-fetch fails) - the mod page this extension downloads from. */
export const WCC_LITE_NEXUS_MOD_URL = `https://www.nexusmods.com/witcher3/mods/${WCC_LITE_NEXUS_MOD_ID}`;

export interface AcquireWccLiteOptions {
  api: types.IExtensionApi;
  /** Test-only seam / manual override - skips `nexusGetModFiles` resolution entirely
   *  when set. */
  fileId?: number;
  /** Test-only seam - see `nexusDownloader.ts`'s `NexusDownloader`. */
  downloader?: NexusDownloader;
  /** Test-only seam - see `archiveExtractor.ts`'s `ArchiveExtractor`. */
  extractor?: ArchiveExtractor;
}

/** Minimal local shape for the fields this module actually reads off a Nexus file
 *  listing - deliberately not importing `IFileInfo` from `@nexusmods/nexus-api` (a
 *  transitive, types-only dependency of `@nexusmods/vortex-api` that isn't itself
 *  installed in this project - see `package.json`), which would tie this file to a
 *  package this project has no direct dependency on for zero benefit over this narrow
 *  local interface. */
interface NexusFileInfoLike {
  file_id: number;
  file_name?: string;
  is_primary?: boolean;
}

async function resolveFileId(
  api: types.IExtensionApi,
  explicitFileId: number | undefined,
): Promise<{ fileId: number; fileName?: string }> {
  if (explicitFileId !== undefined) {
    return { fileId: explicitFileId };
  }

  const nexusGetModFiles = api.ext?.nexusGetModFiles;
  if (typeof nexusGetModFiles !== 'function') {
    throw new Error(
      'Cannot resolve which wcc_lite file to download: api.ext.nexusGetModFiles is unavailable and no ' +
        'explicit fileId was supplied.',
    );
  }

  const files = (await nexusGetModFiles(
    WCC_LITE_NEXUS_GAME_ID,
    WCC_LITE_NEXUS_MOD_ID,
  )) as unknown as NexusFileInfoLike[];
  const primary = files.find((f) => f.is_primary) ?? files[0];
  if (!primary) {
    throw new Error(`Nexus reported no files at all for mod ${WCC_LITE_NEXUS_MOD_ID} ('Official ModKit').`);
  }
  return { fileId: primary.file_id, fileName: primary.file_name };
}

/** Keyed by extractDir - mirrors `toolAcquisition.ts`'s own `inFlightAcquisitions`:
 *  overlapping calls sharing the same target directory coalesce onto whichever call
 *  started first, rather than racing two concurrent downloads/extractions into the
 *  same directory (e.g. two dashlet instances both offering a "Get wcc_lite" button).
 *  Same deliberate simplification `toolAcquisition.ts` documents - not a full
 *  per-argument dedup. */
const inFlightAcquisitions = new Map<string, Promise<string>>();

/**
 * Downloads (if not already present locally - see `bundleTools.ts`'s `detectWccLite`),
 * extracts, and returns the absolute path to `wcc_lite.exe`.
 */
export async function acquireWccLite(options: AcquireWccLiteOptions): Promise<string> {
  const extractDir = path.join(getBundleToolsDir(options.api), WCC_LITE_SUBDIR);

  const existing = inFlightAcquisitions.get(extractDir);
  if (existing) {
    return existing;
  }

  const promise = acquireWccLiteUncoordinated(options, extractDir);
  inFlightAcquisitions.set(extractDir, promise);
  try {
    return await promise;
  } finally {
    inFlightAcquisitions.delete(extractDir);
  }
}

async function acquireWccLiteUncoordinated(options: AcquireWccLiteOptions, extractDir: string): Promise<string> {
  const { api } = options;

  const existing = await detectWccLite(api);
  if (existing) {
    return existing;
  }

  const { fileId, fileName } = await resolveFileId(api, options.fileId);
  const downloader = options.downloader ?? createVortexNexusDownloader(api);

  const archivePath = await downloader.downloadModFile({
    gameId: WCC_LITE_NEXUS_GAME_ID,
    modId: WCC_LITE_NEXUS_MOD_ID,
    fileId,
    fileName,
  });

  // Wipe whatever's currently in extractDir before extracting - a prior
  // interrupted/failed extraction's debris shouldn't survive into this attempt and mix
  // with a fresh one (mirrors toolAcquisition.ts's acquireWsmToolUncoordinated's own
  // rm-then-mkdir before extracting, for the identical reason).
  await fs.promises.rm(extractDir, { recursive: true, force: true });
  await fs.promises.mkdir(extractDir, { recursive: true });

  const extractor = options.extractor ?? createVortexArchiveExtractor(api);
  await extractor.extractAll(archivePath, extractDir);

  const exePath = await findFileByNameBounded(extractDir, WCC_LITE_EXE_FILENAME, BUNDLE_TOOL_SEARCH_MAX_DEPTH);
  if (!exePath) {
    throw new Error(
      `Downloaded and extracted the wcc_lite Nexus mod file into '${extractDir}' but no ` +
        `'${WCC_LITE_EXE_FILENAME}' was found there (searched up to ${BUNDLE_TOOL_SEARCH_MAX_DEPTH} directory ` +
        "levels deep). The mod's internal layout may not match what this extension expects - see this unit's " +
        'PR description for the "archive-layout caveat".',
    );
  }

  // Deliberately does *not* delete the downloaded archive afterward (unlike
  // toolAcquisition.ts's own best-effort cleanup of its scratch download-cache zip):
  // that archive lives in *Vortex's own* downloads store (see nexusDownloader.ts),
  // which Vortex itself tracks as a real, user-visible download entry - deleting the
  // backing file out from under Vortex's own bookkeeping would leave a dangling
  // "missing file" entry in Vortex's Downloads page, unlike the disposable,
  // this-extension-only scratch cache toolAcquisition.ts cleans up.
  return exePath;
}

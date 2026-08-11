import * as path from 'path';
import { types } from 'vortex-api';

/**
 * Extension-private storage layout, all rooted under Vortex's own `userData` directory
 * (`api.getPath('userData')` - the same "always use the appropriate folder location"
 * mechanism `@nexusmods/vortex-api`'s own `IExtensionApi.getPath` doc comment
 * recommends, rather than this extension inventing its own path). Every helper here
 * takes `api` rather than reading a module-level constant so it's trivially testable
 * with a fake `{ getPath: () => tmpDir }` object, the same pattern `gating.ts` already
 * uses for `isWitcher3Active(api)`.
 *
 * Layout:
 *
 *   <userData>/witcherscriptmerger-vortex/
 *     tool/                 <- the acquired WSM Headless build (see toolAcquisition.ts).
 *                              Flat, single "current" install, not versioned side-by-side
 *                              installs - re-acquiring a different version overwrites it.
 *                              `installed-version.txt` (INSTALLED_VERSION_FILENAME)
 *                              records which version is currently unpacked here.
 *     downloads/            <- scratch .zip downloads before extraction; safe to delete
 *                              entirely at any time (toolAcquisition.ts treats it as a
 *                              cache, not a source of truth).
 *     bundle-tools/         <- QuickBMS (quickbms.exe + witcher3.bms) and wcc_lite
 *                              (see bundleTools.ts's detection logic and
 *                              wccLiteAcquisition.ts's wcc_lite auto-download) land
 *                              under here, e.g.
 *                              path.join(getBundleToolsDir(api), 'QuickBMS', 'quickbms.exe').
 *                              WSM_QuickBmsPath/WSM_QuickBmsPluginPath/WSM_WccLitePath
 *                              (see wsmEnv.ts) are populated from these paths by
 *                              wsmStatusSummary.ts today; the sibling units driving
 *                              actual bundle-content merges should reuse
 *                              bundleTools.ts's detectBundleTools(api) too, rather than
 *                              re-deriving this layout independently.
 */
const EXTENSION_STORAGE_DIRNAME = 'witcherscriptmerger-vortex';
const TOOL_SUBDIR = 'tool';
const DOWNLOAD_CACHE_SUBDIR = 'downloads';
const BUNDLE_TOOLS_SUBDIR = 'bundle-tools';

/** Records which WSM version is currently unpacked in getWsmToolDir(api) - see that
 *  function's doc comment. Plain text, just the version string (e.g. "0.6.2"), no
 *  surrounding JSON/XML - deliberately trivial to read/write without a parser. */
export const INSTALLED_VERSION_FILENAME = 'installed-version.txt';

/** The Headless host's executable name inside any WSM install this extension uses -
 *  lives here (rather than toolAcquisition.ts, its original home) so wsmToolPath.ts
 *  can import it without a toolAcquisition <-> wsmToolPath import cycle. */
export const WSM_HEADLESS_EXE_NAME = 'WitcherScriptMerger.Headless.exe';

export function getExtensionStorageDir(api: types.IExtensionApi): string {
  return path.join(api.getPath('userData'), EXTENSION_STORAGE_DIRNAME);
}

/** Where the acquired WSM Headless build's files (exe, its .dll.config, etc.) live. */
export function getWsmToolDir(api: types.IExtensionApi): string {
  return path.join(getExtensionStorageDir(api), TOOL_SUBDIR);
}

/** Scratch directory for in-progress .zip downloads - a cache, not persistent state. */
export function getDownloadCacheDir(api: types.IExtensionApi): string {
  return path.join(getExtensionStorageDir(api), DOWNLOAD_CACHE_SUBDIR);
}

/** See this module's own doc comment above - QuickBMS/wcc_lite land under here (used by
 *  `bundleTools.ts`'s detection and `wccLiteAcquisition.ts`'s auto-download). */
export function getBundleToolsDir(api: types.IExtensionApi): string {
  return path.join(getExtensionStorageDir(api), BUNDLE_TOOLS_SUBDIR);
}

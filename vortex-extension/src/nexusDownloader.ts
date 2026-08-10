import * as path from 'path';
import { selectors, types } from 'vortex-api';

/**
 * Downloads a file from a Nexus Mods mod page through Vortex's own Nexus integration -
 * the pattern a Vortex extension normally uses to fetch a Nexus-hosted dependency by
 * mod id, per this unit's own task instructions. `api.ext.nexusDownload` (a real,
 * optional cross-extension API surface - `INexusAPIExtension` in
 * `@nexusmods/vortex-api`'s own `lib/api.d.ts`) is provided by Vortex's own built-in
 * Nexus integration extension at runtime; there is no single "download from Nexus and
 * give me a local file path" call in that surface, so this module composes one out of
 * the primitives that do exist: `nexusDownload` itself (queues/starts the download,
 * resolving to a download id - `lib/api.d.ts` documents no guarantee about whether that
 * promise resolves before or after the download actually finishes, so this doesn't
 * assume either), then polling `state.persistent.downloads.files[id]` (an `IDownload`)
 * until that download actually finishes, then `selectors.downloadPathForGame` to
 * resolve the download's `localPath` (relative, per `IDownload.localPath`'s own doc
 * comment) to an absolute path.
 *
 * Injectable (mirrors `githubRelease.ts`'s `HttpClient`/`archiveExtractor.ts`'s
 * `ArchiveExtractor` seams) so `wccLiteAcquisition.ts` stays unit-testable without a
 * real Vortex host or a real Nexus download - **this deliberately deviates from this
 * unit's own instruction to mock at `githubRelease.ts`'s `nodeHttpsClient` boundary**:
 * wcc_lite is fetched through Vortex's Nexus-download mechanism, not a plain HTTPS GET
 * (Nexus doesn't serve unauthenticated direct-download links, so there is no URL for a
 * raw `HttpClient` to hit even in principle) - see this unit's PR description for why.
 * `createVortexNexusDownloader`'s real implementation below is, like
 * `archiveExtractor.ts`'s real `createVortexArchiveExtractor`, never exercised by any
 * test in this repo - there is no real Vortex host to run it against. See this unit's
 * PR description for exactly what was/wasn't verified.
 */
export interface NexusDownloadOptions {
  gameId: string;
  modId: number;
  fileId: number;
  fileName?: string;
}

export interface NexusDownloader {
  /** Resolves to the absolute local path of the fully-downloaded file. Rejects if the
   *  download fails, or doesn't finish before this call's own timeout. */
  downloadModFile(options: NexusDownloadOptions): Promise<string>;
}

const DEFAULT_POLL_INTERVAL_MS = 1000;
/** A full "Witcher 3 modding tools" ModKit archive can be large (it's CD Projekt Red's
 *  whole toolkit, not just wcc_lite - see `wccLiteAcquisition.ts`), so this is generous
 *  compared to `githubRelease.ts`'s own per-request 30s timeout. */
const DEFAULT_DOWNLOAD_TIMEOUT_MS = 10 * 60 * 1000;

export interface VortexNexusDownloaderOptions {
  pollIntervalMs?: number;
  downloadTimeoutMs?: number;
}

export function createVortexNexusDownloader(
  api: types.IExtensionApi,
  options: VortexNexusDownloaderOptions = {},
): NexusDownloader {
  const pollIntervalMs = options.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS;
  const downloadTimeoutMs = options.downloadTimeoutMs ?? DEFAULT_DOWNLOAD_TIMEOUT_MS;

  return {
    async downloadModFile({ gameId, modId, fileId, fileName }: NexusDownloadOptions): Promise<string> {
      const nexusDownload = api.ext?.nexusDownload;
      if (typeof nexusDownload !== 'function') {
        throw new Error(
          "Cannot download from Nexus Mods: api.ext.nexusDownload is unavailable. Vortex's own Nexus " +
            'integration extension (which provides this) may not be loaded.',
        );
      }

      // allowInstall: false - load-bearing, not a default left at its default value.
      // This download is a build dependency for WSM's own bundle-content merging, not a
      // Witcher 3 mod in its own right; letting Vortex auto-install it would deploy the
      // whole ModKit archive into the game's Mods folder and load-order it like a mod,
      // which is not what this needs.
      const downloadId = await nexusDownload(gameId, modId, fileId, fileName, false);

      const download = await waitForDownloadToFinish(api, downloadId, pollIntervalMs, downloadTimeoutMs);
      if (!download.localPath) {
        throw new Error(
          `Nexus download '${downloadId}' (mod ${modId}, file ${fileId}) finished but reported no localPath.`,
        );
      }

      const downloadsDir = selectors.downloadPathForGame(api.getState(), gameId);
      return path.join(downloadsDir, download.localPath);
    },
  };
}

interface DownloadStateLike {
  state?: string;
  localPath?: string;
}

async function waitForDownloadToFinish(
  api: types.IExtensionApi,
  downloadId: string,
  pollIntervalMs: number,
  timeoutMs: number,
): Promise<DownloadStateLike> {
  const deadline = Date.now() + timeoutMs;

  for (;;) {
    const state = api.getState() as {
      persistent?: { downloads?: { files?: Record<string, DownloadStateLike> } };
    };
    const download = state.persistent?.downloads?.files?.[downloadId];

    if (download?.state === 'finished') {
      return download;
    }
    if (download?.state === 'failed') {
      throw new Error(`Nexus download '${downloadId}' failed.`);
    }
    // Any other observed state (undefined/not-yet-registered, 'init', 'started',
    // 'paused', 'finalizing', 'redirect' - the full `DownloadState` union per
    // `lib/api.d.ts` minus the two handled above) just means "still in progress" -
    // keep polling rather than treating it as success or failure.

    if (Date.now() >= deadline) {
      throw new Error(`Nexus download '${downloadId}' did not finish within ${timeoutMs}ms.`);
    }

    await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
  }
}

import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createVortexNexusDownloader } from './nexusDownloader';

interface FakeDownloadEntry {
  state?: string;
  localPath?: string;
}

function fakeApi(options: {
  nexusDownload?: (...args: unknown[]) => PromiseLike<string>;
  downloadsDir?: string;
  downloads?: Record<string, FakeDownloadEntry>;
}) {
  const downloads = options.downloads ?? {};
  return {
    ext: options.nexusDownload ? { nexusDownload: options.nexusDownload } : {},
    // Wires `options.downloadsDir` into the vitest stub's own fake
    // `selectors.downloadPathForGame` state shape (see
    // test/testUtils/vortexApiStub.ts) - keyed by 'witcher3' since every test in this
    // file passes that as `gameId`. Without this, downloadModFile's own
    // `path.join(downloadsDir, localPath)` step would silently join onto the stub's
    // fallback sentinel directory instead, and no assertion here would actually cover
    // that this module joins the two together correctly.
    getState: () => ({
      persistent: { downloads: { files: downloads } },
      downloadPathForGame: options.downloadsDir ? { witcher3: options.downloadsDir } : undefined,
    }),
  } as unknown as Parameters<typeof createVortexNexusDownloader>[0];
}

describe('createVortexNexusDownloader', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('throws a clear error when api.ext.nexusDownload is unavailable', async () => {
    const api = fakeApi({});
    const downloader = createVortexNexusDownloader(api);

    await expect(
      downloader.downloadModFile({ gameId: 'witcher3', modId: 3173, fileId: 1 }),
    ).rejects.toThrow(/nexusDownload is unavailable/);
  });

  it('calls nexusDownload with allowInstall: false and resolves to downloadsDir joined with localPath', async () => {
    const downloads: Record<string, FakeDownloadEntry> = {
      'download-1': { state: 'started' },
    };
    const nexusDownload = vi.fn(async () => 'download-1');
    const downloadsDir = path.join('C:', 'fake-vortex-downloads', 'witcher3');
    const api = fakeApi({ nexusDownload, downloads, downloadsDir });

    // Simulate the download finishing shortly after being queued.
    setTimeout(() => {
      downloads['download-1'] = { state: 'finished', localPath: 'wcc_lite_modkit.zip' };
    }, 2500);

    const downloader = createVortexNexusDownloader(api, { pollIntervalMs: 1000 });
    const resultPromise = downloader.downloadModFile({
      gameId: 'witcher3',
      modId: 3173,
      fileId: 42,
      fileName: 'wcc_lite_modkit.zip',
    });

    await vi.advanceTimersByTimeAsync(5000);
    const result = await resultPromise;

    expect(nexusDownload).toHaveBeenCalledWith('witcher3', 3173, 42, 'wcc_lite_modkit.zip', false);
    // The real assertion this test exists for: downloadsDir (from
    // selectors.downloadPathForGame) actually gets joined with the download's own
    // localPath, not just "the result happens to end with the right filename".
    expect(result).toBe(path.join(downloadsDir, 'wcc_lite_modkit.zip'));
  });

  it('rejects when the download reports a failed state', async () => {
    const downloads: Record<string, FakeDownloadEntry> = {
      'download-1': { state: 'failed' },
    };
    const nexusDownload = vi.fn(async () => 'download-1');
    const api = fakeApi({ nexusDownload, downloads });

    const downloader = createVortexNexusDownloader(api, { pollIntervalMs: 1000 });
    await expect(
      downloader.downloadModFile({ gameId: 'witcher3', modId: 3173, fileId: 42 }),
    ).rejects.toThrow(/failed/);
  });

  it('rejects once the timeout elapses without the download finishing', async () => {
    const downloads: Record<string, FakeDownloadEntry> = {
      'download-1': { state: 'started' },
    };
    const nexusDownload = vi.fn(async () => 'download-1');
    const api = fakeApi({ nexusDownload, downloads });

    const downloader = createVortexNexusDownloader(api, { pollIntervalMs: 1000, downloadTimeoutMs: 3000 });
    const resultPromise = downloader.downloadModFile({ gameId: 'witcher3', modId: 3173, fileId: 42 });
    const assertion = expect(resultPromise).rejects.toThrow(/did not finish within/);

    await vi.advanceTimersByTimeAsync(5000);
    await assertion;
  });

  it('treats an entry not yet present in downloads state as still in progress, not a failure', async () => {
    const downloads: Record<string, FakeDownloadEntry> = {};
    const nexusDownload = vi.fn(async () => 'download-1');
    const api = fakeApi({ nexusDownload, downloads });

    setTimeout(() => {
      downloads['download-1'] = { state: 'finished', localPath: 'file.zip' };
    }, 2000);

    const downloader = createVortexNexusDownloader(api, { pollIntervalMs: 500 });
    const resultPromise = downloader.downloadModFile({ gameId: 'witcher3', modId: 3173, fileId: 1 });

    await vi.advanceTimersByTimeAsync(3000);
    await expect(resultPromise).resolves.toContain('file.zip');
  });
});

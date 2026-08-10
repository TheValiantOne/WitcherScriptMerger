import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ArchiveExtractor } from './archiveExtractor';
import { NexusDownloader } from './nexusDownloader';
import { WCC_LITE_NEXUS_MOD_ID, acquireWccLite } from './wccLiteAcquisition';

function fakeApi(userDataDir: string, nexusGetModFiles?: (...args: unknown[]) => PromiseLike<unknown>) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({}),
    ext: nexusGetModFiles ? { nexusGetModFiles } : {},
  } as unknown as Parameters<typeof acquireWccLite>[0]['api'];
}

function fakeDownloaderReturning(archivePath: string): NexusDownloader {
  return {
    downloadModFile: vi.fn(async () => archivePath),
  };
}

function fakeExtractorThatProducesExe(exeRelativePath: string[]): ArchiveExtractor {
  return {
    extractAll: async (_archivePath: string, destDir: string) => {
      const exeDir = path.join(destDir, ...exeRelativePath.slice(0, -1));
      await fs.promises.mkdir(exeDir, { recursive: true });
      await fs.promises.writeFile(path.join(destDir, ...exeRelativePath), 'fake wcc_lite bytes', 'utf8');
    },
  };
}

describe('acquireWccLite', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-wcclite-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('detects an already-installed wcc_lite and does no network/extraction work at all', async () => {
    const wccLiteDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'wcc_lite', 'bin', 'x64');
    fs.mkdirSync(wccLiteDir, { recursive: true });
    const exePath = path.join(wccLiteDir, 'wcc_lite.exe');
    fs.writeFileSync(exePath, 'existing exe', 'utf8');

    const api = fakeApi(userDataDir);
    const downloader = fakeDownloaderReturning('/should-not-be-used.zip');
    const extractor = fakeExtractorThatProducesExe(['bin', 'x64', 'wcc_lite.exe']);
    const downloadSpy = vi.spyOn(downloader, 'downloadModFile');
    const extractSpy = vi.spyOn(extractor, 'extractAll');

    const result = await acquireWccLite({ api, downloader, extractor, fileId: 99 });

    expect(result).toBe(exePath);
    expect(downloadSpy).not.toHaveBeenCalled();
    expect(extractSpy).not.toHaveBeenCalled();
  });

  it('downloads and extracts wcc_lite when not already present, using an explicit fileId', async () => {
    const api = fakeApi(userDataDir);
    const downloader = fakeDownloaderReturning(path.join(userDataDir, 'downloaded-modkit.zip'));
    const downloadSpy = vi.spyOn(downloader, 'downloadModFile');
    const extractor = fakeExtractorThatProducesExe(['Modkit', 'bin', 'x64', 'wcc_lite.exe']);

    const result = await acquireWccLite({ api, downloader, extractor, fileId: 12345 });

    expect(downloadSpy).toHaveBeenCalledWith(
      expect.objectContaining({ modId: WCC_LITE_NEXUS_MOD_ID, fileId: 12345 }),
    );
    expect(fs.existsSync(result)).toBe(true);
    expect(path.basename(result).toLowerCase()).toBe('wcc_lite.exe');
  });

  it('resolves the fileId via nexusGetModFiles, preferring the file Nexus marks is_primary', async () => {
    const api = fakeApi(userDataDir, async () => [
      { file_id: 111, file_name: 'old.zip', is_primary: false },
      { file_id: 222, file_name: 'modkit-current.zip', is_primary: true },
    ]);
    const downloader = fakeDownloaderReturning(path.join(userDataDir, 'modkit-current.zip'));
    const downloadSpy = vi.spyOn(downloader, 'downloadModFile');
    const extractor = fakeExtractorThatProducesExe(['bin', 'x64', 'wcc_lite.exe']);

    await acquireWccLite({ api, downloader, extractor });

    expect(downloadSpy).toHaveBeenCalledWith(
      expect.objectContaining({ fileId: 222, fileName: 'modkit-current.zip' }),
    );
  });

  it('falls back to the first listed file when none is marked is_primary', async () => {
    const api = fakeApi(userDataDir, async () => [
      { file_id: 333, file_name: 'only.zip', is_primary: false },
    ]);
    const downloader = fakeDownloaderReturning(path.join(userDataDir, 'only.zip'));
    const downloadSpy = vi.spyOn(downloader, 'downloadModFile');
    const extractor = fakeExtractorThatProducesExe(['bin', 'x64', 'wcc_lite.exe']);

    await acquireWccLite({ api, downloader, extractor });

    expect(downloadSpy).toHaveBeenCalledWith(expect.objectContaining({ fileId: 333 }));
  });

  it('throws when nexusGetModFiles is unavailable and no explicit fileId was supplied', async () => {
    const api = fakeApi(userDataDir);
    const downloader = fakeDownloaderReturning('/unused.zip');
    const extractor = fakeExtractorThatProducesExe(['bin', 'x64', 'wcc_lite.exe']);

    await expect(acquireWccLite({ api, downloader, extractor })).rejects.toThrow(/nexusGetModFiles is unavailable/);
  });

  it('throws a clear error when extraction succeeds but no wcc_lite.exe is found anywhere inside it', async () => {
    const api = fakeApi(userDataDir);
    const downloader = fakeDownloaderReturning(path.join(userDataDir, 'modkit.zip'));
    const noOpExtractor: ArchiveExtractor = {
      extractAll: async (_archivePath, destDir) => {
        await fs.promises.mkdir(destDir, { recursive: true });
        await fs.promises.writeFile(path.join(destDir, 'readme.txt'), 'no exe here', 'utf8');
      },
    };

    await expect(acquireWccLite({ api, downloader, extractor: noOpExtractor, fileId: 1 })).rejects.toThrow(
      /no 'wcc_lite\.exe' was found/,
    );
  });
});

import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ArchiveExtractor } from './archiveExtractor';
import { WSM_TOOL_ID } from './discoveredTool';
import { DEFAULT_WSM_REPO, HttpClient } from './githubRelease';
import { getDownloadCacheDir, INSTALLED_VERSION_FILENAME } from './storage';
import { acquireWsmTool, AcquiredWsmTool, ensureWsmToolRegistered, WSM_HEADLESS_EXE_NAME } from './toolAcquisition';

const RELEASE_JSON = {
  tag_name: 'v0.6.2',
  assets: [
    {
      name: 'WitcherScriptMerger.Headless-0.6.2-win-x64.zip',
      browser_download_url: 'https://example.invalid/headless-win.zip',
      size: 999,
    },
  ],
};

function fakeApi(userDataDir: string, dispatch: (action: unknown) => void = vi.fn()) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({}),
    store: { dispatch },
  } as unknown as Parameters<typeof acquireWsmTool>[0]['api'];
}

/** A fake extractor that simulates "successful extraction" by writing the exe (and
 *  nothing else) directly into destDir - good enough to prove acquireWsmTool's own
 *  orchestration without a real archive. */
function fakeExtractorThatProducesExe(): ArchiveExtractor {
  return {
    extractAll: async (_archivePath: string, destDir: string) => {
      await fs.promises.mkdir(destDir, { recursive: true });
      await fs.promises.writeFile(path.join(destDir, WSM_HEADLESS_EXE_NAME), 'fake exe bytes', 'utf8');
    },
  };
}

function fakeClientForRelease(): HttpClient {
  return {
    getJson: async () => RELEASE_JSON,
    downloadToFile: async (_url: string, destPath: string) => {
      fs.writeFileSync(destPath, Buffer.alloc(RELEASE_JSON.assets[0].size, 1));
      return { bytesWritten: RELEASE_JSON.assets[0].size };
    },
  };
}

describe('acquireWsmTool', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-acquire-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('downloads, verifies, extracts, records the installed repo+version, and registers the tool on a fresh install', async () => {
    const dispatch = vi.fn();
    const api = fakeApi(userDataDir, dispatch);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();

    const getJsonSpy = vi.spyOn(client, 'getJson');
    const downloadSpy = vi.spyOn(client, 'downloadToFile');
    const extractSpy = vi.spyOn(extractor, 'extractAll');

    const result = await acquireWsmTool({ api, version: '0.6.2', client, extractor });

    expect(getJsonSpy).toHaveBeenCalledTimes(1);
    expect(downloadSpy).toHaveBeenCalledTimes(1);
    expect(extractSpy).toHaveBeenCalledTimes(1);
    expect(fs.existsSync(result.exePath)).toBe(true);
    expect(fs.readFileSync(path.join(result.installDir, INSTALLED_VERSION_FILENAME), 'utf8')).toBe(
      `${DEFAULT_WSM_REPO}@0.6.2`,
    );

    expect(dispatch).toHaveBeenCalledTimes(1);
    const action = dispatch.mock.calls[0][0] as { payload: { toolId: string; result: { path: string } } };
    expect(action.payload.toolId).toBe(WSM_TOOL_ID);
    expect(action.payload.result.path).toBe(result.exePath);
  });

  it('deletes the downloaded zip from the download cache after a successful extraction', async () => {
    const api = fakeApi(userDataDir);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();

    await acquireWsmTool({ api, version: '0.6.2', client, extractor });

    const cacheDir = getDownloadCacheDir(api);
    expect(fs.readdirSync(cacheDir)).toEqual([]);
  });

  it('is idempotent: does no network/extraction work when the requested repo+version is already installed, but still registers', async () => {
    const dispatch = vi.fn();
    const api = fakeApi(userDataDir, dispatch);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();
    const getJsonSpy = vi.spyOn(client, 'getJson');
    const extractSpy = vi.spyOn(extractor, 'extractAll');

    const first = await acquireWsmTool({ api, version: '0.6.2', client, extractor });
    dispatch.mockClear();
    getJsonSpy.mockClear();
    extractSpy.mockClear();

    const second: AcquiredWsmTool = await acquireWsmTool({ api, version: '0.6.2', client, extractor });

    expect(second).toEqual(first);
    expect(getJsonSpy).not.toHaveBeenCalled();
    expect(extractSpy).not.toHaveBeenCalled();
    // Still (re-)registers even though nothing was downloaded - see acquireWsmTool's own
    // doc comment for why that's not wasted work.
    expect(dispatch).toHaveBeenCalledTimes(1);
  });

  it('re-acquires when a different version is requested than what is currently installed, wiping stale files from the old install', async () => {
    const api = fakeApi(userDataDir);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();

    const first = await acquireWsmTool({ api, version: '0.6.2', client, extractor });
    // A file that belonged only to the old (v0.6.2) install - e.g. a stray .pdb the new
    // release's zip doesn't contain. If re-acquiring only extracts on top without first
    // clearing installDir, this would survive indefinitely.
    const staleFile = path.join(first.installDir, 'only-in-old-version.pdb');
    fs.writeFileSync(staleFile, 'stale', 'utf8');

    const newerRelease = {
      tag_name: 'v0.7.0',
      assets: [{ name: 'WitcherScriptMerger.Headless-0.7.0-win-x64.zip', browser_download_url: 'https://example.invalid/newer.zip', size: 50 }],
    };
    const newerClient: HttpClient = {
      getJson: async () => newerRelease,
      downloadToFile: async (_url, destPath) => {
        fs.writeFileSync(destPath, Buffer.alloc(50, 2));
        return { bytesWritten: 50 };
      },
    };

    const result = await acquireWsmTool({ api, version: '0.7.0', client: newerClient, extractor });

    expect(fs.readFileSync(path.join(result.installDir, INSTALLED_VERSION_FILENAME), 'utf8')).toBe(
      `${DEFAULT_WSM_REPO}@0.7.0`,
    );
    expect(fs.existsSync(staleFile)).toBe(false);
  });

  it('re-acquires (rather than silently reusing the old install) when the same version is requested from a different repo', async () => {
    const api = fakeApi(userDataDir);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();
    const extractSpy = vi.spyOn(extractor, 'extractAll');

    await acquireWsmTool({ api, version: '0.6.2', repo: 'SomeFork/WitcherScriptMerger', client, extractor });
    extractSpy.mockClear();

    const result = await acquireWsmTool({ api, version: '0.6.2', repo: DEFAULT_WSM_REPO, client, extractor });

    expect(extractSpy).toHaveBeenCalledTimes(1);
    expect(fs.readFileSync(path.join(result.installDir, INSTALLED_VERSION_FILENAME), 'utf8')).toBe(
      `${DEFAULT_WSM_REPO}@0.6.2`,
    );
  });

  it('coalesces concurrent calls targeting the same install onto a single download/extract', async () => {
    const dispatch = vi.fn();
    const api = fakeApi(userDataDir, dispatch);
    const client = fakeClientForRelease();
    const extractor = fakeExtractorThatProducesExe();
    const getJsonSpy = vi.spyOn(client, 'getJson');
    const extractSpy = vi.spyOn(extractor, 'extractAll');

    const [first, second] = await Promise.all([
      acquireWsmTool({ api, version: '0.6.2', client, extractor }),
      acquireWsmTool({ api, version: '0.6.2', client, extractor }),
    ]);

    expect(second).toEqual(first);
    expect(getJsonSpy).toHaveBeenCalledTimes(1);
    expect(extractSpy).toHaveBeenCalledTimes(1);
  });

  it('throws a clear error when extraction reports success but the expected exe is missing afterward', async () => {
    const api = fakeApi(userDataDir);
    const client = fakeClientForRelease();
    const noOpExtractor: ArchiveExtractor = { extractAll: async () => undefined };

    await expect(acquireWsmTool({ api, version: '0.6.2', client, extractor: noOpExtractor })).rejects.toThrow(
      /expected executable .* was not found/,
    );
  });
});

describe('ensureWsmToolRegistered', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-ensure-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('returns false and dispatches nothing when no tool has been acquired yet', async () => {
    const dispatch = vi.fn();
    const api = fakeApi(userDataDir, dispatch);

    await expect(ensureWsmToolRegistered(api)).resolves.toBe(false);
    expect(dispatch).not.toHaveBeenCalled();
  });

  it('returns true and registers the tool when one was already acquired locally', async () => {
    const dispatch = vi.fn();
    const api = fakeApi(userDataDir, dispatch);

    // Simulate a prior acquireWsmTool run's on-disk result directly, without any
    // network/extraction machinery - this function must do none of that.
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    await fs.promises.mkdir(toolDir, { recursive: true });
    const exePath = path.join(toolDir, WSM_HEADLESS_EXE_NAME);
    await fs.promises.writeFile(exePath, 'fake exe bytes', 'utf8');

    await expect(ensureWsmToolRegistered(api)).resolves.toBe(true);
    expect(dispatch).toHaveBeenCalledTimes(1);
    const action = dispatch.mock.calls[0][0] as { payload: { toolId: string; result: { path: string } } };
    expect(action.payload.toolId).toBe(WSM_TOOL_ID);
    expect(action.payload.result.path).toBe(exePath);
  });

  it('propagates a non-ENOENT filesystem error rather than silently treating it as "nothing installed"', async () => {
    const api = fakeApi(userDataDir);
    const accessError = Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' });
    const accessSpy = vi.spyOn(fs.promises, 'access').mockRejectedValueOnce(accessError);

    try {
      await expect(ensureWsmToolRegistered(api)).rejects.toThrow(/EACCES/);
    } finally {
      accessSpy.mockRestore();
    }
  });
});

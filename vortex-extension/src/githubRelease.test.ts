import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  buildAssetFileName,
  downloadReleaseAsset,
  HttpClient,
  resolveReleaseAsset,
} from './githubRelease';

// No test in this file makes a real network call - every HttpClient here is a fake, per
// this unit's own verification requirement ("a unit test against a mocked HTTP response
// for the download logic itself"). githubRelease.ts's real `nodeHttpsClient`
// implementation is only exercised (against a locally-built stand-in, never a real
// GitHub Release) by test/toolAcquisition.integration.test.ts's setup, not here.

function fakeClient(overrides: Partial<HttpClient> = {}): HttpClient {
  return {
    getJson: async () => {
      throw new Error('getJson was not expected to be called in this test');
    },
    downloadToFile: async () => {
      throw new Error('downloadToFile was not expected to be called in this test');
    },
    ...overrides,
  };
}

describe('buildAssetFileName', () => {
  it('matches release.yml\'s package-release job naming exactly', () => {
    expect(buildAssetFileName('0.6.2')).toBe('WitcherScriptMerger.Headless-0.6.2-win-x64.zip');
  });
});

describe('resolveReleaseAsset', () => {
  it('fetches the release-by-tag URL and returns the matching asset\'s download URL and size', async () => {
    const requestedUrls: string[] = [];
    const client = fakeClient({
      getJson: async (url: string) => {
        requestedUrls.push(url);
        return {
          tag_name: 'v0.6.2',
          assets: [
            { name: 'WitcherScriptMerger-0.6.2-win-x64.zip', browser_download_url: 'https://example.invalid/gui.zip', size: 111 },
            {
              name: 'WitcherScriptMerger.Headless-0.6.2-win-x64.zip',
              browser_download_url: 'https://example.invalid/headless-win.zip',
              size: 222,
            },
            {
              name: 'WitcherScriptMerger.Headless-0.6.2-linux-x64.tar.gz',
              browser_download_url: 'https://example.invalid/headless-linux.tar.gz',
              size: 333,
            },
          ],
        };
      },
    });

    const result = await resolveReleaseAsset({
      repo: 'TheValiantOne/WitcherScriptMerger',
      tag: 'v0.6.2',
      assetFileName: 'WitcherScriptMerger.Headless-0.6.2-win-x64.zip',
      client,
    });

    expect(requestedUrls).toEqual([
      'https://api.github.com/repos/TheValiantOne/WitcherScriptMerger/releases/tags/v0.6.2',
    ]);
    expect(result).toEqual({ downloadUrl: 'https://example.invalid/headless-win.zip', size: 222 });
  });

  it('throws a clear error when the requested asset is not present on the release', async () => {
    const client = fakeClient({
      getJson: async () => ({ tag_name: 'v0.6.2', assets: [{ name: 'something-else.zip', browser_download_url: 'x', size: 1 }] }),
    });

    await expect(
      resolveReleaseAsset({
        repo: 'TheValiantOne/WitcherScriptMerger',
        tag: 'v0.6.2',
        assetFileName: 'WitcherScriptMerger.Headless-0.6.2-win-x64.zip',
        client,
      }),
    ).rejects.toThrow(/no asset named 'WitcherScriptMerger\.Headless-0\.6\.2-win-x64\.zip'/);
  });

  it('wraps a failure fetching the release itself (e.g. tag not found) in a clear error', async () => {
    const client = fakeClient({
      getJson: async () => {
        throw new Error("GET '...' failed with HTTP 404: Not Found");
      },
    });

    await expect(
      resolveReleaseAsset({
        repo: 'TheValiantOne/WitcherScriptMerger',
        tag: 'v9.9.9',
        assetFileName: 'WitcherScriptMerger.Headless-9.9.9-win-x64.zip',
        client,
      }),
    ).rejects.toThrow(/Could not fetch release 'v9\.9\.9'/);
  });
});

describe('downloadReleaseAsset', () => {
  let scratchDir: string;

  beforeEach(() => {
    scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-download-test-'));
  });

  afterEach(() => {
    fs.rmSync(scratchDir, { recursive: true, force: true });
  });

  it('resolves when the downloaded byte count matches the expected size', async () => {
    const destPath = path.join(scratchDir, 'asset.zip');
    const client = fakeClient({
      downloadToFile: async (_url: string, dest: string) => {
        fs.writeFileSync(dest, Buffer.alloc(42, 1));
        return { bytesWritten: 42 };
      },
    });

    await expect(
      downloadReleaseAsset({ downloadUrl: 'https://example.invalid/asset.zip', destPath, expectedSize: 42, client }),
    ).resolves.toBeUndefined();
  });

  it('rejects with a clear error when the downloaded byte count does not match', async () => {
    const destPath = path.join(scratchDir, 'asset.zip');
    const client = fakeClient({
      downloadToFile: async (_url: string, dest: string) => {
        fs.writeFileSync(dest, Buffer.alloc(10, 1));
        return { bytesWritten: 10 };
      },
    });

    await expect(
      downloadReleaseAsset({ downloadUrl: 'https://example.invalid/asset.zip', destPath, expectedSize: 42, client }),
    ).rejects.toThrow(/got 10 bytes, expected 42/);
  });

  it('deletes the truncated/corrupt file from destPath when the byte count does not match', async () => {
    const destPath = path.join(scratchDir, 'asset.zip');
    const client = fakeClient({
      downloadToFile: async (_url: string, dest: string) => {
        fs.writeFileSync(dest, Buffer.alloc(10, 1));
        return { bytesWritten: 10 };
      },
    });

    await expect(
      downloadReleaseAsset({ downloadUrl: 'https://example.invalid/asset.zip', destPath, expectedSize: 42, client }),
    ).rejects.toThrow();

    expect(fs.existsSync(destPath)).toBe(false);
  });
});

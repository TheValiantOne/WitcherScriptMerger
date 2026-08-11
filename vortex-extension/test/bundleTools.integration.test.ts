import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { detectBundleTools, detectQuickBms, detectWccLite } from '../src/bundleTools';
import { NexusDownloader } from '../src/nexusDownloader';
import { ArchiveExtractor } from '../src/archiveExtractor';
import { acquireWccLite } from '../src/wccLiteAcquisition';

// Real, no-mock local-detection tests for this unit's bundle-tooling acquisition
// (Unit J) - the closest existing precedent is test/toolAcquisition.integration.test.ts,
// which proves registration/env-var-config against a real (locally-published) WSM
// binary rather than a mocked one. This file does the equivalent for QuickBMS/wcc_lite
// *detection*: real fs operations against a real scratch getBundleToolsDir(api)-shaped
// path, no mocks, no network - proving detectQuickBms/detectWccLite/detectBundleTools
// actually find a real file on a real filesystem, not just a fake fs stub.
//
// **Deviation from this unit's own instructions, disclosed here rather than silently**:
// the instructions ask to "mock the download response at the HTTP-client boundary
// (however src/githubRelease.ts's nodeHttpsClient is structured...)". wcc_lite is
// fetched through Vortex's own Nexus-download mechanism (api.ext.nexusDownload), not a
// plain HTTPS GET - Nexus doesn't serve an unauthenticated direct-download URL for a
// raw HttpClient to hit even in principle, so there is no nodeHttpsClient-shaped seam
// to reuse here. src/wccLiteAcquisition.test.ts already covers the mocked-download
// pipeline (download -> extract -> locate wcc_lite.exe) at the fast-unit-test tier,
// injecting a fake NexusDownloader/ArchiveExtractor, mirroring how
// src/toolAcquisition.test.ts covers acquireWsmTool's own mocked pipeline. Real Nexus
// API access is never attempted by any test in this repo.
//
// This repo's real archive-extraction implementation (archiveExtractor.ts's
// createVortexArchiveExtractor, backed by api.openArchive) has no meaningful behavior
// outside a real Vortex host either, per that module's own doc comment - so, like
// test/toolAcquisition.integration.test.ts sidesteps a real GitHub-Releases download,
// this file sidesteps real Nexus download/extraction and instead proves what it
// actually can for real: local detection, and that a pre-existing install short-circuits
// acquisition before any network/extraction attempt at all.

function fakeApi(userDataDir: string) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({}),
    ext: {},
  } as unknown as Parameters<typeof detectQuickBms>[0];
}

describe('bundle-tool detection end-to-end (real filesystem, no mocks)', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-bundletools-integration-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('detectWccLite finds a real wcc_lite.exe placed in the real getBundleToolsDir(api)-shaped path', async () => {
    const wccLiteDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'wcc_lite', 'bin', 'x64');
    fs.mkdirSync(wccLiteDir, { recursive: true });
    const exePath = path.join(wccLiteDir, 'wcc_lite.exe');
    fs.writeFileSync(exePath, Buffer.from('not a real PE, just a marker file'), 'binary');

    const api = fakeApi(userDataDir);
    await expect(detectWccLite(api)).resolves.toBe(exePath);
  });

  it('detectQuickBms finds a real quickbms.exe + witcher3.bms pair on a real filesystem', async () => {
    const quickBmsDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'QuickBMS');
    fs.mkdirSync(quickBmsDir, { recursive: true });
    fs.writeFileSync(path.join(quickBmsDir, 'quickbms.exe'), Buffer.from('marker'), 'binary');
    fs.writeFileSync(path.join(quickBmsDir, 'witcher3.bms'), Buffer.from('marker'), 'binary');

    const api = fakeApi(userDataDir);
    const result = await detectQuickBms(api);
    expect(result?.exePath).toBe(path.join(quickBmsDir, 'quickbms.exe'));
    expect(result?.pluginPath).toBe(path.join(quickBmsDir, 'witcher3.bms'));
  });

  it('detectBundleTools reports both tools found together, real fs only', async () => {
    const bundleToolsDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools');
    const quickBmsDir = path.join(bundleToolsDir, 'QuickBMS');
    fs.mkdirSync(quickBmsDir, { recursive: true });
    fs.writeFileSync(path.join(quickBmsDir, 'quickbms.exe'), 'marker', 'utf8');
    fs.writeFileSync(path.join(quickBmsDir, 'witcher3.bms'), 'marker', 'utf8');
    const wccLiteDir = path.join(bundleToolsDir, 'wcc_lite', 'bin', 'x64');
    fs.mkdirSync(wccLiteDir, { recursive: true });
    fs.writeFileSync(path.join(wccLiteDir, 'wcc_lite.exe'), 'marker', 'utf8');

    const api = fakeApi(userDataDir);
    const detected = await detectBundleTools(api);

    expect(detected.quickBmsPath).toBe(path.join(quickBmsDir, 'quickbms.exe'));
    expect(detected.quickBmsPluginPath).toBe(path.join(quickBmsDir, 'witcher3.bms'));
    expect(detected.wccLitePath).toBe(path.join(wccLiteDir, 'wcc_lite.exe'));
  });

  it('acquireWccLite finds a pre-existing real install and attempts no download/extraction at all', async () => {
    const wccLiteDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'wcc_lite', 'bin', 'x64');
    fs.mkdirSync(wccLiteDir, { recursive: true });
    const exePath = path.join(wccLiteDir, 'wcc_lite.exe');
    fs.writeFileSync(exePath, 'marker', 'utf8');

    const api = fakeApi(userDataDir);
    // These would reject the test (not just the acquisition) if actually invoked -
    // proves detection genuinely short-circuits before any network/extraction attempt.
    const downloader: NexusDownloader = {
      downloadModFile: vi.fn(async () => {
        throw new Error('downloadModFile should not have been called - a real install was already present');
      }),
    };
    const extractor: ArchiveExtractor = {
      extractAll: vi.fn(async () => {
        throw new Error('extractAll should not have been called - a real install was already present');
      }),
    };

    const result = await acquireWccLite({ api, downloader, extractor, fileId: 1 });

    expect(result).toBe(exePath);
    expect(downloader.downloadModFile).not.toHaveBeenCalled();
    expect(extractor.extractAll).not.toHaveBeenCalled();
  });
});

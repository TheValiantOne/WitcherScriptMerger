import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { detectBundleTools, detectQuickBms, detectWccLite, findFileByNameBounded } from './bundleTools';

function fakeApi(userDataDir: string, discoveryByGameState: Record<string, { tools?: Record<string, { path?: string }> }> = {}) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({ discoveryByGame: discoveryByGameState }),
  } as unknown as Parameters<typeof detectQuickBms>[0];
}

describe('bundleTools', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-bundletools-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  describe('detectQuickBms', () => {
    it('returns undefined when nothing is installed anywhere', async () => {
      const api = fakeApi(userDataDir);
      await expect(detectQuickBms(api)).resolves.toBeUndefined();
    });

    it('finds an install under this extension\'s own bundle-tools directory', async () => {
      const api = fakeApi(userDataDir);
      const quickBmsDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'QuickBMS');
      fs.mkdirSync(quickBmsDir, { recursive: true });
      fs.writeFileSync(path.join(quickBmsDir, 'quickbms.exe'), 'exe', 'utf8');
      fs.writeFileSync(path.join(quickBmsDir, 'witcher3.bms'), 'plugin', 'utf8');

      const result = await detectQuickBms(api);
      expect(result?.exePath).toBe(path.join(quickBmsDir, 'quickbms.exe'));
      expect(result?.pluginPath).toBe(path.join(quickBmsDir, 'witcher3.bms'));
    });

    it('requires both the exe and the plugin to count as found', async () => {
      const api = fakeApi(userDataDir);
      const quickBmsDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'QuickBMS');
      fs.mkdirSync(quickBmsDir, { recursive: true });
      fs.writeFileSync(path.join(quickBmsDir, 'quickbms.exe'), 'exe', 'utf8');
      // No witcher3.bms written.

      await expect(detectQuickBms(api)).resolves.toBeUndefined();
    });

    it('never pairs an exe from one root with a plugin from a different root', async () => {
      // Regression test: an earlier version resolved exePath/pluginPath via two
      // independent scans over the same root list, which could report a "found"
      // result mixing an exe-only install in one location with a plugin-only install
      // in another - two files that were never actually installed together.
      const ownDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'QuickBMS');
      fs.mkdirSync(ownDir, { recursive: true });
      fs.writeFileSync(path.join(ownDir, 'quickbms.exe'), 'exe-only', 'utf8');
      // No witcher3.bms in ownDir.

      const idcsExeDir = path.join(userDataDir, 'idcs-fork-install');
      const idcsToolsDir = path.join(idcsExeDir, 'Tools', 'QuickBMS');
      fs.mkdirSync(idcsToolsDir, { recursive: true });
      fs.writeFileSync(path.join(idcsToolsDir, 'witcher3.bms'), 'plugin-only', 'utf8');
      // No quickbms.exe in idcsToolsDir.

      const api = fakeApi(userDataDir, {
        witcher3: { tools: { W3ScriptMerger: { path: path.join(idcsExeDir, 'WitcherScriptMerger.exe') } } },
      });

      await expect(detectQuickBms(api)).resolves.toBeUndefined();
    });

    it('falls back to a prior IDCs-fork WitcherScriptMerger install\'s own Tools\\ folder', async () => {
      const idcsExeDir = path.join(userDataDir, 'idcs-fork-install');
      fs.mkdirSync(idcsExeDir, { recursive: true });
      const idcsToolsDir = path.join(idcsExeDir, 'Tools', 'QuickBMS');
      fs.mkdirSync(idcsToolsDir, { recursive: true });
      fs.writeFileSync(path.join(idcsToolsDir, 'quickbms.exe'), 'exe', 'utf8');
      fs.writeFileSync(path.join(idcsToolsDir, 'witcher3.bms'), 'plugin', 'utf8');

      const api = fakeApi(userDataDir, {
        witcher3: { tools: { W3ScriptMerger: { path: path.join(idcsExeDir, 'WitcherScriptMerger.exe') } } },
      });

      const result = await detectQuickBms(api);
      expect(result?.exePath).toBe(path.join(idcsToolsDir, 'quickbms.exe'));
    });

    it('prefers this extension\'s own bundle-tools directory over the IDCs-fork fallback', async () => {
      const ownDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'QuickBMS');
      fs.mkdirSync(ownDir, { recursive: true });
      fs.writeFileSync(path.join(ownDir, 'quickbms.exe'), 'exe', 'utf8');
      fs.writeFileSync(path.join(ownDir, 'witcher3.bms'), 'plugin', 'utf8');

      const idcsExeDir = path.join(userDataDir, 'idcs-fork-install');
      const idcsToolsDir = path.join(idcsExeDir, 'Tools', 'QuickBMS');
      fs.mkdirSync(idcsToolsDir, { recursive: true });
      fs.writeFileSync(path.join(idcsToolsDir, 'quickbms.exe'), 'exe', 'utf8');
      fs.writeFileSync(path.join(idcsToolsDir, 'witcher3.bms'), 'plugin', 'utf8');

      const api = fakeApi(userDataDir, {
        witcher3: { tools: { W3ScriptMerger: { path: path.join(idcsExeDir, 'WitcherScriptMerger.exe') } } },
      });

      const result = await detectQuickBms(api);
      expect(result?.exePath).toBe(path.join(ownDir, 'quickbms.exe'));
    });
  });

  describe('detectWccLite', () => {
    it('returns undefined when nothing is installed anywhere', async () => {
      const api = fakeApi(userDataDir);
      await expect(detectWccLite(api)).resolves.toBeUndefined();
    });

    it('finds an install at the canonical bin\\x64\\wcc_lite.exe layout', async () => {
      const api = fakeApi(userDataDir);
      const wccLiteDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'wcc_lite', 'bin', 'x64');
      fs.mkdirSync(wccLiteDir, { recursive: true });
      fs.writeFileSync(path.join(wccLiteDir, 'wcc_lite.exe'), 'exe', 'utf8');

      await expect(detectWccLite(api)).resolves.toBe(path.join(wccLiteDir, 'wcc_lite.exe'));
    });

    it('falls back to a prior IDCs-fork install\'s own Tools\\ folder', async () => {
      const idcsExeDir = path.join(userDataDir, 'idcs-fork-install');
      const idcsWccLiteDir = path.join(idcsExeDir, 'Tools', 'wcc_lite', 'bin', 'x64');
      fs.mkdirSync(idcsWccLiteDir, { recursive: true });
      fs.writeFileSync(path.join(idcsWccLiteDir, 'wcc_lite.exe'), 'exe', 'utf8');

      const api = fakeApi(userDataDir, {
        witcher3: { tools: { W3ScriptMerger: { path: path.join(idcsExeDir, 'WitcherScriptMerger.exe') } } },
      });

      await expect(detectWccLite(api)).resolves.toBe(path.join(idcsWccLiteDir, 'wcc_lite.exe'));
    });

    it('falls back to a bounded search under its own wcc_lite/ subfolder when the canonical layout does not match', async () => {
      const api = fakeApi(userDataDir);
      // Simulates an extracted "Official ModKit" archive whose internal layout differs
      // from the canonical bin\x64\wcc_lite.exe path - see wccLiteAcquisition.ts's own
      // "archive-layout caveat".
      const nestedDir = path.join(
        userDataDir,
        'witcherscriptmerger-vortex',
        'bundle-tools',
        'wcc_lite',
        'Modkit',
        'bin',
        'x64',
      );
      fs.mkdirSync(nestedDir, { recursive: true });
      fs.writeFileSync(path.join(nestedDir, 'wcc_lite.exe'), 'exe', 'utf8');

      await expect(detectWccLite(api)).resolves.toBe(path.join(nestedDir, 'wcc_lite.exe'));
    });

    it('does not find a wcc_lite.exe buried deeper than the bounded search depth', async () => {
      const api = fakeApi(userDataDir);
      const tooDeepDir = path.join(
        userDataDir,
        'witcherscriptmerger-vortex',
        'bundle-tools',
        'wcc_lite',
        'a',
        'b',
        'c',
        'd',
        'e',
        'f',
        'g',
      );
      fs.mkdirSync(tooDeepDir, { recursive: true });
      fs.writeFileSync(path.join(tooDeepDir, 'wcc_lite.exe'), 'exe', 'utf8');

      await expect(detectWccLite(api)).resolves.toBeUndefined();
    });
  });

  describe('detectBundleTools', () => {
    it('combines QuickBMS and wcc_lite detection', async () => {
      const api = fakeApi(userDataDir);
      const bundleToolsDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools');
      const quickBmsDir = path.join(bundleToolsDir, 'QuickBMS');
      fs.mkdirSync(quickBmsDir, { recursive: true });
      fs.writeFileSync(path.join(quickBmsDir, 'quickbms.exe'), 'exe', 'utf8');
      fs.writeFileSync(path.join(quickBmsDir, 'witcher3.bms'), 'plugin', 'utf8');
      const wccLiteDir = path.join(bundleToolsDir, 'wcc_lite', 'bin', 'x64');
      fs.mkdirSync(wccLiteDir, { recursive: true });
      fs.writeFileSync(path.join(wccLiteDir, 'wcc_lite.exe'), 'exe', 'utf8');

      const result = await detectBundleTools(api);
      expect(result).toEqual({
        quickBmsPath: path.join(quickBmsDir, 'quickbms.exe'),
        quickBmsPluginPath: path.join(quickBmsDir, 'witcher3.bms'),
        wccLitePath: path.join(wccLiteDir, 'wcc_lite.exe'),
      });
    });

    it('returns an all-undefined result when nothing is installed', async () => {
      const api = fakeApi(userDataDir);
      await expect(detectBundleTools(api)).resolves.toEqual({
        quickBmsPath: undefined,
        quickBmsPluginPath: undefined,
        wccLitePath: undefined,
      });
    });
  });

  describe('findFileByNameBounded', () => {
    it('is case-insensitive and returns undefined for a missing root directory', async () => {
      await expect(
        findFileByNameBounded(path.join(userDataDir, 'does-not-exist'), 'wcc_lite.exe', 6),
      ).resolves.toBeUndefined();
    });

    it('finds a shallower match before a deeper one', async () => {
      const shallowDir = path.join(userDataDir, 'shallow');
      const deepDir = path.join(userDataDir, 'a', 'b', 'deep');
      fs.mkdirSync(shallowDir, { recursive: true });
      fs.mkdirSync(deepDir, { recursive: true });
      fs.writeFileSync(path.join(shallowDir, 'WCC_LITE.EXE'), 'shallow', 'utf8');
      fs.writeFileSync(path.join(deepDir, 'wcc_lite.exe'), 'deep', 'utf8');

      const found = await findFileByNameBounded(userDataDir, 'wcc_lite.exe', 6);
      expect(found).toBe(path.join(shallowDir, 'WCC_LITE.EXE'));
    });
  });
});

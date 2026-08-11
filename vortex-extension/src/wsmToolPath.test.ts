import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { types } from 'vortex-api';
import { WSM_HEADLESS_EXE_NAME } from './storage';
import {
  WSM_TOOL_PATH_OVERRIDE_FILENAME,
  getWsmToolPathOverride,
  resolveWsmExe,
  resolveWsmExePathIfUsable,
  setWsmToolPathOverride,
} from './wsmToolPath';

// Direct coverage for the central "which WSM executable?" resolver - the precedence
// (override > managed), the deliberate no-silent-fallback policy on a stale override,
// and the override file's persistence round trip. conflictScan.test.ts /
// mergeHistoryDashlet.test.ts cover their own delegating wrappers.
describe('wsmToolPath', () => {
  let userDataDir: string;

  const api = (): types.IExtensionApi =>
    ({
      getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    }) as unknown as types.IExtensionApi;

  const storageDir = (): string => path.join(userDataDir, 'witcherscriptmerger-vortex');
  const managedExePath = (): string => path.join(storageDir(), 'tool', WSM_HEADLESS_EXE_NAME);

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-toolpath-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('resolves to none when neither an override nor a managed install exists', async () => {
    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'none' });
    await expect(resolveWsmExePathIfUsable(api())).resolves.toBeUndefined();
  });

  it('resolves to the managed install when it exists and no override is set', async () => {
    fs.mkdirSync(path.dirname(managedExePath()), { recursive: true });
    fs.writeFileSync(managedExePath(), 'managed exe', 'utf8');

    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'managed', exePath: managedExePath() });
  });

  it('prefers an existing override over an existing managed install', async () => {
    fs.mkdirSync(path.dirname(managedExePath()), { recursive: true });
    fs.writeFileSync(managedExePath(), 'managed exe', 'utf8');
    const overrideExe = path.join(userDataDir, 'elsewhere', WSM_HEADLESS_EXE_NAME);
    fs.mkdirSync(path.dirname(overrideExe), { recursive: true });
    fs.writeFileSync(overrideExe, 'override exe', 'utf8');
    await setWsmToolPathOverride(api(), overrideExe);

    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'override', exePath: overrideExe });
  });

  it('reports a stale override as override-missing rather than silently falling back to the managed install', async () => {
    fs.mkdirSync(path.dirname(managedExePath()), { recursive: true });
    fs.writeFileSync(managedExePath(), 'managed exe', 'utf8');
    const goneExe = path.join(userDataDir, 'uninstalled', WSM_HEADLESS_EXE_NAME);
    await setWsmToolPathOverride(api(), goneExe);

    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'override-missing', overridePath: goneExe });
    // The convenience wrapper treats it as unusable - never the managed path.
    await expect(resolveWsmExePathIfUsable(api())).resolves.toBeUndefined();
  });

  it('round-trips the override through its persistence file, and clearing restores managed resolution', async () => {
    fs.mkdirSync(path.dirname(managedExePath()), { recursive: true });
    fs.writeFileSync(managedExePath(), 'managed exe', 'utf8');
    const overrideExe = path.join(userDataDir, WSM_HEADLESS_EXE_NAME);
    fs.writeFileSync(overrideExe, 'override exe', 'utf8');

    await setWsmToolPathOverride(api(), overrideExe);
    await expect(getWsmToolPathOverride(api())).resolves.toBe(overrideExe);
    expect(fs.existsSync(path.join(storageDir(), WSM_TOOL_PATH_OVERRIDE_FILENAME))).toBe(true);

    await setWsmToolPathOverride(api(), null);
    await expect(getWsmToolPathOverride(api())).resolves.toBeUndefined();
    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'managed', exePath: managedExePath() });
  });

  it('treats a blank override file as no override, and clearing an already-absent override is a no-op', async () => {
    fs.mkdirSync(storageDir(), { recursive: true });
    fs.writeFileSync(path.join(storageDir(), WSM_TOOL_PATH_OVERRIDE_FILENAME), '   \r\n', 'utf8');

    await expect(getWsmToolPathOverride(api())).resolves.toBeUndefined();
    await expect(resolveWsmExe(api())).resolves.toEqual({ kind: 'none' });
    await expect(setWsmToolPathOverride(api(), null)).resolves.toBeUndefined();
    await expect(setWsmToolPathOverride(api(), null)).resolves.toBeUndefined();
  });
});

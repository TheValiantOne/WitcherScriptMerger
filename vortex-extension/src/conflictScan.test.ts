import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WSM_HEADLESS_EXE_NAME } from './toolAcquisition';

// vi.mock factories are hoisted above imports, so the mock function itself has to come
// from vi.hoisted - same pattern index.test.ts already uses to isolate index.ts's own
// wiring from toolAcquisition.ts's real behavior. Here it isolates conflictScan.ts's own
// orchestration (exePath/env resolution, connect/scan/close sequencing) from
// mcpClient.ts's real process-spawning behavior, which is instead covered end-to-end by
// test/conflictScan.integration.test.ts.
const { connectMock } = vi.hoisted(() => ({
  connectMock: vi.fn(),
}));

vi.mock('./mcpClient', () => ({
  WsmMcpClient: { connect: connectMock },
}));

import { getWsmExePath, isWsmToolAcquired, scanWsmConflicts } from './conflictScan';

function fakeApi(userDataDir: string, discoveredGamePath?: string) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({ discoveryByGame: { witcher3: discoveredGamePath !== undefined ? { path: discoveredGamePath } : undefined } }),
  } as unknown as Parameters<typeof scanWsmConflicts>[0];
}

describe('getWsmExePath', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-exepath-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('resolves to the managed install exe once it exists', async () => {
    const api = fakeApi(userDataDir);
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe bytes', 'utf8');

    await expect(getWsmExePath(api)).resolves.toBe(path.join(toolDir, WSM_HEADLESS_EXE_NAME));
  });

  it('resolves to undefined when nothing usable exists', async () => {
    await expect(getWsmExePath(fakeApi(userDataDir))).resolves.toBeUndefined();
  });

  it('prefers a user override over the managed install (wsmToolPath.ts precedence)', async () => {
    const api = fakeApi(userDataDir);
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'managed exe', 'utf8');
    const overrideExe = path.join(userDataDir, 'WitcherScriptMerger.Headless.exe');
    fs.writeFileSync(overrideExe, 'override exe', 'utf8');
    fs.writeFileSync(
      path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool-path-override.txt'),
      overrideExe,
      'utf8',
    );

    await expect(getWsmExePath(api)).resolves.toBe(overrideExe);
  });
});

describe('isWsmToolAcquired', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-conflictscan-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('returns false when no exe has been acquired yet', async () => {
    const api = fakeApi(userDataDir);
    await expect(isWsmToolAcquired(api)).resolves.toBe(false);
  });

  it('returns true once the exe exists on disk', async () => {
    const api = fakeApi(userDataDir);
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe bytes', 'utf8');

    await expect(isWsmToolAcquired(api)).resolves.toBe(true);
  });

  // Mirrors toolAcquisition.test.ts's equivalent test for ensureWsmToolRegistered - this
  // function is documented as mirroring that module's own pathExists helper in
  // substance, not just in name, specifically so a locked/permission-denied file isn't
  // silently mistaken for "nothing installed yet".
  it('propagates a non-ENOENT filesystem error rather than silently treating it as "not acquired"', async () => {
    const api = fakeApi(userDataDir);
    const accessError = Object.assign(new Error('EBUSY: resource busy or locked'), { code: 'EBUSY' });
    const accessSpy = vi.spyOn(fs.promises, 'access').mockRejectedValueOnce(accessError);

    try {
      await expect(isWsmToolAcquired(api)).rejects.toThrow(/EBUSY/);
    } finally {
      accessSpy.mockRestore();
    }
  });
});

describe('scanWsmConflicts', () => {
  let userDataDir: string;
  let stagedExePath: string;

  // scanWsmConflicts resolves the exe through wsmToolPath.ts now and refuses to spawn
  // when nothing usable exists, so these orchestration fixtures stage a real (fake
  // bytes) exe file in a real temp dir instead of handing it a path that was never
  // checked before this unit.
  beforeEach(() => {
    connectMock.mockReset();
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-scan-test-'));
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    stagedExePath = path.join(toolDir, WSM_HEADLESS_EXE_NAME);
    fs.writeFileSync(stagedExePath, 'fake exe bytes', 'utf8');
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('connects with the acquired exe path and Witcher 3 discovered game directory, scans, then always closes', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockResolvedValue([{ relativePath: 'foo.ws' }]);
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(userDataDir, path.join('C:', 'Games', 'Witcher3'));

    const result = await scanWsmConflicts(api);

    expect(result).toEqual([{ relativePath: 'foo.ws' }]);
    expect(connectMock).toHaveBeenCalledTimes(1);
    const connectArgs = connectMock.mock.calls[0][0] as { exePath: string; env: Record<string, string>; requestTimeoutMs: number };
    expect(connectArgs.exePath).toBe(stagedExePath);
    expect(connectArgs.env.WSM_GameDirectory).toBe(path.join('C:', 'Games', 'Witcher3'));
    expect(scanConflictsMock).toHaveBeenCalledTimes(1);
    expect(closeMock).toHaveBeenCalledTimes(1);
  });

  // This handler runs inside Vortex's own emitAndAwait('did-deploy', ...) await window
  // (see index.ts's own doc comment) - a hung WSM process shouldn't be able to block
  // Vortex's reported deployment-completion for mcpClient.ts's full general-purpose 30s
  // default (up to ~60s worst case across two requests).
  it('requests a shorter-than-default MCP timeout, since this runs inside did-deploy\'s own blocking window', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockResolvedValue([]);
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(userDataDir);
    await scanWsmConflicts(api);

    const connectArgs = connectMock.mock.calls[0][0] as { requestTimeoutMs?: number };
    expect(connectArgs.requestTimeoutMs).toBeDefined();
    expect(connectArgs.requestTimeoutMs).toBeLessThan(30_000);
    expect(connectArgs.requestTimeoutMs).toBeGreaterThan(0);
  });

  it('still closes the client when scanConflicts itself throws', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockRejectedValue(new Error('boom'));
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(userDataDir);

    await expect(scanWsmConflicts(api)).rejects.toThrow('boom');
    expect(closeMock).toHaveBeenCalledTimes(1);
  });

  it('does not attempt to close when connect itself fails (nothing to close)', async () => {
    connectMock.mockRejectedValue(new Error('spawn failed'));

    const api = fakeApi(userDataDir);

    await expect(scanWsmConflicts(api)).rejects.toThrow('spawn failed');
  });

  // Mirrors toolAcquisition.test.ts's "coalesces concurrent calls" test for
  // acquireWsmTool - same rationale: overlapping did-deploy events must not each spawn
  // their own WSM process against the same mods folder, and must not let an
  // out-of-order resolution feed a stale result to a later caller.
  it('coalesces overlapping calls onto a single in-flight connect/scan', async () => {
    let resolveScan: ((value: unknown[]) => void) | undefined;
    const scanConflictsMock = vi.fn().mockReturnValue(
      new Promise((resolve) => {
        resolveScan = resolve;
      }),
    );
    const closeMock = vi.fn().mockResolvedValue(undefined);
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(userDataDir);

    const first = scanWsmConflicts(api);
    const second = scanWsmConflicts(api);

    resolveScan?.([{ relativePath: 'a.ws' }]);
    const [firstResult, secondResult] = await Promise.all([first, second]);

    expect(firstResult).toBe(secondResult);
    expect(connectMock).toHaveBeenCalledTimes(1);
    expect(scanConflictsMock).toHaveBeenCalledTimes(1);
  });

  it('allows a fresh scan after a prior one has fully completed (does not coalesce sequential calls)', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockResolvedValue([]);
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(userDataDir);

    await scanWsmConflicts(api);
    await scanWsmConflicts(api);

    expect(connectMock).toHaveBeenCalledTimes(2);
  });
});

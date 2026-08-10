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
  it('points at the acquired WSM Headless exe under the tool storage dir', () => {
    const api = fakeApi(path.join('C:', 'fake', 'userData'));
    expect(getWsmExePath(api)).toBe(
      path.join('C:', 'fake', 'userData', 'witcherscriptmerger-vortex', 'tool', WSM_HEADLESS_EXE_NAME),
    );
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
});

describe('scanWsmConflicts', () => {
  beforeEach(() => {
    connectMock.mockReset();
  });

  it('connects with the acquired exe path and Witcher 3 discovered game directory, scans, then always closes', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockResolvedValue([{ relativePath: 'foo.ws' }]);
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(path.join('C:', 'fake', 'userData'), path.join('C:', 'Games', 'Witcher3'));

    const result = await scanWsmConflicts(api);

    expect(result).toEqual([{ relativePath: 'foo.ws' }]);
    expect(connectMock).toHaveBeenCalledTimes(1);
    const connectArgs = connectMock.mock.calls[0][0] as { exePath: string; env: Record<string, string> };
    expect(connectArgs.exePath).toBe(getWsmExePath(api));
    expect(connectArgs.env.WSM_GameDirectory).toBe(path.join('C:', 'Games', 'Witcher3'));
    expect(scanConflictsMock).toHaveBeenCalledTimes(1);
    expect(closeMock).toHaveBeenCalledTimes(1);
  });

  it('still closes the client when scanConflicts itself throws', async () => {
    const closeMock = vi.fn().mockResolvedValue(undefined);
    const scanConflictsMock = vi.fn().mockRejectedValue(new Error('boom'));
    connectMock.mockResolvedValue({ scanConflicts: scanConflictsMock, close: closeMock });

    const api = fakeApi(path.join('C:', 'fake', 'userData'));

    await expect(scanWsmConflicts(api)).rejects.toThrow('boom');
    expect(closeMock).toHaveBeenCalledTimes(1);
  });

  it('does not attempt to close when connect itself fails (nothing to close)', async () => {
    connectMock.mockRejectedValue(new Error('spawn failed'));

    const api = fakeApi(path.join('C:', 'fake', 'userData'));

    await expect(scanWsmConflicts(api)).rejects.toThrow('spawn failed');
  });
});

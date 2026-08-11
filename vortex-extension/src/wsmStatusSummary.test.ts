import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WsmMcpClient, WsmMcpClientOptions } from './mcpClient';
import { WSM_HEADLESS_EXE_NAME } from './toolAcquisition';
import { getWsmStatusSummary } from './wsmStatusSummary';

function fakeApi(userDataDir: string) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({}),
  } as unknown as Parameters<typeof getWsmStatusSummary>[0];
}

describe('getWsmStatusSummary', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-statussummary-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it("reports 'not-acquired' when no WSM build has been downloaded yet", async () => {
    const api = fakeApi(userDataDir);
    await expect(getWsmStatusSummary(api)).resolves.toEqual({ kind: 'not-acquired' });
  });

  it("reports 'error' when the WSM process fails to spawn/connect", async () => {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe', 'utf8');

    const api = fakeApi(userDataDir);
    const connect = vi.fn(async (_options: WsmMcpClientOptions) => {
      throw new Error('spawn failed');
    }) as unknown as typeof WsmMcpClient.connect;

    const result = await getWsmStatusSummary(api, { connect });
    expect(result).toEqual({ kind: 'error', message: 'spawn failed' });
  });

  it("reports 'ok' with the status and detected bundle tools when the WSM process responds", async () => {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe', 'utf8');

    const wccLiteDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'bundle-tools', 'wcc_lite', 'bin', 'x64');
    fs.mkdirSync(wccLiteDir, { recursive: true });
    fs.writeFileSync(path.join(wccLiteDir, 'wcc_lite.exe'), 'exe', 'utf8');

    const closeSpy = vi.fn(async () => undefined);
    const getStatusSpy = vi.fn(async () => ({
      gameDirectory: 'C:\\Game',
      modsDirectory: 'C:\\Game\\Mods',
      dependenciesValid: false,
      textMergeDependenciesValid: true,
      bundleDependenciesValid: false,
      modsDirectoryExists: true,
      mergedModName: 'mod0000_MergedFiles',
      conflictCount: 3,
    }));
    const fakeClient = { getStatus: getStatusSpy, close: closeSpy } as unknown as WsmMcpClient;
    const connect = vi.fn(async (_options: WsmMcpClientOptions) => fakeClient) as unknown as typeof WsmMcpClient.connect;

    const api = fakeApi(userDataDir);
    const result = await getWsmStatusSummary(api, { connect });

    expect(result.kind).toBe('ok');
    if (result.kind === 'ok') {
      expect(result.status.conflictCount).toBe(3);
      expect(result.bundleTools.wccLitePath).toBe(path.join(wccLiteDir, 'wcc_lite.exe'));
    }
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it("reports 'error' (not a thrown exception) when getStatus() itself rejects, and still closes the client", async () => {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe', 'utf8');

    const closeSpy = vi.fn(async () => undefined);
    const fakeClient = {
      getStatus: vi.fn(async () => {
        throw new Error('get_status failed');
      }),
      close: closeSpy,
    } as unknown as WsmMcpClient;
    const connect = vi.fn(async (_options: WsmMcpClientOptions) => fakeClient) as unknown as typeof WsmMcpClient.connect;

    const api = fakeApi(userDataDir);
    const result = await getWsmStatusSummary(api, { connect });

    expect(result).toEqual({ kind: 'error', message: 'get_status failed' });
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('does not let a close() failure shadow a successful getStatus() result', async () => {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    fs.writeFileSync(path.join(toolDir, WSM_HEADLESS_EXE_NAME), 'fake exe', 'utf8');

    const fakeClient = {
      getStatus: vi.fn(async () => ({
        gameDirectory: '',
        modsDirectory: '',
        dependenciesValid: true,
        textMergeDependenciesValid: true,
        bundleDependenciesValid: true,
        modsDirectoryExists: false,
        mergedModName: '',
        conflictCount: 0,
      })),
      close: vi.fn(async () => {
        throw new Error('close failed');
      }),
    } as unknown as WsmMcpClient;
    const connect = vi.fn(async (_options: WsmMcpClientOptions) => fakeClient) as unknown as typeof WsmMcpClient.connect;

    const api = fakeApi(userDataDir);
    const result = await getWsmStatusSummary(api, { connect });

    expect(result.kind).toBe('ok');
  });
});

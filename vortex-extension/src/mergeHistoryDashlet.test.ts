import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { types } from 'vortex-api';
import { WITCHER3_GAME_ID } from './gating';
import { fetchMergeHistory, registerMergeHistoryDashlet, resolveWsmExePath } from './mergeHistoryDashlet';
import { WsmMcpClient } from './mcpClient';
import { WSM_HEADLESS_EXE_NAME } from './toolAcquisition';

// vitest never mounts/renders MergeHistoryDashlet itself here - vitest.config.ts runs in
// vitest's default 'node' environment (no jsdom), so there's no DOM to mount a React
// component into. What's tested instead: fetchMergeHistory's own data-fetch/lifecycle
// contract (the part with real logic - deciding what to fetch, always closing the
// client) and registerMergeHistoryDashlet's own registration-call shape/gating, mirroring
// how index.test.ts exercises index.ts's wiring without ever rendering anything either.

function fakeApi(userDataDir: string) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({ activeGameId: WITCHER3_GAME_ID }),
  } as unknown as types.IExtensionApi;
}

describe('resolveWsmExePath', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-history-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('returns null when no WSM build has been acquired yet', async () => {
    await expect(resolveWsmExePath(fakeApi(userDataDir))).resolves.toBeNull();
  });

  it('returns the exe path when a WSM build has been acquired', async () => {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    const exePath = path.join(toolDir, WSM_HEADLESS_EXE_NAME);
    fs.writeFileSync(exePath, 'fake exe bytes', 'utf8');

    await expect(resolveWsmExePath(fakeApi(userDataDir))).resolves.toBe(exePath);
  });
});

describe('fetchMergeHistory', () => {
  let userDataDir: string;

  beforeEach(() => {
    userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-history-fetch-test-'));
  });

  afterEach(() => {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  });

  it('returns not-installed without attempting to connect when no WSM build is acquired', async () => {
    const connect = vi.fn();

    const result = await fetchMergeHistory(fakeApi(userDataDir), { connect });

    expect(result).toEqual({ status: 'not-installed' });
    expect(connect).not.toHaveBeenCalled();
  });

  function acquireFakeExe(): string {
    const toolDir = path.join(userDataDir, 'witcherscriptmerger-vortex', 'tool');
    fs.mkdirSync(toolDir, { recursive: true });
    const exePath = path.join(toolDir, WSM_HEADLESS_EXE_NAME);
    fs.writeFileSync(exePath, 'fake exe bytes', 'utf8');
    return exePath;
  }

  it('returns loaded merges and closes the client on success', async () => {
    const exePath = acquireFakeExe();
    const merges = [
      {
        relativePath: 'content\\scripts\\game\\r4Game.ws',
        mergedModName: 'mod0000_MergedFiles',
        mods: [{ name: 'modAlpha', hash: '1a2b3c4d' }],
      },
    ];
    const close = vi.fn().mockResolvedValue(undefined);
    const listMerges = vi.fn().mockResolvedValue(merges);
    const fakeClient = { listMerges, close } as unknown as WsmMcpClient;
    const connect = vi.fn().mockResolvedValue(fakeClient);

    const result = await fetchMergeHistory(fakeApi(userDataDir), { connect });

    expect(connect).toHaveBeenCalledWith({ exePath });
    expect(result).toEqual({ status: 'loaded', merges });
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('returns an error result but still closes the client when listMerges rejects', async () => {
    acquireFakeExe();
    const close = vi.fn().mockResolvedValue(undefined);
    const listMerges = vi.fn().mockRejectedValue(new Error('tool call failed'));
    const fakeClient = { listMerges, close } as unknown as WsmMcpClient;
    const connect = vi.fn().mockResolvedValue(fakeClient);

    const result = await fetchMergeHistory(fakeApi(userDataDir), { connect });

    expect(result).toEqual({ status: 'error', message: 'tool call failed' });
    // The documented "close in a finally" policy (mcpClient.ts) - a failed tool call
    // must never leak the spawned WSM process.
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('returns an error result without attempting to close anything when connect itself rejects', async () => {
    acquireFakeExe();
    const connect = vi.fn().mockRejectedValue(new Error('spawn failed'));

    const result = await fetchMergeHistory(fakeApi(userDataDir), { connect });

    expect(result).toEqual({ status: 'error', message: 'spawn failed' });
  });

  it('wraps a non-Error rejection into a string message rather than throwing', async () => {
    acquireFakeExe();
    const connect = vi.fn().mockRejectedValue('a plain string failure');

    const result = await fetchMergeHistory(fakeApi(userDataDir), { connect });

    expect(result).toEqual({ status: 'error', message: 'a plain string failure' });
  });
});

describe('registerMergeHistoryDashlet', () => {
  function fakeContext(activeGameId: string | undefined) {
    const state = { activeGameId };
    const registerDashlet = vi.fn();
    const context = {
      api: { getState: () => state },
      registerDashlet,
    } as unknown as types.IExtensionContext;
    return { context, registerDashlet, setActiveGame: (id: string | undefined) => (state.activeGameId = id) };
  }

  it('registers a dashlet with the expected title/size/position/options shape', () => {
    const { context, registerDashlet } = fakeContext(WITCHER3_GAME_ID);

    registerMergeHistoryDashlet(context);

    expect(registerDashlet).toHaveBeenCalledTimes(1);
    const [title, width, height, position, component, , , options] = registerDashlet.mock.calls[0];
    expect(title).toBe('WitcherScriptMerger History');
    expect(width).toBeGreaterThanOrEqual(1);
    expect(height).toBeGreaterThanOrEqual(1);
    expect(typeof position).toBe('number');
    expect(component).toBeTruthy();
    expect(options).toEqual({ closable: true });
  });

  it('passes context.api through the props callback', () => {
    const { context, registerDashlet } = fakeContext(WITCHER3_GAME_ID);

    registerMergeHistoryDashlet(context);

    const propsCallback = registerDashlet.mock.calls[0][6] as () => { api: unknown };
    expect(propsCallback().api).toBe(context.api);
  });

  it('gates isVisible on isWitcher3Active, re-evaluated live rather than cached at registration time', () => {
    const { context, registerDashlet, setActiveGame } = fakeContext('skyrimse');

    registerMergeHistoryDashlet(context);

    const isVisible = registerDashlet.mock.calls[0][5] as (state: unknown) => boolean;
    expect(isVisible(undefined)).toBe(false);

    setActiveGame(WITCHER3_GAME_ID);
    expect(isVisible(undefined)).toBe(true);
  });
});

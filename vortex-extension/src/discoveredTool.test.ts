import * as path from 'path';
import { describe, expect, it, vi } from 'vitest';
import { WITCHER3_GAME_ID } from './gating';
import { buildWsmDiscoveredTool, registerWsmDiscoveredTool, WSM_TOOL_ID } from './discoveredTool';

const EXE_PATH = path.join('C:', 'fake', 'tool', 'WitcherScriptMerger.Headless.exe');

describe('WSM_TOOL_ID', () => {
  it('does not collide with game-witcher3\'s own built-in tool ID', () => {
    // See docs/vortex-extension-design.md section 0: game-witcher3 already registers
    // 'W3ScriptMerger'. There is no API to hide/replace another extension's tool
    // registration, so this must be a distinct ID.
    expect(WSM_TOOL_ID).not.toBe('W3ScriptMerger');
  });
});

describe('buildWsmDiscoveredTool', () => {
  it('builds a tool pointing at the given exe path, custom and visible', () => {
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH });

    expect(tool.id).toBe(WSM_TOOL_ID);
    expect(tool.path).toBe(EXE_PATH);
    expect(tool.custom).toBe(true);
    expect(tool.hidden).toBe(false);
    expect(tool.requiredFiles).toEqual([]);
    expect(tool.workingDirectory).toBe(path.dirname(EXE_PATH));
    expect(tool.executable()).toBe('WitcherScriptMerger.Headless.exe');
  });

  it('defaults to running a real, overwrite-enabled merge when launched directly from the Tools dashboard', () => {
    // A bare Tools-dashboard launch has no preview/confirmation dialog to gate on (see
    // resolveScriptConflicts.ts) - it must resolve conflicts outright, the same way that
    // action's own confirmed (non-dry-run) merge call does, rather than printing usage
    // and exiting (the pre-fix behavior with no `parameters` at all).
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH });
    expect(tool.parameters).toEqual(['merge', '--overwrite']);
  });

  it('defaults environment to an empty object when none is given', () => {
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH });
    expect(tool.environment).toEqual({});
  });

  it('carries through a supplied environment map unchanged', () => {
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH, environment: { WSM_ModsDirectory: 'C:\\Mods' } });
    expect(tool.environment).toEqual({ WSM_ModsDirectory: 'C:\\Mods' });
  });

  it('round-trips every field except the known-non-serializable executable function through JSON', () => {
    // ITool.executable is typed as a function - it cannot survive JSON.stringify, a
    // known, documented, unavoidable limitation shared with game-witcher3's own
    // W3ScriptMerger registration (see this module's own doc comment). Every other
    // field must survive, since Vortex persists discovered-tools state to disk.
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH, environment: { WSM_ModsDirectory: 'C:\\Mods' } });
    const roundTripped = JSON.parse(JSON.stringify(tool)) as Record<string, unknown>;

    const expectedWithoutExecutable: Record<string, unknown> = { ...tool };
    delete expectedWithoutExecutable.executable;
    expect(roundTripped).toEqual(expectedWithoutExecutable);
  });
});

describe('registerWsmDiscoveredTool', () => {
  it('dispatches addDiscoveredTool for witcher3 with the given tool, marked custom/manual', () => {
    const dispatch = vi.fn();
    const api = { store: { dispatch } } as unknown as Parameters<typeof registerWsmDiscoveredTool>[0];
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH });

    registerWsmDiscoveredTool(api, tool);

    expect(dispatch).toHaveBeenCalledTimes(1);
    const dispatchedAction = dispatch.mock.calls[0][0] as { payload: { gameId: string; toolId: string; result: unknown; manual: boolean } };
    expect(dispatchedAction.payload.gameId).toBe(WITCHER3_GAME_ID);
    expect(dispatchedAction.payload.toolId).toBe(WSM_TOOL_ID);
    expect(dispatchedAction.payload.result).toBe(tool);
    expect(dispatchedAction.payload.manual).toBe(true);
  });

  it('throws rather than silently no-oping when api.store is unavailable', () => {
    // Callers (toolAcquisition.ts's ensureWsmToolRegistered/acquireWsmTool) treat this
    // function completing without throwing as proof the tool was actually registered -
    // a silent no-op here would make them report success with nothing really dispatched.
    const api = { store: undefined } as unknown as Parameters<typeof registerWsmDiscoveredTool>[0];
    const tool = buildWsmDiscoveredTool({ exePath: EXE_PATH });

    expect(() => registerWsmDiscoveredTool(api, tool)).toThrow(/store is unavailable/);
  });
});

import { describe, expect, it, vi } from 'vitest';
import { WITCHER3_GAME_ID } from './gating';
import { WSM_TOOL_ID } from './discoveredTool';
import { MergeConflictsResult, WsmMcpClientOptions } from './mcpClient';
import { resolveScriptConflicts, WsmMergeClient } from './resolveAction';

function mergeResult(overrides: Partial<MergeConflictsResult> = {}): MergeConflictsResult {
  return {
    merged: [],
    skipped: [],
    unmatched: [],
    dryRun: false,
    functionLevelDecisions: [],
    ...overrides,
  };
}

interface FakeDialogResponse {
  action: string;
}

/** A minimal stand-in for IExtensionApi - just enough surface for
 *  resolveScriptConflicts's own logic (api.getState() fed through
 *  selectors.discoveryByGame, sendNotification, showErrorNotification, showDialog).
 *  Matches gating.test.ts/toolAcquisition.test.ts's own fakeApi philosophy: a
 *  simplified fake shaped to match test/testUtils/vortexApiStub.ts's simplified
 *  selectors, not a replica of Vortex's real Redux state. */
function fakeApi(options: {
  toolPath?: string;
  toolEnvironment?: Record<string, string>;
  dialogResponses?: FakeDialogResponse[];
} = {}) {
  const state = {
    discoveryByGame:
      options.toolPath !== undefined
        ? {
            [WITCHER3_GAME_ID]: {
              tools: {
                [WSM_TOOL_ID]: { path: options.toolPath, environment: options.toolEnvironment ?? {} },
              },
            },
          }
        : {},
  };

  const dialogQueue = [...(options.dialogResponses ?? [])];
  const showDialogCalls: Array<{ type: string; title: string; content: unknown; actions: Array<{ label: string }> }> = [];
  const notifications: unknown[] = [];
  const errorNotifications: Array<{ message: string; detail: unknown }> = [];

  const api = {
    getState: () => state,
    sendNotification: vi.fn((notification: unknown) => {
      notifications.push(notification);
      return 'notification-id';
    }),
    dismissNotification: vi.fn(),
    showErrorNotification: vi.fn((message: string, detail: unknown) => {
      errorNotifications.push({ message, detail });
    }),
    showDialog: vi.fn(async (type: string, title: string, content: unknown, actions: Array<{ label: string }>) => {
      showDialogCalls.push({ type, title, content, actions });
      const next = dialogQueue.shift();
      return { action: next?.action ?? actions[0]?.label ?? '', input: {} };
    }),
  };

  return {
    api: api as unknown as Parameters<typeof resolveScriptConflicts>[0],
    showDialogCalls,
    notifications,
    errorNotifications,
  };
}

/** A fake `connect` - returns queued results/errors in call order and records every
 *  `exePath`/`env` it was called with, plus how many of the clients it produced were
 *  closed - proves `resolveScriptConflicts` closes every client it opens, even on the
 *  error path (via `finally`). */
function fakeConnect(outcomes: Array<{ result?: MergeConflictsResult; error?: Error }>) {
  const calls: WsmMcpClientOptions[] = [];
  let closedCount = 0;
  let callIndex = 0;

  const connect = vi.fn(async (options: WsmMcpClientOptions): Promise<WsmMergeClient> => {
    calls.push(options);
    const outcome = outcomes[callIndex++];
    return {
      mergeConflicts: async () => {
        if (outcome.error) {
          throw outcome.error;
        }
        return outcome.result!;
      },
      close: async () => {
        closedCount += 1;
      },
    };
  });

  return { connect, calls, closedCount: () => closedCount };
}

describe('resolveScriptConflicts', () => {
  it('shows an error notification and never connects when no WSM tool has been registered', async () => {
    const { api, notifications, showDialogCalls } = fakeApi({});
    const { connect } = fakeConnect([]);

    await resolveScriptConflicts(api, { connect });

    expect(connect).not.toHaveBeenCalled();
    expect(showDialogCalls).toHaveLength(0);
    expect(notifications).toHaveLength(1);
    expect((notifications[0] as { type: string }).type).toBe('error');
  });

  it('shows a "nothing to merge" dialog and stops, without a second connect, when the preview finds no conflicts', async () => {
    const { api, showDialogCalls } = fakeApi({ toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe' });
    const { connect } = fakeConnect([{ result: mergeResult() }]);

    await resolveScriptConflicts(api, { connect });

    expect(connect).toHaveBeenCalledTimes(1);
    expect(showDialogCalls).toHaveLength(1);
    expect(showDialogCalls[0].type).toBe('info');
  });

  it('shows the preview dialog and does not run a real merge when the user cancels', async () => {
    const { api, showDialogCalls } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Cancel' }],
    });
    const { connect } = fakeConnect([{ result: mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] }) }]);

    await resolveScriptConflicts(api, { connect });

    expect(connect).toHaveBeenCalledTimes(1);
    expect(showDialogCalls).toHaveLength(1);
    expect(showDialogCalls[0].type).toBe('question');
    expect(showDialogCalls[0].actions.map((a) => a.label)).toEqual(['Cancel', 'Merge Now']);
  });

  it('runs the real merge with a second, separate client on confirmation, and shows a result dialog', async () => {
    const toolPath = 'C:\\wsm\\WitcherScriptMerger.Headless.exe';
    const toolEnvironment = { WSM_GameDirectory: 'C:\\Games\\Witcher3' };
    const { api, showDialogCalls } = fakeApi({
      toolPath,
      toolEnvironment,
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const final = mergeResult({ merged: ['a.ws'], skipped: [], dryRun: false });
    const { connect, calls, closedCount } = fakeConnect([{ result: preview }, { result: final }]);

    await resolveScriptConflicts(api, { connect });

    expect(connect).toHaveBeenCalledTimes(2);
    expect(closedCount()).toBe(2);
    expect(calls[0].exePath).toBe(toolPath);
    expect(calls[1].exePath).toBe(toolPath);
    // The spawn env is the tool's registered WSM_* overrides merged on top of the
    // current process's own environment (wsmEnv.ts's mergeWithProcessEnv) - not the
    // bare override map, which would drop PATH etc. for a raw child_process.spawn.
    expect(calls[0].env?.WSM_GameDirectory).toBe('C:\\Games\\Witcher3');
    expect(calls[1].env?.WSM_GameDirectory).toBe('C:\\Games\\Witcher3');

    expect(showDialogCalls).toHaveLength(2);
    expect(showDialogCalls[0].type).toBe('question');
    // final.skipped is empty, so the result dialog should read as a success, not a
    // "some files still need attention" info dialog.
    expect(showDialogCalls[1].type).toBe('success');
  });

  it('shows an "info" (not "success") result dialog when the real merge still leaves skipped files', async () => {
    const { api, showDialogCalls } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const final = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const { connect } = fakeConnect([{ result: preview }, { result: final }]);

    await resolveScriptConflicts(api, { connect });

    expect(showDialogCalls[1].type).toBe('info');
  });

  it('reports failure via showErrorNotification, without any dialog, when the preview itself fails', async () => {
    const { api, showDialogCalls, errorNotifications } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
    });
    const { connect, closedCount } = fakeConnect([{ error: new Error('spawn failed') }]);

    await resolveScriptConflicts(api, { connect });

    expect(showDialogCalls).toHaveLength(0);
    expect(errorNotifications).toHaveLength(1);
    // The client this connect call produced must still be closed even though
    // mergeConflicts() itself threw - proves the `finally` in
    // runMergeConflictsWorkflow runs on the error path too.
    expect(closedCount()).toBe(1);
  });

  it('reports failure via showErrorNotification when the real merge fails after the user already confirmed', async () => {
    const { api, showDialogCalls, errorNotifications } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const { connect, closedCount } = fakeConnect([{ result: preview }, { error: new Error('merge failed') }]);

    await resolveScriptConflicts(api, { connect });

    // The preview dialog was shown (that part succeeded); only the post-confirm merge
    // failed, so exactly one dialog and one error notification.
    expect(showDialogCalls).toHaveLength(1);
    expect(errorNotifications).toHaveLength(1);
    expect(closedCount()).toBe(2);
  });

  it('reports failure via showErrorNotification instead of throwing when something unexpected fails outside the WSM-client calls themselves (e.g. api.showDialog)', async () => {
    // Regression coverage for a real bug caught in code review: building/showing the
    // preview dialog used to sit outside any try/catch, so an unexpected failure there
    // (originally: buildMergeSummaryDialogContent throwing on a malformed response
    // missing functionLevelDecisions - now fixed at its root in mergePanel.ts's own
    // defensive default) would reject resolveScriptConflicts's own promise, caught only
    // by the last-resort, log-only `.catch()` in registerResolveScriptConflictsAction -
    // the user would see nothing at all. This proves the outer safety net around the
    // whole function body catches that shape of failure too, independent of the
    // specific field that used to trigger it.
    const { api, errorNotifications } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
    });
    (api.showDialog as unknown as ReturnType<typeof vi.fn>).mockImplementationOnce(() => {
      throw new Error('unexpected dialog failure');
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const { connect } = fakeConnect([{ result: preview }]);

    await expect(resolveScriptConflicts(api, { connect })).resolves.toBeUndefined();
    expect(errorNotifications).toHaveLength(1);
  });
});

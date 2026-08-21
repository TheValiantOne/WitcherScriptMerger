import { beforeEach, describe, expect, it, vi } from 'vitest';
import { WITCHER3_GAME_ID } from './gating';
import { WSM_TOOL_ID } from './discoveredTool';
import { GetStatusResult, MergeConflictsResult, WsmMcpClientOptions } from './mcpClient';
import { resolveScriptConflicts, WsmMergeClient } from './resolveAction';

// Unit K: isolates resolveAction.ts's own logic from coexistenceGuard.ts's real
// behavior (its own snapshot/compare logic is covered directly by
// coexistenceGuard.test.ts instead) - same rationale as every other sibling-module mock
// in this codebase (e.g. index.test.ts's own mocks). Without this, the real
// computeMergeStateSnapshot would call the fake client's own getStatus/listMerges below
// and attempt a real recursive fs walk against whatever `status.modsDirectory` those
// stubs return - harmless in practice (an ENOENT-tolerant walk against a nonsense path)
// but untested and not this file's concern.
const { computeMergeStateSnapshotMock, recordOwnMergeStateSnapshotMock, checkCoexistenceDriftMock } = vi.hoisted(() => ({
  computeMergeStateSnapshotMock: vi.fn(),
  recordOwnMergeStateSnapshotMock: vi.fn(),
  checkCoexistenceDriftMock: vi.fn(),
}));

vi.mock('./coexistenceGuard', () => ({
  computeMergeStateSnapshot: computeMergeStateSnapshotMock,
  recordOwnMergeStateSnapshot: recordOwnMergeStateSnapshotMock,
  checkCoexistenceDrift: checkCoexistenceDriftMock,
}));

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

function fakeStatus(overrides: Partial<GetStatusResult> = {}): GetStatusResult {
  return {
    gameDirectory: 'C:\\Games\\Witcher3',
    modsDirectory: 'C:\\Games\\Witcher3\\Mods',
    dependenciesValid: true,
    textMergeDependenciesValid: true,
    bundleDependenciesValid: true,
    modsDirectoryExists: true,
    mergedModName: 'mod0000_MergedFiles',
    conflictCount: 0,
    ...overrides,
  };
}

/** A fake `connect` - returns queued results/errors in call order and records every
 *  `exePath`/`env` it was called with, plus how many of the clients it produced were
 *  closed - proves `resolveScriptConflicts` closes every client it opens, even on the
 *  error path (via `finally`). `getStatus`/`listMerges` are trivial stubs, never
 *  meaningfully exercised here since `./coexistenceGuard` (the only thing that would
 *  call them, via `computeMergeStateSnapshot`) is mocked above - they exist only to
 *  satisfy `WsmMergeClient`'s type. */
function fakeConnect(outcomes: Array<{ result?: MergeConflictsResult; error?: Error }>) {
  const calls: WsmMcpClientOptions[] = [];
  // Every mergeConflicts invocation, so a test can assert what deadline the call was
  // given - see the MERGE_CALL_TIMEOUT_MS tests below.
  const mergeCalls: Array<{ args: unknown; timeoutMs: number | undefined }> = [];
  let closedCount = 0;
  let callIndex = 0;

  const connect = vi.fn(async (options: WsmMcpClientOptions): Promise<WsmMergeClient> => {
    calls.push(options);
    const outcome = outcomes[callIndex++];
    return {
      mergeConflicts: async (args?: unknown, timeoutMs?: number) => {
        mergeCalls.push({ args, timeoutMs });
        if (outcome.error) {
          throw outcome.error;
        }
        return outcome.result!;
      },
      getStatus: async () => fakeStatus(),
      listMerges: async () => [],
      close: async () => {
        closedCount += 1;
      },
    };
  });

  return { connect, calls, mergeCalls, closedCount: () => closedCount };
}

describe('resolveScriptConflicts', () => {
  beforeEach(() => {
    computeMergeStateSnapshotMock.mockReset().mockResolvedValue({
      folderListingSignature: '',
      mergeHistorySignature: '',
      mergedModName: 'mod0000_MergedFiles',
    });
    recordOwnMergeStateSnapshotMock.mockReset();
    checkCoexistenceDriftMock.mockReset();
  });

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

  // Unit K: reconciles coexistenceGuard.ts's own "last known merge state" against this
  // extension's own just-completed workflow, using the same still-open client each time -
  // see resolveAction.ts's own comment in runMergeConflictsWorkflow for why the preview
  // and the real merge deliberately go through *different* coexistenceGuard.ts functions
  // (checkCoexistenceDrift for the no-write preview vs. the silent
  // recordOwnMergeStateSnapshot for the real, writing merge), not the same one for both.
  it('checks for coexistence drift (does not silently re-baseline) after the dry-run preview, and silently re-baselines only after the real merge', async () => {
    const { api } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: ['b.xml'] });
    const final = mergeResult({ merged: ['a.ws'], skipped: [], dryRun: false });
    const { connect } = fakeConnect([{ result: preview }, { result: final }]);
    const previewSnapshot = { folderListingSignature: 'sig1', mergeHistorySignature: 'sig2', mergedModName: 'mod0000_MergedFiles' };
    const finalSnapshot = { folderListingSignature: 'sig3', mergeHistorySignature: 'sig4', mergedModName: 'mod0000_MergedFiles' };
    computeMergeStateSnapshotMock.mockReset().mockResolvedValueOnce(previewSnapshot).mockResolvedValueOnce(finalSnapshot);

    await resolveScriptConflicts(api, { connect });

    // Once per connected client (preview, then the real merge) - each call receives
    // whichever client instance was open at that point, per computeMergeStateSnapshot's
    // own "already-open client" contract.
    expect(computeMergeStateSnapshotMock).toHaveBeenCalledTimes(2);

    // Preview (dryRun: true, no write performed) - compared against the existing
    // baseline, never silently adopted as the new one. A regression here (an earlier
    // version of this code unconditionally called recordOwnMergeStateSnapshot for both
    // calls) would let a preview-then-cancel workflow silently erase evidence of real,
    // undetected drift with no notification ever shown - caught in code review.
    expect(checkCoexistenceDriftMock).toHaveBeenCalledTimes(1);
    expect(checkCoexistenceDriftMock).toHaveBeenCalledWith(api, previewSnapshot);
    expect(recordOwnMergeStateSnapshotMock).not.toHaveBeenCalledWith(previewSnapshot);

    // Real merge (dryRun: false) - this extension's own write, silently adopted as the
    // new known-good baseline, no comparison/notification.
    expect(recordOwnMergeStateSnapshotMock).toHaveBeenCalledTimes(1);
    expect(recordOwnMergeStateSnapshotMock).toHaveBeenCalledWith(finalSnapshot);
    expect(checkCoexistenceDriftMock).not.toHaveBeenCalledWith(api, finalSnapshot);
  });

  it('does not let a coexistence-guard snapshot failure affect the merge result the user sees', async () => {
    const { api, showDialogCalls } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'], skipped: [] });
    const final = mergeResult({ merged: ['a.ws'], skipped: [], dryRun: false });
    const { connect } = fakeConnect([{ result: preview }, { result: final }]);
    computeMergeStateSnapshotMock.mockReset().mockRejectedValue(new Error('snapshot failed'));

    await resolveScriptConflicts(api, { connect });

    expect(showDialogCalls).toHaveLength(2);
    expect(showDialogCalls[1].type).toBe('success');
    expect(recordOwnMergeStateSnapshotMock).not.toHaveBeenCalled();
    expect(checkCoexistenceDriftMock).not.toHaveBeenCalled();
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

  // Regression coverage for a real failure on a 274-mod install: merge_conflicts inherited
  // mcpClient.ts's general-purpose 30s DEFAULT_REQUEST_TIMEOUT_MS, the dry-run preview
  // exceeded it, and resolveScriptConflicts bailed at the preview stage - the real merge
  // never ran, leaving an unmerged mod0000_MergedFiles and a game that wouldn't start.
  // Both calls must carry the long, merge-sized deadline: a preview does the same
  // scan-and-merge computation as the real merge and only skips the writes, so sizing it
  // as if it were cheap is precisely what broke.
  const TEN_MINUTES_MS = 10 * 60 * 1000;

  it('gives the dry-run preview a merge-sized timeout, not the general-purpose request default', async () => {
    const { api } = fakeApi({ toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe' });
    const { connect, mergeCalls } = fakeConnect([{ result: mergeResult() }]);

    await resolveScriptConflicts(api, { connect });

    expect(mergeCalls).toHaveLength(1);
    expect(mergeCalls[0].timeoutMs).toBe(TEN_MINUTES_MS);
    expect((mergeCalls[0].args as { dryRun?: boolean }).dryRun).toBe(true);
  });

  it('gives the real merge the same merge-sized timeout as the preview', async () => {
    const { api } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
      dialogResponses: [{ action: 'Merge Now' }],
    });
    const preview = mergeResult({ merged: ['a.ws'] });
    const final = mergeResult({ merged: ['a.ws'], dryRun: false });
    const { connect, mergeCalls } = fakeConnect([{ result: preview }, { result: final }]);

    await resolveScriptConflicts(api, { connect });

    expect(mergeCalls).toHaveLength(2);
    expect(mergeCalls[0].timeoutMs).toBe(TEN_MINUTES_MS);
    expect(mergeCalls[1].timeoutMs).toBe(TEN_MINUTES_MS);
    expect((mergeCalls[1].args as { dryRun?: boolean }).dryRun).toBe(false);
  });

  // The long deadline belongs to the merge call alone. connect() must NOT be handed it as
  // the client-wide requestTimeoutMs, which also bounds the initialize handshake - a WSM
  // process that fails to start should still fail fast rather than hang for ten minutes.
  it('does not widen the client-wide request timeout (the initialize handshake must still fail fast)', async () => {
    const { api } = fakeApi({ toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe' });
    const { connect, calls } = fakeConnect([{ result: mergeResult() }]);

    await resolveScriptConflicts(api, { connect });

    expect(calls[0].requestTimeoutMs).toBeUndefined();
  });

  it('explains that nothing was merged when the merge call times out, rather than surfacing a bare transport error', async () => {
    const { api, errorNotifications } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
    });
    const timeout = new Error("WSM MCP request 'tools/call' timed out after 600000ms");
    const { connect } = fakeConnect([{ error: timeout }]);

    await resolveScriptConflicts(api, { connect });

    expect(errorNotifications).toHaveLength(1);
    const shown = errorNotifications[0].message;
    expect(shown).toContain('nothing was merged');
    expect(shown).toContain('10 minutes');
    // the original error is still handed over as the detail, not swallowed
    expect(errorNotifications[0].detail).toBe(timeout);
  });

  it('leaves a non-timeout failure message unchanged', async () => {
    const { api, errorNotifications } = fakeApi({
      toolPath: 'C:\\wsm\\WitcherScriptMerger.Headless.exe',
    });
    const { connect } = fakeConnect([{ error: new Error('spawn ENOENT') }]);

    await resolveScriptConflicts(api, { connect });

    expect(errorNotifications).toHaveLength(1);
    expect(errorNotifications[0].message).toBe('Failed to preview script-conflict merges');
    expect(errorNotifications[0].message).not.toContain('nothing was merged');
  });
});

import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Isolates refreshCoexistenceState's own orchestration (connect/compute/check/close) from
// conflictScan.ts's real, filesystem-based isWsmToolAcquired/getWsmExePath - the same
// rationale as index.test.ts's own mock of this module. Every other export tested in this
// file (computeMergeStateSnapshot, checkCoexistenceDrift, the signature helpers) takes
// its inputs as plain arguments and never reaches this module, so mocking it here has no
// effect on those tests.
const { isWsmToolAcquiredMock, getWsmExePathMock } = vi.hoisted(() => ({
  isWsmToolAcquiredMock: vi.fn(),
  getWsmExePathMock: vi.fn(),
}));

vi.mock('./conflictScan', () => ({
  isWsmToolAcquired: isWsmToolAcquiredMock,
  getWsmExePath: getWsmExePathMock,
}));

import { WSM_CONFLICTS_NOTIFICATION_ID } from './conflictNotifications';
import { GetStatusResult, ListMergesResult, WsmMcpClientOptions } from './mcpClient';
import {
  buildFolderListingSignature,
  checkCoexistenceDrift,
  computeMergeHistorySignature,
  computeMergeStateSnapshot,
  MergeStateClient,
  MergeStateSnapshot,
  recordOwnMergeStateSnapshot,
  refreshCoexistenceState,
  resetCoexistenceGuardState,
  WSM_COEXISTENCE_NOTIFICATION_ID,
} from './coexistenceGuard';

function merge(relativePath: string, mergedModName = 'mod0000_MergedFiles', mods: Array<{ name: string; hash: string }> = []): ListMergesResult[number] {
  return { relativePath, mergedModName, mods };
}

function status(overrides: Partial<GetStatusResult> = {}): GetStatusResult {
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

function fakeApi() {
  const notifications: unknown[] = [];
  return {
    getState: () => ({}),
    sendNotification: vi.fn((notification: unknown) => {
      notifications.push(notification);
      return WSM_COEXISTENCE_NOTIFICATION_ID;
    }),
    dismissNotification: vi.fn(),
    showDialog: vi.fn(async () => ({ action: 'Close', input: {} })),
    notifications,
  };
}

describe('computeMergeHistorySignature', () => {
  it('is order-independent (sorted before joining)', () => {
    const a = computeMergeHistorySignature([merge('b.ws'), merge('a.ws')]);
    const b = computeMergeHistorySignature([merge('a.ws'), merge('b.ws')]);
    expect(a).toBe(b);
  });

  it('is empty for no recorded merges', () => {
    expect(computeMergeHistorySignature([])).toBe('');
  });

  it('differs when a recorded merge is added, removed, or its mods/hashes change', () => {
    const base = computeMergeHistorySignature([merge('a.ws', 'mod0000_MergedFiles', [{ name: 'modA', hash: 'h1' }])]);
    const added = computeMergeHistorySignature([
      merge('a.ws', 'mod0000_MergedFiles', [{ name: 'modA', hash: 'h1' }]),
      merge('b.ws'),
    ]);
    const changedHash = computeMergeHistorySignature([merge('a.ws', 'mod0000_MergedFiles', [{ name: 'modA', hash: 'h2' }])]);

    expect(base).not.toBe(added);
    expect(base).not.toBe(changedHash);
  });
});

describe('buildFolderListingSignature', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-coexistence-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('is empty for a folder that does not exist (ENOENT tolerated, not an error)', async () => {
    const signature = await buildFolderListingSignature(path.join(tmpDir, 'does-not-exist'));
    expect(signature).toBe('');
  });

  it('is empty for an existing, empty folder', async () => {
    const signature = await buildFolderListingSignature(tmpDir);
    expect(signature).toBe('');
  });

  it('lists nested files with relative paths, order-independent of directory read order', async () => {
    fs.mkdirSync(path.join(tmpDir, 'sub'));
    fs.writeFileSync(path.join(tmpDir, 'b.ws'), 'content-b');
    fs.writeFileSync(path.join(tmpDir, 'sub', 'a.ws'), 'content-a');

    const signature = await buildFolderListingSignature(tmpDir);

    expect(signature).toContain('b.ws:');
    expect(signature).toContain(path.join('sub', 'a.ws') + ':');
    // Sorted: "b.ws" sorts after "sub/a.ws" is not guaranteed either way by content, but
    // the signature itself must be deterministic regardless of fs.readdir's own order -
    // verified by the "order-independent" test below instead of asserting exact order
    // here.
  });

  it('changes when a file\'s size changes', async () => {
    fs.writeFileSync(path.join(tmpDir, 'a.ws'), 'short');
    const before = await buildFolderListingSignature(tmpDir);

    fs.writeFileSync(path.join(tmpDir, 'a.ws'), 'a much longer piece of content than before');
    const after = await buildFolderListingSignature(tmpDir);

    expect(before).not.toBe(after);
  });

  it('is stable across two reads of the same unchanged content', async () => {
    fs.writeFileSync(path.join(tmpDir, 'a.ws'), 'unchanged');
    fs.writeFileSync(path.join(tmpDir, 'b.ws'), 'also unchanged');

    const first = await buildFolderListingSignature(tmpDir);
    const second = await buildFolderListingSignature(tmpDir);

    expect(first).toBe(second);
  });

  // Regression test for a real gap caught in code review: an earlier version of
  // walkFilesRecursive checked only isFile()/isDirectory(), silently skipping a symlink
  // entry entirely (isSymbolicLink() true, the other two both false) - neither recording
  // nor recursing into it, so a change reachable only via a symlink inside the merged-mod
  // folder would never show up in this signature at all.
  //
  // Symlink creation can fail with EPERM on Windows without Developer Mode or an
  // elevated process (confirmed: this is a real, common CI/dev-machine restriction, not a
  // hypothetical) - this test skips itself gracefully rather than failing the whole suite
  // on a machine where symlinks simply aren't creatable, since that's an environment
  // limitation, not a signal this module's own logic is broken.
  it('records a symlinked file (its own link metadata, not silently skipped)', async () => {
    const targetPath = path.join(tmpDir, 'target.ws');
    const linkPath = path.join(tmpDir, 'link.ws');
    fs.writeFileSync(targetPath, 'target content');

    try {
      fs.symlinkSync(targetPath, linkPath, 'file');
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code === 'EPERM') {
        return; // no symlink privilege on this machine - nothing more this test can check
      }
      throw err;
    }

    const signature = await buildFolderListingSignature(tmpDir);

    expect(signature).toContain('link.ws:');
    expect(signature).toContain('target.ws:');
  });
});

describe('computeMergeStateSnapshot', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-coexistence-snapshot-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function fakeClient(mergedFolder: string, merges: ListMergesResult): MergeStateClient {
    return {
      getStatus: async () => status({ modsDirectory: tmpDir, mergedModName: mergedFolder }),
      listMerges: async () => merges,
    };
  }

  it('joins get_status\'s modsDirectory + mergedModName to locate the folder listing, and reflects list_merges too', async () => {
    fs.mkdirSync(path.join(tmpDir, 'mod0000_MergedFiles'));
    fs.writeFileSync(path.join(tmpDir, 'mod0000_MergedFiles', 'a.ws'), 'merged content');

    const snapshot = await computeMergeStateSnapshot(fakeClient('mod0000_MergedFiles', [merge('a.ws')]));

    expect(snapshot.mergedModName).toBe('mod0000_MergedFiles');
    expect(snapshot.folderListingSignature).toContain('a.ws:');
    expect(snapshot.mergeHistorySignature).toBe(computeMergeHistorySignature([merge('a.ws')]));
  });

  it('does not require the merged-mod folder to already exist (get_status is not gated on modsDirectoryExists per WsmMcpTools.GetStatus)', async () => {
    // No directory created under tmpDir at all - simulates a fresh install where
    // get_status still reports a real modsDirectory/mergedModName (both plain config
    // reads) even though nothing has ever been merged yet.
    const snapshot = await computeMergeStateSnapshot(fakeClient('mod0000_MergedFiles', []));

    expect(snapshot.folderListingSignature).toBe('');
    expect(snapshot.mergeHistorySignature).toBe('');
  });
});

describe('checkCoexistenceDrift', () => {
  beforeEach(() => {
    resetCoexistenceGuardState();
  });

  function snapshot(overrides: Partial<MergeStateSnapshot> = {}): MergeStateSnapshot {
    return {
      folderListingSignature: 'a.ws:10:1000',
      mergeHistorySignature: 'a.ws:mod0000_MergedFiles:modA=hash1',
      mergedModName: 'mod0000_MergedFiles',
      ...overrides,
    };
  }

  it('seeds the baseline on the first observation without notifying - nothing to compare against yet', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());

    expect(api.sendNotification).not.toHaveBeenCalled();
  });

  it('does not notify when the snapshot is unchanged from the baseline', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, snapshot());

    expect(api.sendNotification).not.toHaveBeenCalled();
  });

  it('sends a distinctly-branded, non-conflicts notification when the folder listing changes', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, snapshot({ folderListingSignature: 'a.ws:99:9999' }));

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
    const notification = api.notifications[0] as { id: string; type: string; allowSuppress?: boolean };
    expect(notification.id).toBe(WSM_COEXISTENCE_NOTIFICATION_ID);
    expect(notification.type).toBe('warning');
    // Deliberately not suppressible by default, unlike the ordinary conflicts
    // notification - see coexistenceGuard.ts's own doc comment.
    expect(notification.allowSuppress).not.toBe(true);
  });

  it('dismisses the ordinary conflicts notification on a genuine drift, so a stale count never lingers', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, snapshot({ folderListingSignature: 'changed' }));

    expect(api.dismissNotification).toHaveBeenCalledWith(WSM_CONFLICTS_NOTIFICATION_ID);
  });

  it('does not dismiss anything on the first observation or when nothing changed', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, snapshot());

    expect(api.dismissNotification).not.toHaveBeenCalled();
  });

  it('sends a notification when the merge-history signature changes, even if the folder listing does not', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, snapshot({ mergeHistorySignature: 'a.ws:mod0000_MergedFiles:modA=hash2' }));

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
  });

  it('only notifies once for a single change, not again on every subsequent identical re-check', () => {
    const api = fakeApi();
    const changed = snapshot({ folderListingSignature: 'changed' });

    checkCoexistenceDrift(api as never, snapshot());
    checkCoexistenceDrift(api as never, changed);
    checkCoexistenceDrift(api as never, changed);
    checkCoexistenceDrift(api as never, changed);

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
  });

  it('does not throw when sendNotification itself throws', () => {
    const api = fakeApi();
    api.sendNotification.mockImplementationOnce(() => {
      throw new Error('notification system unavailable');
    });

    checkCoexistenceDrift(api as never, snapshot());
    expect(() => checkCoexistenceDrift(api as never, snapshot({ folderListingSignature: 'changed' }))).not.toThrow();
  });

  // Regression test for the ordering fix mirroring conflictNotifications.ts's own
  // notifyConflictsIfChanged: the baseline must only advance after sendNotification
  // actually succeeds, or a failed attempt would be silently treated as "already
  // reported" for the rest of the session - the exact bug notifyConflictsIfChanged's own
  // doc comment explains avoiding.
  it('retries the notification on the next check when sendNotification failed - does not silently drop a real drift', () => {
    const api = fakeApi();
    checkCoexistenceDrift(api as never, snapshot());

    api.sendNotification.mockImplementationOnce(() => {
      throw new Error('notification system unavailable');
    });
    const changed = snapshot({ folderListingSignature: 'changed' });
    checkCoexistenceDrift(api as never, changed);
    expect(api.sendNotification).toHaveBeenCalledTimes(1); // the failed attempt

    // Same still-changed snapshot re-checked at the next checkpoint - since the failed
    // attempt never advanced the baseline, this must be treated as a fresh, real drift
    // and retried, not silently matched against a baseline that was never actually
    // reported to the user.
    checkCoexistenceDrift(api as never, changed);
    expect(api.sendNotification).toHaveBeenCalledTimes(2);
  });

  it('recordOwnMergeStateSnapshot updates the baseline without notifying, and a later external change is still caught against the new baseline', () => {
    const api = fakeApi();

    checkCoexistenceDrift(api as never, snapshot());
    // This extension's own merge changes state - re-baseline, no notification.
    recordOwnMergeStateSnapshot(snapshot({ folderListingSignature: 'own-merge-result' }));
    expect(api.sendNotification).not.toHaveBeenCalled();

    // A later external change, compared against the *new* (own-merge) baseline, not the
    // original one - still detected.
    checkCoexistenceDrift(api as never, snapshot({ folderListingSignature: 'externally-changed' }));
    expect(api.sendNotification).toHaveBeenCalledTimes(1);
  });
});

describe('refreshCoexistenceState', () => {
  beforeEach(() => {
    resetCoexistenceGuardState();
    isWsmToolAcquiredMock.mockReset();
    getWsmExePathMock.mockReset().mockReturnValue('C:\\wsm\\WitcherScriptMerger.Headless.exe');
  });

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

  function fakeConnectApi() {
    return { getState: () => ({}), sendNotification: vi.fn(), dismissNotification: vi.fn(), showDialog: vi.fn() };
  }

  it('does nothing - never connects - when no WSM tool has been acquired', async () => {
    isWsmToolAcquiredMock.mockResolvedValue(false);
    const connect = vi.fn();

    await refreshCoexistenceState(fakeConnectApi() as never, { connect });

    expect(connect).not.toHaveBeenCalled();
  });

  it('connects with a bounded per-request timeout at the exe path conflictScan.ts resolves, computes a snapshot, and closes the client', async () => {
    isWsmToolAcquiredMock.mockResolvedValue(true);
    const closeSpy = vi.fn(async () => undefined);
    const fakeClient = {
      getStatus: vi.fn(async () => fakeStatus()),
      listMerges: vi.fn(async () => []),
      close: closeSpy,
    };
    const connect = vi.fn(async (_options: WsmMcpClientOptions) => fakeClient);

    await refreshCoexistenceState(fakeConnectApi() as never, { connect: connect as never });

    expect(connect).toHaveBeenCalledTimes(1);
    const options = connect.mock.calls[0][0] as WsmMcpClientOptions;
    expect(options.exePath).toBe('C:\\wsm\\WitcherScriptMerger.Headless.exe');
    // Bounded, not mcpClient.ts's own 30s default - this call sits inside Vortex's
    // did-deploy emitAndAwait window on one of its trigger points (index.ts's
    // checkForConflictsAfterDeploy) - see coexistenceGuard.ts's own
    // COEXISTENCE_CHECK_TIMEOUT_MS doc comment.
    expect(options.requestTimeoutMs).toBe(15_000);
    expect(fakeClient.getStatus).toHaveBeenCalledTimes(1);
    expect(fakeClient.listMerges).toHaveBeenCalledTimes(1);
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('resolves (never rejects/throws) when connect itself rejects', async () => {
    isWsmToolAcquiredMock.mockResolvedValue(true);
    const connect = vi.fn(async () => {
      throw new Error('spawn failed');
    });

    await expect(refreshCoexistenceState(fakeConnectApi() as never, { connect: connect as never })).resolves.toBeUndefined();
  });

  it('still closes the client, and still resolves without throwing, when getStatus itself rejects', async () => {
    isWsmToolAcquiredMock.mockResolvedValue(true);
    const closeSpy = vi.fn(async () => undefined);
    const fakeClient = {
      getStatus: vi.fn(async () => {
        throw new Error('get_status failed');
      }),
      listMerges: vi.fn(async () => []),
      close: closeSpy,
    };
    const connect = vi.fn(async () => fakeClient);

    await expect(refreshCoexistenceState(fakeConnectApi() as never, { connect: connect as never })).resolves.toBeUndefined();
    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('sends the distinct coexistence notification end-to-end when two real checks observe a genuine change', async () => {
    isWsmToolAcquiredMock.mockResolvedValue(true);
    let listMergesCallCount = 0;
    const connect = vi.fn(async () => ({
      getStatus: vi.fn(async () => fakeStatus()),
      listMerges: vi.fn(async () => {
        listMergesCallCount += 1;
        // First call (seeds the baseline): no recorded merges. Second call: one -
        // a real, observable change in mergeHistorySignature between the two checks.
        return listMergesCallCount === 1 ? [] : [{ relativePath: 'a.ws', mergedModName: 'mod0000_MergedFiles', mods: [] }];
      }),
      close: vi.fn(async () => undefined),
    }));
    const api = fakeConnectApi();

    await refreshCoexistenceState(api as never, { connect: connect as never });
    expect(api.sendNotification).not.toHaveBeenCalled(); // first observation only seeds the baseline

    await refreshCoexistenceState(api as never, { connect: connect as never });
    expect(api.sendNotification).toHaveBeenCalledTimes(1);
    expect((api.sendNotification.mock.calls[0][0] as { id: string }).id).toBe(WSM_COEXISTENCE_NOTIFICATION_ID);
  });
});

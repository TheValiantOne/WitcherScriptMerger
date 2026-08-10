import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ScanConflictsResult } from './mcpClient';
import { computeConflictSignature, notifyConflictsIfChanged, resetConflictNotificationState, WSM_CONFLICTS_NOTIFICATION_ID } from './conflictNotifications';

function conflict(relativePath: string, alreadyResolved = false): ScanConflictsResult[number] {
  return {
    relativePath,
    category: 'Script',
    mods: [],
    defaultOrder: [],
    alreadyResolved,
  };
}

// `activity` intentionally accepts either shape: `string[]` is the real, confirmed
// runtime shape (Vortex's `session` reducer's `startActivity`/`stopActivity` push/filter
// a plain array per group - see conflictNotifications.ts's own `activityEntries` doc
// comment for the exact citation), while `@nexusmods/vortex-api`'s published `lib/api.d.ts`
// types it as a single `string` - stale, but tolerated too so this doesn't break again if
// a future SDK version actually matches its own types.
function fakeApi(activity: Record<string, string | string[]> = {}) {
  return {
    getState: () => ({ session: { base: { activity } } }),
    sendNotification: vi.fn().mockReturnValue(WSM_CONFLICTS_NOTIFICATION_ID),
    dismissNotification: vi.fn(),
  };
}

describe('computeConflictSignature', () => {
  it('is order-independent (sorted before joining)', () => {
    expect(computeConflictSignature([conflict('b.ws'), conflict('a.ws')])).toBe(
      computeConflictSignature([conflict('a.ws'), conflict('b.ws')]),
    );
  });

  it('is empty for no conflicts', () => {
    expect(computeConflictSignature([])).toBe('');
  });

  it('differs when the conflict set differs', () => {
    expect(computeConflictSignature([conflict('a.ws')])).not.toBe(computeConflictSignature([conflict('a.ws'), conflict('b.ws')]));
  });
});

describe('notifyConflictsIfChanged', () => {
  beforeEach(() => {
    resetConflictNotificationState();
  });

  it('sends a notification with the documented shape when unresolved conflicts are found', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
    const notification = api.sendNotification.mock.calls[0][0];
    expect(notification.id).toBe(WSM_CONFLICTS_NOTIFICATION_ID);
    expect(notification.allowSuppress).toBe(true);
    expect(notification.actions).toEqual([]);
    expect(typeof notification.type).toBe('string');
    expect(typeof notification.message).toBe('string');
  });

  it('uses a notification id distinct from the built-in game-witcher3 extension\'s "witcher3-merge"', () => {
    expect(WSM_CONFLICTS_NOTIFICATION_ID).not.toBe('witcher3-merge');
  });

  it('does not re-notify on a second call with the identical conflict set (same signature)', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, [conflict('a.ws')]);
    notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
  });

  it('notifies again when the conflict set actually changes', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, [conflict('a.ws')]);
    notifyConflictsIfChanged(api as never, [conflict('a.ws'), conflict('b.ws')]);

    expect(api.sendNotification).toHaveBeenCalledTimes(2);
  });

  it('excludes already-resolved conflicts from both the count and the change signature', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, [conflict('a.ws'), conflict('resolved.ws', true)]);
    // Only the already-resolved one changes (a different resolved file) - unresolved set
    // ('a.ws') is identical, so this must not re-notify.
    notifyConflictsIfChanged(api as never, [conflict('a.ws'), conflict('other-resolved.ws', true)]);

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
  });

  it('dismisses any existing notification once the unresolved set becomes empty', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, [conflict('a.ws')]);
    notifyConflictsIfChanged(api as never, [conflict('a.ws', true)]);

    expect(api.sendNotification).toHaveBeenCalledTimes(1);
    expect(api.dismissNotification).toHaveBeenCalledWith(WSM_CONFLICTS_NOTIFICATION_ID);
  });

  // Regression test: lastNotifiedSignature now starts at '' (computeConflictSignature's
  // own value for "no conflicts"), not undefined, specifically so this first-ever check
  // matches the same-signature early-return and never calls dismissNotification for a
  // notification that was never sent.
  it('does not call dismissNotification on the very first check of a session when there are no conflicts at all', () => {
    const api = fakeApi();

    notifyConflictsIfChanged(api as never, []);

    expect(api.dismissNotification).not.toHaveBeenCalled();
    expect(api.sendNotification).not.toHaveBeenCalled();
  });

  describe('failure handling (send/dismiss must not corrupt suppression state)', () => {
    it('does not throw, and does not record the signature as "shown", when sendNotification throws', () => {
      const api = fakeApi();
      api.sendNotification.mockImplementation(() => {
        throw new Error('store dispatch failed');
      });

      expect(() => notifyConflictsIfChanged(api as never, [conflict('a.ws')])).not.toThrow();

      // The user never actually saw a notification - a later check with the identical
      // conflict set must retry, not silently treat it as already shown.
      const retryApi = fakeApi();
      notifyConflictsIfChanged(retryApi as never, [conflict('a.ws')]);
      expect(retryApi.sendNotification).toHaveBeenCalledTimes(1);
    });

    it('retries dismissNotification on a later call after a prior attempt failed, rather than silently treating the failure as success', () => {
      const api = fakeApi();
      // First, genuinely show a notification so the next calls take the "unresolved
      // set became empty" -> dismissNotification path.
      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);
      expect(api.sendNotification).toHaveBeenCalledTimes(1);

      api.dismissNotification.mockImplementationOnce(() => {
        throw new Error('store dispatch failed');
      });
      expect(() => notifyConflictsIfChanged(api as never, [conflict('a.ws', true)])).not.toThrow();
      expect(api.dismissNotification).toHaveBeenCalledTimes(1);

      // Same (still-empty) unresolved set again. If the failed attempt above had
      // wrongly committed '' as the new "last known" signature (the pre-fix ordering
      // bug), this call would see signature === lastNotifiedSignature and skip
      // retrying entirely - the dismiss would never actually happen and a stale
      // notification would linger forever. The fix leaves the prior successful ('a.ws')
      // signature in place on failure, so this must retry.
      notifyConflictsIfChanged(api as never, [conflict('a.ws', true)]);
      expect(api.dismissNotification).toHaveBeenCalledTimes(2);
    });
  });

  describe('activity-in-progress suppression (real Vortex shape: string[] per group)', () => {
    it('skips entirely (no state mutation) while a dependency install is in progress', () => {
      const api = fakeApi({ installing_dependencies: ['some-mod-id'] });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);
      expect(api.sendNotification).not.toHaveBeenCalled();

      // Once the install activity clears, the very first real check must still notify -
      // proof that the skip above didn't record 'a.ws' as already-seen.
      const apiAfter = fakeApi({});
      notifyConflictsIfChanged(apiAfter as never, [conflict('a.ws')]);
      expect(apiAfter.sendNotification).toHaveBeenCalledTimes(1);
    });

    // Regression test for the exact trap `activityEntries` exists to avoid: Vortex's own
    // `stopActivity` reducer never deletes the group key, it filters the id out and
    // leaves an empty array behind (`session.ts`: `[group]: group.filter(id => id !==
    // activityId)`). A naive `Boolean(activity.installing_dependencies)` truthiness
    // check would see `[]` (truthy in JS) and report "still active" forever after the
    // first dependency install ever completed.
    it('does NOT treat a stale, now-empty activity array (left behind by a completed install) as still active', () => {
      const api = fakeApi({ installing_dependencies: [] });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).toHaveBeenCalledTimes(1);
    });

    it('skips while a plain mod install is in progress (activity.mods includes "installing")', () => {
      const api = fakeApi({ mods: ['installing'] });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).not.toHaveBeenCalled();
    });

    it('does NOT skip merely because activity.mods contains "deployment" - the own did-deploy handler runs during that window', () => {
      const api = fakeApi({ mods: ['deployment'] });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).toHaveBeenCalledTimes(1);
    });

    it('does not skip merely because some unrelated activity group is non-empty', () => {
      const api = fakeApi({ discovery: ['scanning'] });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).toHaveBeenCalledTimes(1);
    });
  });

  describe('activity shape tolerance (documented-but-stale @nexusmods/vortex-api shape: single string)', () => {
    it('still skips if a future/different Vortex build reports a plain string instead of an array', () => {
      const api = fakeApi({ installing_dependencies: 'some-mod-id' });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).not.toHaveBeenCalled();
    });

    it('treats an empty string the same as "not active"', () => {
      const api = fakeApi({ installing_dependencies: '' });

      notifyConflictsIfChanged(api as never, [conflict('a.ws')]);

      expect(api.sendNotification).toHaveBeenCalledTimes(1);
    });
  });
});

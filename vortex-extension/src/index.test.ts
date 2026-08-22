import { beforeEach, describe, expect, it, vi } from 'vitest';

// `vi.mock` factories are hoisted above imports, so anything they reference has to come
// from `vi.hoisted` rather than an ordinary outer-scope `const` - this isolates index.ts's
// own wiring (what this file actually tests) from toolAcquisition.ts/statusTile.ts/
// conflictScan.ts/conflictNotifications.ts's own real behavior, each already thoroughly
// covered by their own *.test.ts files.
const {
  ensureWsmToolRegisteredMock,
  registerWsmStatusDashletMock,
  isWsmToolAcquiredMock,
  scanWsmConflictsMock,
  notifyConflictsIfChangedMock,
  isModOrDependencyInstallActiveMock,
  refreshCoexistenceStateMock,
  resolveScriptConflictsMock,
} = vi.hoisted(() => ({
  ensureWsmToolRegisteredMock: vi.fn(),
  registerWsmStatusDashletMock: vi.fn(),
  isWsmToolAcquiredMock: vi.fn(),
  scanWsmConflictsMock: vi.fn(),
  notifyConflictsIfChangedMock: vi.fn(),
  isModOrDependencyInstallActiveMock: vi.fn(),
  refreshCoexistenceStateMock: vi.fn(),
  resolveScriptConflictsMock: vi.fn(),
}));

vi.mock('./toolAcquisition', () => ({
  ensureWsmToolRegistered: ensureWsmToolRegisteredMock,
}));

// Mocked for the same reason as './toolAcquisition' above - isolates index.ts's own
// wiring from statusTile.ts's real behavior (its own registerDashlet call shape is
// covered directly by statusTile.test.ts instead).
vi.mock('./statusTile', () => ({
  registerWsmStatusDashlet: registerWsmStatusDashletMock,
}));

// Partial mock, deliberately: index.ts now also imports resolveScriptConflicts, to hand
// notifyConflictsIfChanged the callback behind the notification's "Resolve Now" button.
// Only that one export is stubbed - registerResolveScriptConflictsAction stays real,
// because a test below asserts it reaches context.registerAction with the right shape.
vi.mock('./resolveAction', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./resolveAction')>()),
  resolveScriptConflicts: resolveScriptConflictsMock,
}));

vi.mock('./conflictScan', () => ({
  isWsmToolAcquired: isWsmToolAcquiredMock,
  scanWsmConflicts: scanWsmConflictsMock,
}));

vi.mock('./conflictNotifications', () => ({
  notifyConflictsIfChanged: notifyConflictsIfChangedMock,
  isModOrDependencyInstallActive: isModOrDependencyInstallActiveMock,
}));

// Unit K: isolates index.ts's own wiring from coexistenceGuard.ts's real behavior (its
// own snapshot/compare/notify logic is covered directly by coexistenceGuard.test.ts
// instead) - matches every mock above's own rationale. Without this, refreshCoexistenceState
// would run for real against the *mocked* './conflictScan' module above (which doesn't
// export getWsmExePath), silently failing inside its own try/catch on every did-deploy
// test where isWsmToolAcquiredMock resolves true - technically harmless (it never throws
// out to the caller) but untested and liable to mask a real regression.
vi.mock('./coexistenceGuard', () => ({
  refreshCoexistenceState: refreshCoexistenceStateMock,
}));

import main from './index';
import { WITCHER3_GAME_ID } from './gating';

/** A minimal stand-in for IExtensionContext - just enough surface for index.ts's own
 *  logic (context.once, context.api.getState/events.on/onAsync, context.registerAction/
 *  registerDashlet - the register calls added alongside resolveAction.ts's and
 *  mergeHistoryDashlet.ts's own registrations, both called directly at `main()` time per
 *  IExtensionContext.once's own doc comment - see index.ts's own updated header comment
 *  for why that's NOT deferred through context.once), matching gating.test.ts's own
 *  fakeApi philosophy: a simplified fake, not a replica of Vortex's real context shape.
 *
 *  `registerDashlet` needs a real (no-op) implementation, not just a type - without this,
 *  every test below would throw "context.registerDashlet is not a function" the moment
 *  main(context) runs; mergeHistoryDashlet.test.ts covers registerMergeHistoryDashlet's
 *  own argument-shape/gating behavior directly.
 *
 *  `profiles` backs `selectors.profileById` (via the shared `vortexApiStub.ts`) -
 *  `checkForConflictsAfterDeploy` (index.ts) resolves `did-deploy`'s own `profileId`
 *  argument to that profile's `gameId`, deliberately *not* `activeGameId` (see index.ts's
 *  own comment on why those differ) - so tests exercising that handler set up a profile
 *  entry rather than (only) `setActiveGame`. `activeGameId`/`setActiveGame` remain for the
 *  unrelated `tryRegisterWsmTool`/`gamemode-activated` tests, which genuinely do gate on
 *  "what's active right now". */
function fakeContext(initialActiveGameId: string | undefined, profiles: Record<string, { gameId: string }> = {}) {
  const state = { activeGameId: initialActiveGameId, profiles };
  let onceCallback: (() => void) | undefined;
  // Args-capable (not just `() => void`) since Unit K's 'profile-did-change' listener
  // takes a `profileId: string` argument - `did-deploy`'s own async equivalent already
  // needed args support (see `fireAsyncEvent` below), this just extends the same
  // capability to plain `events.on` listeners.
  const eventListeners = new Map<string, Array<(...args: unknown[]) => void>>();
  const asyncListeners = new Map<string, (...args: unknown[]) => Promise<unknown>>();
  const registerActionMock = vi.fn();

  const context = {
    once: (callback: () => void) => {
      onceCallback = callback;
    },
    registerAction: registerActionMock,
    registerDashlet: (..._args: unknown[]) => {
      // Intentionally a no-op in tests - mergeHistoryDashlet.test.ts covers
      // registerMergeHistoryDashlet's own argument-shape/gating behavior directly.
    },
    api: {
      getState: () => state,
      events: {
        on: (eventName: string, listener: (...args: unknown[]) => void) => {
          const listeners = eventListeners.get(eventName) ?? [];
          listeners.push(listener);
          eventListeners.set(eventName, listeners);
        },
      },
      onAsync: (eventName: string, listener: (...args: unknown[]) => Promise<unknown>) => {
        asyncListeners.set(eventName, listener);
      },
    },
  };

  return {
    context: context as unknown as Parameters<typeof main>[0],
    fireOnce: () => onceCallback?.(),
    fireEvent: (eventName: string, ...args: unknown[]) => eventListeners.get(eventName)?.forEach((listener) => listener(...args)),
    fireAsyncEvent: (eventName: string, ...args: unknown[]) => asyncListeners.get(eventName)?.(...args),
    setActiveGame: (gameId: string | undefined) => {
      state.activeGameId = gameId;
    },
    registerActionMock,
  };
}

describe('main (index.ts)', () => {
  // Unit K: refreshCoexistenceState is now called unconditionally, from THREE different
  // call sites, whenever witcher3 is the relevant game (tryRegisterWsmTool - both at
  // fireOnce() time and on every 'gamemode-activated' - plus checkForConflictsAfterDeploy
  // and checkCoexistenceOnProfileChange). A safe resolved-value default here (rather than
  // requiring every single test that happens to exercise a witcher3-active path to
  // remember to configure it) avoids a whole class of "Cannot read properties of
  // undefined (reading 'catch')" crashes an unconfigured vi.fn() would otherwise cause the
  // moment this file's own .catch(...) call sites run against it - caught in code review
  // when the gamemode-activated wiring was added and several previously-passing tests
  // started crashing. Individual tests below still override this via their own
  // .mockClear()/.mockReset() calls where they need to assert something more specific
  // (an exact call count, a rejection, etc.) - this is only the baseline every other test
  // can rely on without thinking about it.
  beforeEach(() => {
    refreshCoexistenceStateMock.mockReset().mockResolvedValue(undefined);
  });

  it('returns true (Vortex extension init contract)', () => {
    const { context } = fakeContext(undefined);
    expect(main(context)).toBe(true);
  });

  it('calls ensureWsmToolRegistered when witcher3 is already active at context.once time', () => {
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    const { context, fireOnce } = fakeContext(WITCHER3_GAME_ID);

    main(context);
    fireOnce();

    expect(ensureWsmToolRegisteredMock).toHaveBeenCalledTimes(1);
  });

  it('does not call ensureWsmToolRegistered when witcher3 is not active at context.once time', () => {
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    const { context, fireOnce } = fakeContext('skyrimse');

    main(context);
    fireOnce();

    expect(ensureWsmToolRegisteredMock).not.toHaveBeenCalled();
  });

  it('re-checks on a live "gamemode-activated" switch into witcher3, without requiring a restart', () => {
    // This is the fix for a real gap: previously, isWitcher3Active was only checked
    // once inside context.once, so a user switching into Witcher 3 mid-session (no
    // Vortex restart) would never trigger registration for the rest of that session.
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    const { context, fireOnce, fireEvent, setActiveGame } = fakeContext('skyrimse');

    main(context);
    fireOnce();
    expect(ensureWsmToolRegisteredMock).not.toHaveBeenCalled();

    setActiveGame(WITCHER3_GAME_ID);
    fireEvent('gamemode-activated');

    expect(ensureWsmToolRegisteredMock).toHaveBeenCalledTimes(1);
  });

  // Unit K: this test exists specifically because a prior version of this file's own
  // header comment (and coexistenceGuard.ts's own) documented 'gamemode-activated' as a
  // wired coexistence-check trigger point without tryRegisterWsmTool actually calling
  // refreshCoexistenceState at all - caught in code review, not by any test, since
  // nothing had asserted on this call site before. This test is what would have caught
  // that regression, and is what should catch it again if this wiring is ever dropped.
  it('calls refreshCoexistenceState (the third trigger point) whenever tryRegisterWsmTool runs with witcher3 active - both at context.once time and on a live gamemode-activated switch', () => {
    const { context, fireOnce, fireEvent, setActiveGame } = fakeContext(WITCHER3_GAME_ID);

    main(context);
    fireOnce();
    expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(1);
    expect(refreshCoexistenceStateMock).toHaveBeenCalledWith(context.api);

    setActiveGame('skyrimse');
    fireEvent('gamemode-activated');
    expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(1); // still not witcher3 - no new call

    setActiveGame(WITCHER3_GAME_ID);
    fireEvent('gamemode-activated');
    expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(2);
  });

  it('does not throw when refreshCoexistenceState rejects from tryRegisterWsmTool', () => {
    refreshCoexistenceStateMock.mockReset().mockRejectedValue(new Error('coexistence check failed'));
    const { context, fireOnce } = fakeContext(WITCHER3_GAME_ID);

    main(context);
    expect(() => fireOnce()).not.toThrow();
  });

  it('does nothing on "gamemode-activated" when the newly-active game still is not witcher3', () => {
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    const { context, fireOnce, fireEvent, setActiveGame } = fakeContext('skyrimse');

    main(context);
    fireOnce();

    setActiveGame('fallout4');
    fireEvent('gamemode-activated');

    expect(ensureWsmToolRegisteredMock).not.toHaveBeenCalled();
  });

  it('does not throw when ensureWsmToolRegistered rejects', async () => {
    ensureWsmToolRegisteredMock.mockClear().mockRejectedValue(new Error('boom'));
    const { context, fireOnce } = fakeContext(WITCHER3_GAME_ID);

    main(context);
    expect(() => fireOnce()).not.toThrow();

    // Let the rejected promise's .catch() handler actually run before the test ends.
    await new Promise((resolve) => setTimeout(resolve, 0));
  });

  it('registers the WSM status dashlet synchronously inside main() itself, not deferred into context.once', () => {
    // Per @nexusmods/vortex-api's own lib/api.d.ts doc comment on IExtensionContext:
    // register functions "must be called immediately inside the init function," and
    // once is documented as being for extension setup "except for the register calls."
    // An earlier version of this test (and of index.ts itself) got this backwards -
    // asserting registerWsmStatusDashlet only fired after fireOnce() - which would have
    // meant the dashlet's registerDashlet call never actually took effect against a
    // real Vortex host. This test now asserts the call happens from main(context)
    // alone, with context.once never fired at all.
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    registerWsmStatusDashletMock.mockClear();
    const { context } = fakeContext('skyrimse');

    main(context);

    expect(registerWsmStatusDashletMock).toHaveBeenCalledTimes(1);
    expect(registerWsmStatusDashletMock).toHaveBeenCalledWith(context);
    // Still isn't gated on isWitcher3Active - statusTile.ts's own registerDashlet call
    // supplies a live isVisible callback instead (covered by statusTile.test.ts).
    expect(ensureWsmToolRegisteredMock).not.toHaveBeenCalled();
  });

  it('registers the "Resolve Script Conflicts" action directly (not deferred through context.once), gated live on witcher3 being active', () => {
    const { context, registerActionMock, setActiveGame } = fakeContext('skyrimse');

    main(context);

    // Registered synchronously by main() itself - IExtensionContext.once's own doc
    // comment says registrations are expected to have already happened by the time
    // `once` fires, so this must not require fireOnce() to have been called at all.
    expect(registerActionMock).toHaveBeenCalledTimes(1);
    const [group, , , , title, , condition] = registerActionMock.mock.calls[0] as [
      string,
      number,
      string,
      unknown,
      string,
      unknown,
      () => boolean,
    ];
    expect(group).toBe('mod-icons');
    expect(title).toBe('Resolve Script Conflicts');

    // The condition callback is live, re-evaluated against current state each time
    // Vortex calls it - not baked in once at registration time.
    expect(condition()).toBe(false);
    setActiveGame(WITCHER3_GAME_ID);
    expect(condition()).toBe(true);
  });

  describe('did-deploy conflict scanning', () => {
    it('registers a did-deploy handler via onAsync (not events.on) at context.once time', () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      const { context, fireOnce, fireAsyncEvent } = fakeContext(undefined);

      main(context);
      fireOnce();

      // No handler registered for a plain events.on('did-deploy', ...) - only onAsync.
      expect(fireAsyncEvent('did-deploy', 'profile1', undefined)).toBeInstanceOf(Promise);
    });

    it("does nothing when the deployed profile's own game is not witcher3, even if witcher3 happens to be active", async () => {
      // Deliberately the inverse of what a naive isWitcher3Active(context.api) check
      // would give: 'skyrimse' profile deployed while witcher3 is still the currently
      // active game. The gate must follow the deployed profile, not "what's active".
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear();
      scanWsmConflictsMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: 'skyrimse' },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(isWsmToolAcquiredMock).not.toHaveBeenCalled();
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
    });

    it('does nothing when the profileId is unknown (no matching profile at all)', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear();
      scanWsmConflictsMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {});

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'unknown-profile', undefined);

      expect(isWsmToolAcquiredMock).not.toHaveBeenCalled();
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
    });

    it("scans a witcher3 deployment even if a different game has since become active (fixes the isWitcher3Active-at-handler-time race)", async () => {
      // The scenario the profileId-based gate specifically exists to handle: the
      // deployed profile really was witcher3, but by the time this async handler runs,
      // the user already switched the *active* game elsewhere. A naive
      // isWitcher3Active(context.api) check would wrongly skip this.
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      const conflicts = [{ relativePath: 'a.ws' }];
      scanWsmConflictsMock.mockClear().mockResolvedValue(conflicts);
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent, setActiveGame } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      setActiveGame('skyrimse');
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(scanWsmConflictsMock).toHaveBeenCalledTimes(1);
      expect(notifyConflictsIfChangedMock).toHaveBeenCalledWith(context.api, conflicts, expect.any(Function));
    });

    it('skips scanning (without throwing) when no WSM tool has been acquired yet', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(false);
      refreshCoexistenceStateMock.mockClear();
      scanWsmConflictsMock.mockClear();
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(isWsmToolAcquiredMock).toHaveBeenCalledTimes(1);
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
      expect(notifyConflictsIfChangedMock).not.toHaveBeenCalled();
    });

    it('scans and notifies for a witcher3 deployment', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      const conflicts = [{ relativePath: 'a.ws' }];
      scanWsmConflictsMock.mockClear().mockResolvedValue(conflicts);
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(scanWsmConflictsMock).toHaveBeenCalledTimes(1);
      expect(notifyConflictsIfChangedMock).toHaveBeenCalledWith(context.api, conflicts, expect.any(Function));
    });

    // The whole point of the third argument: the notification's "Resolve Now" button has
    // to actually run the merge workflow, not just exist. Regression coverage for the
    // passive version, which shipped `actions: []` and left the user to go find a
    // different button - on a real install it fired seconds before a game that wouldn't
    // start, and led nowhere.
    it('hands notifyConflictsIfChanged a callback that runs the resolve-conflicts workflow', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      scanWsmConflictsMock.mockClear().mockResolvedValue([{ relativePath: 'a.ws' }]);
      notifyConflictsIfChangedMock.mockClear();
      resolveScriptConflictsMock.mockReset().mockResolvedValue(undefined);
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      const onResolveNow = notifyConflictsIfChangedMock.mock.calls[0][2] as () => void;
      expect(resolveScriptConflictsMock).not.toHaveBeenCalled();

      onResolveNow();

      expect(resolveScriptConflictsMock).toHaveBeenCalledWith(context.api);
    });

    // The callback must not let a rejected workflow escape as an unhandled rejection -
    // resolveScriptConflicts reports its own failures to the user, so this is a log-only
    // last-resort net, matching registerResolveScriptConflictsAction's own.
    it('swallows a rejection from the Resolve Now callback rather than letting it escape', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      scanWsmConflictsMock.mockClear().mockResolvedValue([{ relativePath: 'a.ws' }]);
      notifyConflictsIfChangedMock.mockClear();
      resolveScriptConflictsMock.mockReset().mockRejectedValue(new Error('boom'));
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      const onResolveNow = notifyConflictsIfChangedMock.mock.calls[0][2] as () => void;

      expect(() => onResolveNow()).not.toThrow();
      await Promise.resolve();
    });

    // Fix for a real wasted-work case: notifyConflictsIfChanged would discard this
    // scan's result anyway (it checks the same condition), so checking before ever
    // spawning a WSM process avoids paying for a process spawn whose result can never
    // be shown - concretely relevant during a dependency-install burst (e.g. installing
    // a Collection triggers several deploy-per-mod cycles in a row).
    it('does not spawn a scan at all while a mod/dependency install is in progress', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(true);
      scanWsmConflictsMock.mockClear();
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
      expect(notifyConflictsIfChangedMock).not.toHaveBeenCalled();
    });

    // Unit K's own headline regression: refreshCoexistenceState must run even while a
    // mod/dependency install is in progress (e.g. installing a Collection), because
    // that's precisely the window in which game-witcher3's own importScriptMerges() can
    // overwrite this extension's merge output (coexistenceGuard.ts's own doc comment,
    // hazard 1). A coexistence check gated behind the same isModOrDependencyInstallActive
    // early-return that (correctly) skips *conflict scanning* here would never see the
    // one did-deploy where that overwrite actually happened.
    it('still calls refreshCoexistenceState even while a mod/dependency install is in progress', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(true);
      scanWsmConflictsMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      // fireOnce() itself already triggered one refreshCoexistenceState call via
      // tryRegisterWsmTool (witcher3 is this test's own initial active game) - cleared
      // here so the assertion below isolates did-deploy's own, separate call to it,
      // which is what this test actually exists to prove.
      refreshCoexistenceStateMock.mockClear();
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(1);
      expect(refreshCoexistenceStateMock).toHaveBeenCalledWith(context.api);
      // Still correctly skips the unrelated conflict scan itself.
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
    });

    it('calls refreshCoexistenceState for an ordinary witcher3 deployment too', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      scanWsmConflictsMock.mockClear().mockResolvedValue([]);
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      refreshCoexistenceStateMock.mockClear(); // see comment in the test just above
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(1);
    });

    it('does not call refreshCoexistenceState when no WSM tool has been acquired yet', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(false);
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      refreshCoexistenceStateMock.mockClear(); // see comment two tests above
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(refreshCoexistenceStateMock).not.toHaveBeenCalled();
    });

    it('does not call refreshCoexistenceState for a non-witcher3 deployment', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: 'skyrimse' },
      });

      main(context);
      fireOnce();
      refreshCoexistenceStateMock.mockClear(); // see comment three tests above
      await fireAsyncEvent('did-deploy', 'profile1', undefined);

      expect(refreshCoexistenceStateMock).not.toHaveBeenCalled();
    });

    // Corrected in code review: an earlier version of this test's own title claimed the
    // conflict scan still runs when refreshCoexistenceState rejects, while its own
    // neighboring comment said the opposite - and neither was actually verified by an
    // assertion, so the mismatch went unnoticed. The real, correct behavior (per
    // checkForConflictsAfterDeploy's single shared try/catch wrapping the whole handler
    // body) is that a refreshCoexistenceState rejection aborts the rest of *this specific*
    // handler invocation, matching the existing, established precedent for every other
    // unexpected throw in this same handler (see the isWsmToolAcquired-throws-EBUSY test
    // below) - not a special case for this one call. refreshCoexistenceState is
    // documented (coexistenceGuard.ts's own doc comment) to never actually reject in
    // production; this test exists to prove the handler stays robust (never rejects
    // onAsync's own promise) even if that contract were ever violated, not to claim the
    // scan proceeds regardless.
    it('resolves (never rejects) when refreshCoexistenceState itself rejects, and correctly skips the rest of this handler invocation (matching every other unexpected-throw case in this same handler)', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockRejectedValue(new Error('coexistence check failed'));
      const conflicts = [{ relativePath: 'a.ws' }];
      scanWsmConflictsMock.mockClear().mockResolvedValue(conflicts);
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();

      await expect(fireAsyncEvent('did-deploy', 'profile1', undefined)).resolves.toBeUndefined();
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
      expect(notifyConflictsIfChangedMock).not.toHaveBeenCalled();
    });

    it('resolves (never rejects) when scanWsmConflicts throws - onAsync listeners must report their own errors', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockResolvedValue(true);
      isModOrDependencyInstallActiveMock.mockClear().mockReturnValue(false);
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      scanWsmConflictsMock.mockClear().mockRejectedValue(new Error('spawn failed'));
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();

      await expect(fireAsyncEvent('did-deploy', 'profile1', undefined)).resolves.toBeUndefined();
      expect(notifyConflictsIfChangedMock).not.toHaveBeenCalled();
    });

    // Regression test: isWsmToolAcquired is documented (conflictScan.ts) to propagate
    // non-ENOENT filesystem errors rather than silently returning false. That call must
    // stay inside checkForConflictsAfterDeploy's own try/catch, or a real EBUSY/EPERM
    // (an antivirus scan, a concurrently running WSM process) would reject this
    // onAsync('did-deploy', ...) handler's promise straight into Vortex's own
    // emitAndAwait('did-deploy', ...) dispatch - exactly what onAsync's contract
    // forbids.
    it('resolves (never rejects) when isWsmToolAcquired throws a non-ENOENT filesystem error', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear().mockRejectedValue(Object.assign(new Error('EBUSY: resource busy or locked'), { code: 'EBUSY' }));
      scanWsmConflictsMock.mockClear();
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();

      await expect(fireAsyncEvent('did-deploy', 'profile1', undefined)).resolves.toBeUndefined();
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
      expect(notifyConflictsIfChangedMock).not.toHaveBeenCalled();
    });

    // Regression test: the try/catch wraps the entire handler body now, including the
    // selectors.profileById(context.api.getState(), profileId) gate at the very top -
    // not just the parts already known to be able to throw (isWsmToolAcquired,
    // scanWsmConflicts). A synchronous throw from state lookup should be as unlikely
    // as it is cheap to guard against, but onAsync's "never reject" contract applies to
    // the whole handler.
    it('resolves (never rejects) when reading state for the deployed-profile gate throws synchronously', async () => {
      ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
      isWsmToolAcquiredMock.mockClear();
      scanWsmConflictsMock.mockClear();
      notifyConflictsIfChangedMock.mockClear();
      const { context, fireOnce, fireAsyncEvent } = fakeContext(WITCHER3_GAME_ID, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      // main()/fireOnce() run first, using the working getState for
      // tryRegisterWsmTool's own unrelated isWitcher3Active check - only sabotage
      // getState afterward, right before firing did-deploy, so this test isolates the
      // did-deploy handler's own robustness rather than breaking context.once itself.
      main(context);
      fireOnce();
      context.api.getState = () => {
        throw new Error('state store unavailable');
      };

      await expect(fireAsyncEvent('did-deploy', 'profile1', undefined)).resolves.toBeUndefined();
      expect(isWsmToolAcquiredMock).not.toHaveBeenCalled();
      expect(scanWsmConflictsMock).not.toHaveBeenCalled();
    });
  });

  // Unit K: the second coexistence-check trigger point, alongside did-deploy above -
  // see coexistenceGuard.ts's own header comment for why profile-will-change itself is
  // deliberately not used and profile-did-change is used instead.
  describe('profile-did-change coexistence check', () => {
    it('registers a profile-did-change handler via events.on (not onAsync) at context.once time', () => {
      const { context, fireOnce, fireEvent } = fakeContext(undefined);

      main(context);
      fireOnce();

      // Must not throw - proves a listener really is registered for this event name,
      // not silently falling through to a no-op (fireEvent no-ops on an unknown event
      // name too, so this alone wouldn't distinguish "registered" from "not registered"
      // without the refreshCoexistenceStateMock assertions in the tests below).
      expect(() => fireEvent('profile-did-change', 'profile1')).not.toThrow();
    });

    it('calls refreshCoexistenceState when the switched-to profile is witcher3', () => {
      refreshCoexistenceStateMock.mockClear().mockResolvedValue(undefined);
      const { context, fireOnce, fireEvent } = fakeContext(undefined, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      fireEvent('profile-did-change', 'profile1');

      expect(refreshCoexistenceStateMock).toHaveBeenCalledTimes(1);
      expect(refreshCoexistenceStateMock).toHaveBeenCalledWith(context.api);
    });

    it('does not call refreshCoexistenceState when the switched-to profile is a different game', () => {
      refreshCoexistenceStateMock.mockClear();
      const { context, fireOnce, fireEvent } = fakeContext(undefined, {
        profile1: { gameId: 'skyrimse' },
      });

      main(context);
      fireOnce();
      fireEvent('profile-did-change', 'profile1');

      expect(refreshCoexistenceStateMock).not.toHaveBeenCalled();
    });

    it('does not call refreshCoexistenceState when the profileId is unknown (no matching profile at all)', () => {
      refreshCoexistenceStateMock.mockClear();
      const { context, fireOnce, fireEvent } = fakeContext(undefined, {});

      main(context);
      fireOnce();
      fireEvent('profile-did-change', 'unknown-profile');

      expect(refreshCoexistenceStateMock).not.toHaveBeenCalled();
    });

    it('does not throw (a plain events.on listener rejecting is an unhandled rejection in Vortex\'s own process) when refreshCoexistenceState rejects', async () => {
      refreshCoexistenceStateMock.mockClear().mockRejectedValue(new Error('coexistence check failed'));
      const { context, fireOnce, fireEvent } = fakeContext(undefined, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();

      expect(() => fireEvent('profile-did-change', 'profile1')).not.toThrow();
      // Let the rejected promise's own .catch() handler actually run before the test
      // ends, matching the existing "does not throw when ensureWsmToolRegistered
      // rejects" test's own pattern above.
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    it('does not throw when reading state for the profile gate throws synchronously', () => {
      refreshCoexistenceStateMock.mockClear();
      const { context, fireOnce, fireEvent } = fakeContext(undefined, {
        profile1: { gameId: WITCHER3_GAME_ID },
      });

      main(context);
      fireOnce();
      context.api.getState = () => {
        throw new Error('state store unavailable');
      };

      expect(() => fireEvent('profile-did-change', 'profile1')).not.toThrow();
      expect(refreshCoexistenceStateMock).not.toHaveBeenCalled();
    });
  });
});

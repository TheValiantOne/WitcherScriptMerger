import { describe, expect, it, vi } from 'vitest';

// `vi.mock` factories are hoisted above imports, so anything they reference has to come
// from `vi.hoisted` rather than an ordinary outer-scope `const` - this isolates index.ts's
// own wiring (what this file actually tests) from toolAcquisition.ts's real behavior
// (already thoroughly covered by toolAcquisition.test.ts).
const { ensureWsmToolRegisteredMock, registerWsmStatusDashletMock } = vi.hoisted(() => ({
  ensureWsmToolRegisteredMock: vi.fn(),
  registerWsmStatusDashletMock: vi.fn(),
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

import main from './index';
import { WITCHER3_GAME_ID } from './gating';

/** A minimal stand-in for IExtensionContext - just enough surface for index.ts's own
 *  logic (context.once, context.api.getState/events.on), matching gating.test.ts's own
 *  fakeApi philosophy: a simplified fake, not a replica of Vortex's real context shape. */
function fakeContext(initialActiveGameId: string | undefined) {
  const state = { activeGameId: initialActiveGameId };
  let onceCallback: (() => void) | undefined;
  const eventListeners = new Map<string, Array<() => void>>();

  const context = {
    once: (callback: () => void) => {
      onceCallback = callback;
    },
    api: {
      getState: () => state,
      events: {
        on: (eventName: string, listener: () => void) => {
          const listeners = eventListeners.get(eventName) ?? [];
          listeners.push(listener);
          eventListeners.set(eventName, listeners);
        },
      },
    },
  };

  return {
    context: context as unknown as Parameters<typeof main>[0],
    fireOnce: () => onceCallback?.(),
    fireEvent: (eventName: string) => eventListeners.get(eventName)?.forEach((listener) => listener()),
    setActiveGame: (gameId: string | undefined) => {
      state.activeGameId = gameId;
    },
  };
}

describe('main (index.ts)', () => {
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

  it('registers the WSM status dashlet once, at context.once time, regardless of the active game', () => {
    // Unlike tryRegisterWsmTool, this registration isn't gated on isWitcher3Active here
    // - statusTile.ts's own registerDashlet call supplies a live isVisible callback
    // instead (covered by statusTile.test.ts), so index.ts always registers it once.
    ensureWsmToolRegisteredMock.mockClear().mockResolvedValue(false);
    registerWsmStatusDashletMock.mockClear();
    const { context, fireOnce } = fakeContext('skyrimse');

    main(context);
    fireOnce();

    expect(registerWsmStatusDashletMock).toHaveBeenCalledTimes(1);
    expect(registerWsmStatusDashletMock).toHaveBeenCalledWith(context);
  });
});

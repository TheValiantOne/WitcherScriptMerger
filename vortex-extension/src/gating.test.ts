import { describe, expect, it } from 'vitest';
import { isWitcher3Active, WITCHER3_GAME_ID } from './gating';

// A minimal stand-in for IExtensionApi - just enough for isWitcher3Active's own logic
// (api.getState(), fed through selectors.activeGameId). The real selectors.activeGameId
// is provided at test time by test/testUtils/vortexApiStub.ts (see vitest.config.ts's
// alias) - a simplified fake, not a replica of Vortex's real Redux state shape - so the
// fake state here matches that stub's shape, not real Vortex's.
function fakeApi(activeGameId: string | undefined) {
  return {
    getState: () => ({ activeGameId }),
  } as unknown as Parameters<typeof isWitcher3Active>[0];
}

describe('isWitcher3Active', () => {
  it('returns true when witcher3 is the active game', () => {
    expect(isWitcher3Active(fakeApi(WITCHER3_GAME_ID))).toBe(true);
  });

  it('returns false for a different active game', () => {
    expect(isWitcher3Active(fakeApi('skyrimse'))).toBe(false);
  });

  it('returns false when no game is active', () => {
    expect(isWitcher3Active(fakeApi(undefined))).toBe(false);
  });
});

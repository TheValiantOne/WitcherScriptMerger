import { describe, expect, it, vi } from 'vitest';
import { WITCHER3_GAME_ID } from './gating';
import { registerWsmStatusDashlet } from './statusTile';

/** A minimal stand-in for IExtensionContext, matching index.test.ts's own fakeContext
 *  philosophy - just enough surface for registerWsmStatusDashlet's own logic. */
function fakeContext() {
  const registerDashlet = vi.fn();
  return {
    context: { api: { fake: 'api' }, registerDashlet } as unknown as Parameters<typeof registerWsmStatusDashlet>[0],
    registerDashlet,
  };
}

describe('registerWsmStatusDashlet', () => {
  it('calls context.registerDashlet with the real 8-argument signature', () => {
    const { context, registerDashlet } = fakeContext();

    registerWsmStatusDashlet(context);

    expect(registerDashlet).toHaveBeenCalledTimes(1);
    const [title, width, height, position, component, isVisible, propsCallback, options] =
      registerDashlet.mock.calls[0];

    expect(typeof title).toBe('string');
    expect(width).toBeGreaterThanOrEqual(1);
    expect(width).toBeLessThanOrEqual(3);
    expect(height).toBeGreaterThanOrEqual(1);
    expect(height).toBeLessThanOrEqual(6);
    expect(typeof position).toBe('number');
    expect(typeof component).toBe('function');
    expect(typeof isVisible).toBe('function');
    expect(typeof propsCallback).toBe('function');
    expect(typeof options).toBe('object');
  });

  it('isVisible is true only when witcher3 is the active game', () => {
    const { context, registerDashlet } = fakeContext();
    registerWsmStatusDashlet(context);

    const isVisible = registerDashlet.mock.calls[0][5] as (state: unknown) => boolean;

    expect(isVisible({ activeGameId: WITCHER3_GAME_ID })).toBe(true);
    expect(isVisible({ activeGameId: 'skyrimse' })).toBe(false);
  });

  it('the props callback supplies the extension api', () => {
    const { context, registerDashlet } = fakeContext();
    registerWsmStatusDashlet(context);

    const propsCallback = registerDashlet.mock.calls[0][6] as () => { api: unknown };
    expect(propsCallback()).toEqual({ api: context.api });
  });
});

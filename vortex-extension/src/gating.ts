import { selectors, types } from 'vortex-api';

/**
 * The game id Vortex's own built-in `game-witcher3` extension registers (see
 * `extensions/games/game-witcher3/src/` in the `Nexus-Mods/Vortex` monorepo). This
 * extension is a companion to that extension, not a replacement of it - it must never
 * call `context.registerGame` for this id.
 */
export const WITCHER3_GAME_ID = 'witcher3';

/**
 * True only when Witcher 3 is the currently active game.
 *
 * Every feature this extension registers - here and in every later unit built on this
 * scaffold (tool acquisition, conflict scanning, the merge panel, dashlets) - must be
 * gated on this. Vortex loads every installed extension regardless of which game is
 * currently active, so without this check, this extension's registrations would apply
 * (and potentially show UI) for every other game too.
 *
 * Prefer passing this as a live `condition` callback to whichever `context.register*`
 * API a later unit uses (re-evaluated by Vortex itself on every game-mode switch)
 * rather than only checking it once at extension-load time - a user can switch the
 * active game without restarting Vortex.
 */
export function isWitcher3Active(api: types.IExtensionApi): boolean {
  return selectors.activeGameId(api.getState()) === WITCHER3_GAME_ID;
}

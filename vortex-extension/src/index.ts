import { log, types } from 'vortex-api';
import { isWitcher3Active } from './gating';

/**
 * Extension entry point (Vortex looks for a default export named `init`, or a function
 * named `init`, per `@nexusmods/vortex-api`'s own documented extension structure).
 *
 * This is the foundation scaffold unit: no feature registration lives here yet. Later
 * units (tool acquisition, conflict scanning, the merge panel, dashlets) each add their
 * own `context.register*` calls inside the `context.once(...)` callback below, gated on
 * `isWitcher3Active` (imported from `./gating`) - preferably via each registration API's
 * own `condition` callback, so a live game-mode switch is honored without requiring a
 * Vortex restart, rather than only checked once here.
 *
 * This extension must never call `context.registerGame('witcher3', ...)` - Vortex's own
 * built-in `game-witcher3` extension already owns that registration; this extension is a
 * companion to it, not a replacement.
 */
function main(context: types.IExtensionContext): boolean {
  context.once(() => {
    if (isWitcher3Active(context.api)) {
      log('info', 'witcherscriptmerger-vortex: witcher3 is the active game, extension ready');
    } else {
      log('debug', 'witcherscriptmerger-vortex: active game is not witcher3, extension is idle');
    }
  });

  return true;
}

export default main;

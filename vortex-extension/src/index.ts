import { log, types } from 'vortex-api';
import { isWitcher3Active } from './gating';
import { registerMergeHistoryDashlet } from './mergeHistoryDashlet';
import { ensureWsmToolRegistered } from './toolAcquisition';

/**
 * Extension entry point (Vortex looks for a default export named `init`, or a function
 * named `init`, per `@nexusmods/vortex-api`'s own documented extension structure).
 *
 * This unit (tool acquisition) is the first to add real registration: re-registering a
 * previously-acquired WSM binary as a discovered tool, via `ensureWsmToolRegistered`
 * (`./toolAcquisition`) - a **local-only, network-free** check, safe to run
 * unconditionally on every load. Deliberately not an eager background *download* here:
 * as of this unit, no GitHub Release exists on this repo yet (see
 * `githubRelease.ts`'s own doc comment), so attempting one on every Vortex startup
 * would just be a guaranteed, noisy failure with nothing to show for it.
 * `./toolAcquisition`'s `acquireWsmTool` (the actual download/verify/extract/register
 * pipeline) is exported for a later unit's own UI trigger (a "Get WitcherScriptMerger"
 * action, once one exists) to call on explicit user request instead.
 *
 * Re-evaluated on every `'gamemode-activated'` event (`@nexusmods/vortex-api`'s own
 * README documents this event, firing with the newly-active game's id), not just once
 * at `context.once` time - a user can switch the active game without restarting Vortex,
 * and without this, registering a previously-acquired tool would only ever happen if
 * Witcher 3 already happened to be active the moment Vortex loaded this extension.
 *
 * Later units (conflict scanning, the merge panel, dashlets) each add their own
 * `context.register*` calls inside the `context.once(...)` callback below, gated on
 * `isWitcher3Active` (imported from `./gating`) - preferably via each registration API's
 * own `condition` callback, so a live game-mode switch is honored without requiring a
 * Vortex restart, the same way `tryRegisterWsmTool` below re-checks it on every
 * `'gamemode-activated'` event rather than only once.
 *
 * This extension must never call `context.registerGame('witcher3', ...)` - Vortex's own
 * built-in `game-witcher3` extension already owns that registration; this extension is a
 * companion to it, not a replacement.
 */
function main(context: types.IExtensionContext): boolean {
  function tryRegisterWsmTool(): void {
    if (!isWitcher3Active(context.api)) {
      log('debug', 'witcherscriptmerger-vortex: active game is not witcher3, extension is idle');
      return;
    }

    log('info', 'witcherscriptmerger-vortex: witcher3 is the active game, extension ready');

    ensureWsmToolRegistered(context.api)
      .then((registered) => {
        if (registered) {
          log('info', 'witcherscriptmerger-vortex: re-registered a previously acquired WSM tool');
        } else {
          log('debug', 'witcherscriptmerger-vortex: no previously acquired WSM tool found - nothing to register yet');
        }
      })
      .catch((err: unknown) => {
        // Must never throw out of an event handler / context.once - an uncaught
        // rejection here would be an unhandled promise rejection in Vortex's own
        // process, not a contained extension failure. Local-only re-registration
        // failing is unexpected (it does no network I/O), so this is logged at 'warn'
        // rather than swallowed silently.
        log('warn', 'witcherscriptmerger-vortex: failed to re-register a previously acquired WSM tool', {
          error: err instanceof Error ? err.message : String(err),
        });
      });
  }

  // Registered synchronously here, not inside context.once below - see
  // mergeHistoryDashlet.ts's own registerMergeHistoryDashlet doc comment for why a
  // register call specifically must not be deferred into once, unlike
  // tryRegisterWsmTool's legitimate use of once just above.
  registerMergeHistoryDashlet(context);

  context.once(() => {
    tryRegisterWsmTool();
    context.api.events.on('gamemode-activated', tryRegisterWsmTool);
  });

  return true;
}

export default main;

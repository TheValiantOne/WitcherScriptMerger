import { log, types } from 'vortex-api';
import { isWsmToolAcquired, scanWsmConflicts } from './conflictScan';
import { notifyConflictsIfChanged } from './conflictNotifications';
import { isWitcher3Active } from './gating';
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
 * This unit (conflict scanning) adds the second real registration: after every Vortex
 * deployment for Witcher 3 finishes, scan for WSM script conflicts
 * (`./conflictScan`'s `scanWsmConflicts`) and show a dashboard notification when the
 * *unresolved* conflict set has changed since the last check this session
 * (`./conflictNotifications`'s `notifyConflictsIfChanged`). Registered via
 * `context.api.onAsync('did-deploy', ...)`, not `context.api.events.on(...)` -
 * `did-deploy` is documented (`@nexusmods/vortex-api`'s own `docs/EVENTS.md`) as an
 * *async* event (fired via `emitAndAwait`), and the package's own README example
 * (`#### Event hooks`) and Vortex's own built-in `game-witcher3` extension
 * (`extensions/games/game-witcher3/src/index.ts`: `context.api.onAsync("did-deploy",
 * onDidDeploy(context.api))`) both register it that way - see this unit's PR
 * description for the exact citations.
 *
 * Later units (the merge panel, dashlets) each add their own `context.register*` calls
 * inside the `context.once(...)` callback below, gated on `isWitcher3Active` (imported
 * from `./gating`) - preferably via each registration API's own `condition` callback,
 * so a live game-mode switch is honored without requiring a Vortex restart, the same
 * way `tryRegisterWsmTool` below re-checks it on every `'gamemode-activated'` event
 * rather than only once.
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

  // onAsync's own contract (@nexusmods/vortex-api's lib/api.d.ts doc comment on
  // IExtensionApi.onAsync): "listeners should report all errors themselves, it is
  // considered a bug if the listener returns a rejected promise" - so every path here
  // must resolve, never reject, matching tryRegisterWsmTool's own catch-and-log
  // (non-throwing) shape above.
  async function checkForConflictsAfterDeploy(): Promise<void> {
    if (!isWitcher3Active(context.api)) {
      return;
    }

    if (!(await isWsmToolAcquired(context.api))) {
      // Same normal, expected state tryRegisterWsmTool already logs at 'debug' above -
      // no WSM binary acquired yet is not an error worth spawning a doomed process to
      // discover.
      log('debug', 'witcherscriptmerger-vortex: no acquired WSM tool - skipping post-deploy conflict scan');
      return;
    }

    try {
      const conflicts = await scanWsmConflicts(context.api);
      notifyConflictsIfChanged(context.api, conflicts);
    } catch (err) {
      log('warn', 'witcherscriptmerger-vortex: failed to scan for script conflicts after deployment', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  context.once(() => {
    tryRegisterWsmTool();
    context.api.events.on('gamemode-activated', tryRegisterWsmTool);
    context.api.onAsync('did-deploy', checkForConflictsAfterDeploy);
  });

  return true;
}

export default main;

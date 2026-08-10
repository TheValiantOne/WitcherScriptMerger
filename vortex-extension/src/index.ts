import { log, types } from 'vortex-api';
import { isWitcher3Active } from './gating';
import { registerResolveScriptConflictsAction } from './resolveAction';
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
 * Later units (conflict scanning, dashlets) each add their own `context.register*`
 * calls directly in `main`, alongside `registerResolveScriptConflictsAction` below -
 * NOT inside the `context.once(...)` callback: `IExtensionContext.once`'s own doc
 * comment (`lib/api.d.ts`) says registration calls are expected to have already
 * happened by the time `once` fires ("if your extension registers its own extension
 * function... those registrations happen before once is called"), matching every
 * `registerAction` call site found in Vortex's own extensions (`gh search code
 * 'registerAction("mod-icons"' --repo Nexus-Mods/Vortex`) - none of them defer through
 * `once`. Each registration instead gates on `isWitcher3Active` (imported from
 * `./gating`) via its own `condition` callback, so a live game-mode switch is honored
 * without requiring a Vortex restart - see `resolveAction.ts`'s own doc comment for how
 * this unit's own action does exactly that, the declarative counterpart to how
 * `tryRegisterWsmTool` below re-checks the same condition imperatively on every
 * `'gamemode-activated'` event.
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

  context.once(() => {
    tryRegisterWsmTool();
    context.api.events.on('gamemode-activated', tryRegisterWsmTool);
  });

  // This unit's own registration - see resolveAction.ts's own doc comment for why it
  // gates on Witcher 3 being active via a live `condition` callback rather than an
  // upfront check here, the same pattern every other registration in this extension
  // follows (gating.ts's own doc comment).
  registerResolveScriptConflictsAction(context);

  return true;
}

export default main;

import { log, types } from 'vortex-api';
import { isWitcher3Active } from './gating';
import { registerWsmStatusDashlet } from './statusTile';
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
 * **Correction (found while building Unit J, applies to every future unit too):** an
 * earlier version of this comment said later units should add their own
 * `context.register*` calls *inside* the `context.once(...)` callback below. That's
 * wrong, and matters for real Vortex behavior, not just style - `@nexusmods/vortex-api`'s
 * own `lib/api.d.ts` doc comment on `IExtensionContext` is explicit: register functions
 * "must be called immediately inside the init function," calls to them are "stored and
 * evaluated once all extensions have been initialised," and `once` (part (c) of that same
 * doc comment) is documented as being for "all your extension setup *except* for the
 * register calls (i.e. installing event handlers, doing startup calculations)" - not a
 * valid place to call a `context.register*` function at all. `registerWsmStatusDashlet`
 * below is therefore called directly in `main`'s own body, synchronously, before
 * `context.once(...)` - not deferred into it. Every future unit adding a
 * `context.register*` call (an action, a main page, a settings page, another dashlet)
 * must do the same: call it directly here, gating *visibility* (not the registration call
 * itself) on `isWitcher3Active` via that API's own live `condition`/`isVisible` callback
 * instead, exactly like `registerWsmStatusDashlet` does (see `statusTile.ts`). Only
 * non-register work - event handlers, one-time startup calculations, anything that reads
 * `context.api`'s fully-initialized state - belongs inside `context.once`, which is
 * exactly what `tryRegisterWsmTool` below is (it dispatches a Redux action via
 * `api.store.dispatch`, not a `context.register*` call, so `context.once` is the right
 * place for it).
 *
 * This extension must never call `context.registerGame('witcher3', ...)` - Vortex's own
 * built-in `game-witcher3` extension already owns that registration; this extension is a
 * companion to it, not a replacement.
 */
function main(context: types.IExtensionContext): boolean {
  // Unit J: the dependency/status dashlet. Called here, synchronously and
  // unconditionally, per the register-function contract explained above - never
  // deferred into context.once. Visibility itself is still gated on Witcher 3 being the
  // active game, via registerWsmStatusDashlet's own live `isVisible` callback
  // (statusTile.ts), so this unconditional call doesn't show the tile for other games.
  registerWsmStatusDashlet(context);

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

  return true;
}

export default main;

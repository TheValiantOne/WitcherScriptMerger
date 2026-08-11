import { log, selectors, types } from 'vortex-api';
import { isWsmToolAcquired, scanWsmConflicts } from './conflictScan';
import { isModOrDependencyInstallActive, notifyConflictsIfChanged } from './conflictNotifications';
import { isWitcher3Active, WITCHER3_GAME_ID } from './gating';
import { registerMergeHistoryDashlet } from './mergeHistoryDashlet';
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
 * This unit (the merge-history dashlet) adds the third real registration:
 * `registerMergeHistoryDashlet` (`./mergeHistoryDashlet`), called directly in `main`,
 * NOT deferred into the `context.once(...)` callback below - `IExtensionContext.once`'s
 * own doc comment (`@nexusmods/vortex-api`'s `lib/api.d.ts`) says registration calls are
 * expected to have already happened by the time `once` fires, matching every
 * `registerDashlet`/`registerAction` call site in Vortex's own built-in extensions
 * (none of them defer through `once`). Each registration instead gates on
 * `isWitcher3Active` (imported from `./gating`) via its own `condition`/`isVisible`
 * callback, so a live game-mode switch is honored without requiring a Vortex restart -
 * the declarative counterpart to how `tryRegisterWsmTool` below re-checks the same
 * condition imperatively on every `'gamemode-activated'` event.
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
 * (this unit's own fourth registration) is therefore called directly in `main`'s own
 * body, synchronously, before `context.once(...)` - not deferred into it, matching
 * `registerMergeHistoryDashlet` above. Every future unit adding a `context.register*`
 * call (an action, a main page, a settings page, another dashlet) must do the same: call
 * it directly here, gating *visibility* (not the registration call itself) on
 * `isWitcher3Active` via that API's own live `condition`/`isVisible` callback instead,
 * exactly like `registerWsmStatusDashlet` does (see `statusTile.ts`). Only non-register
 * work - event handlers, one-time startup calculations, anything that reads
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

  // onAsync's own contract (@nexusmods/vortex-api's lib/api.d.ts doc comment on
  // IExtensionApi.onAsync): "listeners should report all errors themselves, it is
  // considered a bug if the listener returns a rejected promise" - so every path here
  // must resolve, never reject, matching tryRegisterWsmTool's own catch-and-log
  // (non-throwing) shape above.
  //
  // Gates on the *deployed* profile's own game (via did-deploy's own `profileId`
  // argument, looked up with `selectors.profileById`), not `isWitcher3Active(context.api)`
  // (whichever game happens to be active at the moment this async handler actually
  // runs). Those are not the same thing: `did-deploy` fires once deployment completes,
  // and by the time this handler's own turn comes up (after every other handler
  // `emitAndAwait` is also waiting on), the user may already have switched to a
  // different game. `isWitcher3Active` would then read the *new* active game and skip a
  // real Witcher 3 deployment's scan entirely - a false negative that misses genuine
  // conflicts, not just an ordering nicety. `did-deploy`'s own `IDeploymentManifest`
  // exposes an optional `gameId`, but it's `gameId?: string` (not guaranteed present),
  // so `profileId` -> `selectors.profileById(...).gameId` is the reliable, time-invariant
  // signal used here instead. **Not a verbatim copy of `game-witcher3`'s own
  // `eventHandlers.ts`/`util.ts` `validateProfile(profileId, state)`** - read directly:
  // that function still ultimately keys off `selectors.activeProfile(state).gameId` (the
  // same "what's active *now*" read this comment argues against), just with an added
  // guard that `profileId` matches the currently active profile's own id. That's the
  // right call for what `game-witcher3` uses it for (INI/load-order bookkeeping that
  // only makes sense for the actively-displayed profile) but not for this extension's
  // narrower job here - telling the user about conflicts from a deployment that
  // genuinely happened is still correct even if they've since tabbed to another game, so
  // this deliberately drops the "still the active profile" cross-check and trusts
  // `profileId` alone, which is what actually removes the race rather than just
  // narrowing its window.
  async function checkForConflictsAfterDeploy(profileId: string): Promise<void> {
    // The entire body lives inside this one try/catch, including the deployed-game
    // gate immediately below - onAsync's contract (quoted above) applies to the whole
    // handler, not just the parts that were already known to be able to throw. A
    // simple selectors.profileById lookup or api.getState() call is very unlikely to
    // throw, but there's no reason to leave even a narrow, structurally-identical gap
    // next to the one just closed for isWsmToolAcquired below.
    try {
      const deployedGameId = selectors.profileById(context.api.getState(), profileId)?.gameId;
      if (deployedGameId !== WITCHER3_GAME_ID) {
        return;
      }

      if (!(await isWsmToolAcquired(context.api))) {
        // Same normal, expected state tryRegisterWsmTool already logs at 'debug' above -
        // no WSM binary acquired yet is not an error worth spawning a doomed process to
        // discover.
        log('debug', 'witcherscriptmerger-vortex: no acquired WSM tool - skipping post-deploy conflict scan');
        return;
      }

      if (isModOrDependencyInstallActive(context.api)) {
        // Purely an optimization, not a correctness requirement - notifyConflictsIfChanged
        // (conflictNotifications.ts) checks this same condition again on whatever result
        // would come back anyway, so this isn't the only thing standing between a real
        // scan and a suppressed notification. What this pre-check buys is not spawning an
        // entire WSM process in the first place when the answer is already known to be
        // "discard this result" - worth avoiding specifically because a dependency-install
        // burst (e.g. installing a Collection) can trigger several deploy-per-mod cycles
        // in a row, each of which would otherwise spawn and tear down a WSM process for
        // nothing.
        log('debug', 'witcherscriptmerger-vortex: mod/dependency install activity in progress - skipping post-deploy conflict scan');
        return;
      }

      const conflicts = await scanWsmConflicts(context.api);
      notifyConflictsIfChanged(context.api, conflicts);
    } catch (err) {
      log('warn', 'witcherscriptmerger-vortex: post-deploy conflict scan failed', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  // Registered synchronously here, not inside context.once below - see
  // mergeHistoryDashlet.ts's own registerMergeHistoryDashlet doc comment for why a
  // register call specifically must not be deferred into once, unlike
  // tryRegisterWsmTool's legitimate use of once just above.
  registerMergeHistoryDashlet(context);

  context.once(() => {
    tryRegisterWsmTool();
    context.api.events.on('gamemode-activated', tryRegisterWsmTool);
    context.api.onAsync('did-deploy', checkForConflictsAfterDeploy);
  });

  return true;
}

export default main;

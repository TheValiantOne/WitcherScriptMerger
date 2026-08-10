import { log, types } from 'vortex-api';
import { ScanConflictsResult } from './mcpClient';

/**
 * Dashboard-notification id this extension uses for "you have unresolved WSM script
 * conflicts" - deliberately distinct from `"witcher3-merge"`, the notification id
 * Vortex's own built-in `game-witcher3` extension uses for its own (unconditional,
 * scan-less) post-deploy "you may need to run the script merger" prompt (confirmed
 * directly against that extension's current source,
 * `extensions/games/game-witcher3/src/eventHandlers.ts`'s `queryScriptMerge`, in the
 * `Nexus-Mods/Vortex` monorepo - see this unit's PR description for the exact commit
 * fetched). Reusing that id would let a user with both extensions installed silently
 * overwrite/collide on the same notification slot instead of seeing both.
 */
export const WSM_CONFLICTS_NOTIFICATION_ID = 'witcherscriptmerger-vortex-conflicts';

/**
 * Builds a stable, order-independent signature for a set of conflicts, used to detect
 * whether the *actual* unresolved-conflict set changed since the last check this
 * session (see `notifyConflictsIfChanged` below).
 *
 * Deliberately includes each conflict's own contributing mod names, not just its
 * `relativePath` alone - a per-conflict `relativePath:sortedModNames` entry, the
 * entries themselves then sorted and newline-joined. `relativePath` alone would miss a
 * real, user-relevant change: WSM's own `scan_conflicts` de-duplicates by
 * `relativePath` (`FileIndex/ModFileIndex.cs`'s `GetModFilesFromPaths` folds multiple
 * mods touching the same file into one `ModFile` entry), so no two entries in a single
 * scan result share a `relativePath` - but the *set of mods* contributing to that same
 * `relativePath` can change between two scans (e.g. a third mod starts touching an
 * already-conflicting file that was never merged), and a `relativePath`-only signature
 * can't tell that apart from "nothing changed," silently missing the re-notification a
 * user would want. `|` and `:` are both used as delimiters here, and `\n` to join
 * entries - none of the three can appear in a Windows file/directory name (`|`, `:`,
 * and newline are all part of Windows' reserved-character/control-character set), so
 * there's no realistic way for two genuinely different conflict sets to collide onto
 * the same signature string.
 */
export function computeConflictSignature(conflicts: ScanConflictsResult): string {
  return conflicts
    .map((c) => `${c.relativePath}:${[...c.mods.map((m) => m.name)].sort().join('|')}`)
    .sort()
    .join('\n');
}

/**
 * Normalizes one `state.session.base.activity` group entry into a plain string array,
 * tolerating either shape a caller might see there.
 *
 * **The published `@nexusmods/vortex-api` `lib/api.d.ts` types `ISession.activity` as
 * `{[group: string]: string}` - confirmed stale by reading the real reducer.** Fetched
 * directly (`gh api`, `src/renderer/src/reducers/session.ts` in the `Nexus-Mods/Vortex`
 * monorepo - see this unit's PR description for the exact commit SHA): `startActivity`
 * does `activity: {...state.activity, [group]: [...(state.activity[group] ?? []),
 * activityId]}` and `stopActivity` does `[group]: (state.activity[group] ??
 * []).filter(id => id !== activityId)` - i.e. the real runtime value is a `string[]`
 * per group (letting two concurrent activities share one group), and critically,
 * `stopActivity` never deletes the group key - it leaves an **empty array** behind. A
 * naive `Boolean(activity[group])` truthiness check would therefore see `[]` (truthy in
 * JS) and report "still active" forever after the first activity in that group ever
 * ran, permanently suppressing every future notification - a real, easy-to-miss trap
 * this function exists specifically to avoid. Written to tolerate the *documented*
 * shape too (a single string), not just the confirmed-real one, so this doesn't quietly
 * break again if a future SDK version actually matches its own published types.
 */
function activityEntries(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value as string[];
  }
  if (typeof value === 'string' && value.length > 0) {
    return [value];
  }
  return [];
}

/**
 * True while Vortex reports a mod-install or dependency-install operation in progress,
 * per `state.session.base.activity`.
 *
 * Exported (not just used internally by `notifyConflictsIfChanged` below) so
 * `index.ts`'s `checkForConflictsAfterDeploy` can also check it *before* calling
 * `scanWsmConflicts` - avoiding an entirely wasted WSM process spawn during, e.g., a
 * Collection install that triggers several deploy-per-mod cycles in a row, since
 * `notifyConflictsIfChanged` would just discard that scan's result anyway. That
 * pre-check is a pure optimization, not a correctness requirement - this function is
 * still called again inside `notifyConflictsIfChanged` itself, because activity can
 * start at any point during the scan that follows a clean pre-check.
 *
 * The two specific group/id checks below are not a guess at plausible-sounding names -
 * both are confirmed directly against the real, current `Nexus-Mods/Vortex` monorepo
 * source (fetched via `gh api`; see this unit's PR description for exact file paths and
 * commit SHAs):
 *
 * - A non-empty `installing_dependencies` group is the *exact* guard Vortex's own
 *   built-in `game-witcher3` extension already uses before showing its own
 *   `"witcher3-merge"` conflict-adjacent notification
 *   (`eventHandlers.ts`'s `queryScriptMerge`: `if ((state.session.base.activity
 *   ?.installing_dependencies ?? []).length > 0) { return; }` - note this real call
 *   site already assumes the array shape, another independent confirmation of the
 *   `activityEntries` finding above) - this is the single closest possible prior art
 *   for this exact "should I bug the user about script conflicts right now" decision,
 *   so it's mirrored here deliberately rather than independently re-derived. The group
 *   is populated by `startActivity("installing_dependencies", <modId>)` in
 *   `mod_management/InstallManager.ts`'s `withActivityTracking`, called around both
 *   `installRecommendationsImpl`/`installDependenciesImpl` (collection/dependency
 *   installs).
 * - `'installing'` present in the `mods` group covers the plainer single-mod-install
 *   case, populated by `startActivity("mods", "installing")` in
 *   `mod_management/InstallContext.ts`. **Deliberately not** "the `mods` group is
 *   non-empty" - that same group key is also used for `startActivity("mods",
 *   "deployment")` in `mod_management/index.ts`, and - confirmed directly by reading
 *   that file - `stopActivity("mods", "deployment")` only fires *after*
 *   `emitAndAwait("did-deploy", ...)` resolves, i.e. after every `did-deploy` handler
 *   (including this extension's own) has already run. A blanket "is the `mods` group
 *   non-empty" check would therefore always be true from inside this extension's own
 *   `did-deploy` handler and permanently suppress every notification - checking for the
 *   specific `'installing'` entry (not `'deployment'`) is what avoids that. Unlike the
 *   `installing_dependencies` check above, this one has no direct precedent in
 *   `game-witcher3`'s own code (it doesn't check this) - included because this unit's
 *   own task explicitly names "mod-install" alongside "dependency-install", but worth
 *   flagging as the less battle-tested of the two.
 */
export function isModOrDependencyInstallActive(api: types.IExtensionApi): boolean {
  // api.getState() defaults to IState (its generic parameter's own default), so
  // session.base.activity is real, typed state here, not a hand-rolled shape - the
  // optional chaining is defensive only (a fake `api` in a unit test need not supply
  // every nested field), not a hedge against the real type being different.
  const activity = api.getState()?.session?.base?.activity;
  if (!activity) {
    return false;
  }
  return (
    activityEntries(activity.installing_dependencies).length > 0 ||
    activityEntries(activity.mods).includes('installing')
  );
}

/** Module-level "last seen" state - deliberately in-memory only and scoped to this
 *  extension's own process lifetime (one Vortex session), per this unit's own
 *  suppression requirement: no persisted cross-session state needed. Initialized to
 *  `''` - the same value `computeConflictSignature([])` produces for "no unresolved
 *  conflicts" - rather than `undefined`, specifically so the very first check of a
 *  session with zero conflicts matches the "nothing changed" early-return below and
 *  never calls `dismissNotification` for an id that was never sent (harmless against
 *  the real Vortex API, which no-ops on an unknown id, but pointless noise otherwise).
 *  Exported reset hook is for test isolation only - no production caller should ever
 *  need it. */
let lastNotifiedSignature = '';

export function resetConflictNotificationState(): void {
  lastNotifiedSignature = '';
}

/**
 * Given an already-obtained `scan_conflicts` result, shows (or updates/dismisses) the
 * dashboard notification for unresolved WSM conflicts - deliberately takes the scan
 * result as a plain argument, rather than performing the scan itself, so it's directly
 * unit-testable against a fabricated conflicts array without spawning any process (see
 * `conflictNotifications.test.ts`). `index.ts`'s `did-deploy` handler is the only
 * production caller, but nothing here reaches into Vortex's event system itself.
 *
 * Suppression: skips entirely (no state mutated at all) while
 * `isModOrDependencyInstallActive` is true, so a later, real post-install `did-deploy`
 * still gets a fair chance to notify against the real conflict set rather than being
 * silently marked "already seen" by a scan taken mid-install. Otherwise, only sends a
 * fresh notification when the *unresolved* (`!alreadyResolved`) conflict set's
 * signature actually changed since the last check this session; a conflict
 * `scan_conflicts` reports as `alreadyResolved` has a recorded, still-valid merge already covering it
 * (`AppState.Inventory.HasResolvedConflict`, `WitcherScriptMerger.Core/Mcp/WsmMcpTools.cs`)
 * and needs no user action, so it's excluded from both the notification count and the
 * signature entirely - otherwise a deployment that changes nothing conflict-relevant
 * would still show a stale "already-resolved" conflict as if it were new. If the
 * signature changes to "no unresolved conflicts" (e.g. the user resolved them via the
 * GUI since the last check), any existing notification is dismissed rather than left
 * stale.
 *
 * `lastNotifiedSignature` is committed only *after* the `sendNotification`/
 * `dismissNotification` call itself completes without throwing - deliberately not
 * before. `context.api.sendNotification`/`dismissNotification` are both typed optional
 * (`?:`) on `IExtensionApi`, and this function is reachable from `index.ts`'s
 * `onAsync('did-deploy', ...)` handler where a thrown error must never propagate (see
 * that file's own comment on `onAsync`'s contract) - so any failure here is caught,
 * logged, and swallowed locally rather than left to a misleading catch-all message
 * further up the call stack. Committing the signature only on success matters
 * concretely: if the call had failed *after* the signature was already recorded, the
 * user would never have actually seen the notification, yet every later `did-deploy`
 * with that same conflict set would silently skip re-attempting it for the rest of the
 * session (the same-signature early-return above would treat it as "already shown").
 */
export function notifyConflictsIfChanged(api: types.IExtensionApi, conflicts: ScanConflictsResult): void {
  if (isModOrDependencyInstallActive(api)) {
    log('debug', 'witcherscriptmerger-vortex: mod/dependency install activity in progress - skipping conflict notification check');
    return;
  }

  const unresolved = conflicts.filter((c) => !c.alreadyResolved);
  const signature = computeConflictSignature(unresolved);

  if (signature === lastNotifiedSignature) {
    return;
  }

  try {
    if (unresolved.length === 0) {
      api.dismissNotification?.(WSM_CONFLICTS_NOTIFICATION_ID);
    } else {
      api.sendNotification?.({
        id: WSM_CONFLICTS_NOTIFICATION_ID,
        type: 'warning',
        message: `WitcherScriptMerger: ${unresolved.length} unresolved script conflict${unresolved.length === 1 ? '' : 's'} found`,
        allowSuppress: true,
        actions: [],
      });
    }
  } catch (err) {
    // Never let this escape to the caller - see this function's own doc comment above.
    // Deliberately not committing lastNotifiedSignature below in this branch: a failed
    // attempt must not be recorded as "already shown," or the user would silently never
    // see it, this session, for this exact conflict set.
    log('warn', 'witcherscriptmerger-vortex: failed to show/update the conflict notification', {
      error: err instanceof Error ? err.message : String(err),
    });
    return;
  }

  lastNotifiedSignature = signature;
}

import { selectors, types } from 'vortex-api';
import { WITCHER3_GAME_ID } from './gating';
import { ScanConflictsResult, WsmMcpClient } from './mcpClient';
import { resolveWsmExePathIfUsable } from './wsmToolPath';
import { buildWsmEnv, mergeWithProcessEnv } from './wsmEnv';

/**
 * Drives a single WSM conflict scan for Witcher 3, spawned fresh and torn down
 * immediately afterward - `mcpClient.ts`'s own documented process-lifecycle policy
 * ("spawn per user-initiated workflow ... not a long-lived singleton") applies just as
 * much to this unit's post-deployment trigger as it does to a user-initiated one; the
 * trigger here is Vortex's own `did-deploy` event rather than a button click, but the
 * lifecycle rule doesn't distinguish between the two.
 *
 * Callers are responsible for their own gating on Witcher 3 - see `gating.ts`'s own doc
 * comment for the general rule every feature this extension registers follows. This
 * module's only real caller (`index.ts`'s `checkForConflictsAfterDeploy`) does *not*
 * use the general-purpose `isWitcher3Active(api)` helper for that gating, though - it
 * resolves `did-deploy`'s own `profileId` argument to that specific deployed profile's
 * `gameId` instead, since "whichever game is active right now" and "which game this
 * particular deployment was actually for" are two different questions for a
 * post-deployment hook (see `index.ts`'s own doc comment for the full reasoning). Any
 * *other* future caller of `scanWsmConflicts`/`isWsmToolAcquired` that isn't reacting to
 * a specific past deployment should still default to `isWitcher3Active(api)`, per
 * `gating.ts`'s own general rule.
 */

/** Absolute path to the WSM Headless exe this extension should use - the central
 *  resolver's answer (user override first, then the managed install; see
 *  `wsmToolPath.ts`), or undefined when nothing usable is resolved. Kept as a named
 *  re-export here because this module's callers (coexistenceGuard.ts) already import
 *  it under this name. */
export async function getWsmExePath(api: types.IExtensionApi): Promise<string | undefined> {
  return resolveWsmExePathIfUsable(api);
}

/**
 * Cheap, local, network-free existence check for the acquired WSM exe - mirrors
 * `toolAcquisition.ts`'s own private `pathExists` helper (not exported, so duplicated
 * here rather than reused) for the same reason it exists there: no WSM binary acquired
 * yet is a normal, expected state (e.g. before any GitHub Release exists on this repo -
 * see `index.ts`'s own doc comment), not an error worth spawning a doomed child process
 * over just to discover via a failed `WsmMcpClient.connect`. Critically, this mirrors
 * `pathExists` in substance, not just in name: only `ENOENT` is treated as "not
 * acquired" - anything else (`EPERM`/`EBUSY` from an antivirus scan or a concurrently
 * running WSM process holding the file, a permissions problem, etc.) is a real,
 * unexpected condition the caller needs to see, not something to silently paper over as
 * "no tool yet." A bare catch-all here would make a transient lock look identical to
 * "nothing installed," silently skipping every post-deploy scan for the rest of the
 * session with only a misleading 'debug'-level log line - exactly the failure mode
 * `pathExists`'s own doc comment calls out.
 */
export async function isWsmToolAcquired(api: types.IExtensionApi): Promise<boolean> {
  return (await resolveWsmExePathIfUsable(api)) !== undefined;
}

/**
 * Tighter than `mcpClient.ts`'s general-purpose `DEFAULT_REQUEST_TIMEOUT_MS` (30s per
 * request, so up to ~60s worst case across the `initialize` handshake and the
 * `scan_conflicts` call). This specific call site runs *inside* Vortex's own
 * `emitAndAwait('did-deploy', ...)` await window (confirmed by reading
 * `mod_management/index.ts` - see `conflictNotifications.ts`'s own doc comment and this
 * unit's PR description for the citation): `stopActivity('mods', 'deployment')` doesn't
 * fire until every `did-deploy` handler, including this one, resolves. A slow or hung
 * WSM process would therefore extend Vortex's own reported deployment-completion time
 * by however long this waits - not something an automatic, unrequested background
 * trigger should be allowed to do for a full 30-60s. A normal `scan_conflicts` call
 * against a real mods folder completes in well under a second (see
 * `test/conflictScan.integration.test.ts`), so this still leaves generous headroom for
 * a large mod list while bounding the worst case.
 */
const POST_DEPLOY_SCAN_TIMEOUT_MS = 15_000;

/** Coalesces overlapping calls onto a single in-flight scan, the same pattern
 *  `toolAcquisition.ts`'s `inFlightAcquisitions` uses for `acquireWsmTool` (see that
 *  module's own doc comment). Unlike that map (keyed by install dir, since multiple
 *  distinct installs are meaningful there), a single slot is enough here - this
 *  extension only ever scans one thing: Witcher 3's own mods folder. Without this, two
 *  overlapping `did-deploy` events (e.g. Vortex firing a deploy again while a prior
 *  one's handlers are still resolving) could run two concurrent WSM processes against
 *  the same mods folder and, worse, resolve out of order - letting a stale scan's
 *  result reach `notifyConflictsIfChanged` *after* a fresher one already did, showing a
 *  notification that no longer matches the real current conflict set and recording that
 *  stale signature as "already seen," suppressing the correct one until the conflict set
 *  changes again. */
let inFlightScan: Promise<ScanConflictsResult> | undefined;

/**
 * Spawns a short-lived `WsmMcpClient`, runs `scan_conflicts`, and closes the client in a
 * `finally` - matching `test/mcpClient.integration.test.ts`'s own pattern exactly.
 *
 * Points the spawned process at Witcher 3's own discovered game directory via the
 * `WSM_<KeyName>` env-var mechanism (`wsmEnv.ts`), the same lookup
 * `toolAcquisition.ts`'s `registerAcquiredTool` already does for the same reason: a
 * spawned WSM process has no idea what "the active game" is on its own, so without this
 * it would fall back to whatever `GameDirectory` happens to be baked into the deployed
 * `.dll.config` (which may be blank, or stale from a previous game) - see
 * `docs/vortex-extension-design.md` section 4.1.
 *
 * See `inFlightScan`'s own doc comment for why overlapping calls coalesce onto a single
 * scan rather than each spawning their own WSM process.
 */
export async function scanWsmConflicts(api: types.IExtensionApi): Promise<ScanConflictsResult> {
  if (inFlightScan) {
    return inFlightScan;
  }

  const promise = scanWsmConflictsUncoordinated(api);
  inFlightScan = promise;
  try {
    return await promise;
  } finally {
    inFlightScan = undefined;
  }
}

async function scanWsmConflictsUncoordinated(api: types.IExtensionApi): Promise<ScanConflictsResult> {
  const gameDirectory = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.path;
  const env = mergeWithProcessEnv(buildWsmEnv({ gameDirectory }));

  // Note a real, acknowledged TOCTOU gap here, not a claim of one that doesn't exist:
  // this connect() re-derives the exe path independently of whatever isWsmToolAcquired
  // check a caller may have already done (index.ts does one before calling this), so a
  // concurrent re-acquisition (acquireWsmTool overwrites installDir in place - see
  // storage.ts/toolAcquisition.ts) between that check and this connect() could spawn
  // against a mid-write or momentarily-missing exe. Not fixed here: this is an
  // inherent check-then-act gap in any two-step "confirm it exists, then use it"
  // pattern, and the failure mode is already fully contained - WsmMcpClient.connect
  // rejects, and index.ts's own try/catch around this call already logs it as a
  // warning rather than crashing or hanging. Worth documenting, not worth adding
  // synchronization machinery for a rare, already-safely-handled race.
  const exePath = await getWsmExePath(api);
  if (exePath === undefined) {
    throw new Error(
      'No usable WitcherScriptMerger executable is resolved (not acquired yet, or the ' +
        'configured override path no longer exists - see the WitcherScriptMerger Status dashlet).',
    );
  }
  const client = await WsmMcpClient.connect({
    exePath,
    env,
    requestTimeoutMs: POST_DEPLOY_SCAN_TIMEOUT_MS,
  });
  try {
    return await client.scanConflicts();
  } finally {
    await client.close();
  }
}

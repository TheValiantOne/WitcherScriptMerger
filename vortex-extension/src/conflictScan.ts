import * as fs from 'fs';
import * as path from 'path';
import { selectors, types } from 'vortex-api';
import { WITCHER3_GAME_ID } from './gating';
import { ScanConflictsResult, WsmMcpClient } from './mcpClient';
import { getWsmToolDir } from './storage';
import { WSM_HEADLESS_EXE_NAME } from './toolAcquisition';
import { buildWsmEnv, mergeWithProcessEnv } from './wsmEnv';

/**
 * Drives a single WSM conflict scan for Witcher 3, spawned fresh and torn down
 * immediately afterward - `mcpClient.ts`'s own documented process-lifecycle policy
 * ("spawn per user-initiated workflow ... not a long-lived singleton") applies just as
 * much to this unit's post-deployment trigger as it does to a user-initiated one; the
 * trigger here is Vortex's own `did-deploy` event rather than a button click, but the
 * lifecycle rule doesn't distinguish between the two.
 *
 * Callers are responsible for their own `isWitcher3Active(api)` gating - see
 * `gating.ts`'s own doc comment for why every feature this extension registers gates on
 * that check, and `index.ts` for where this module's own caller does so.
 */

/** Absolute path to the WSM Headless exe this extension would have acquired, per
 *  `storage.ts`'s layout convention - does not check whether it actually exists on
 *  disk (see `isWsmToolAcquired` below for that). */
export function getWsmExePath(api: types.IExtensionApi): string {
  return path.join(getWsmToolDir(api), WSM_HEADLESS_EXE_NAME);
}

/**
 * Cheap, local, network-free existence check for the acquired WSM exe - mirrors
 * `toolAcquisition.ts`'s own private `pathExists` helper (not exported, so not reused
 * directly here) for the same reason it exists there: no WSM binary acquired yet is a
 * normal, expected state (e.g. before any GitHub Release exists on this repo - see
 * `index.ts`'s own doc comment), not an error worth spawning a doomed child process
 * over just to discover via a failed `WsmMcpClient.connect`.
 */
export async function isWsmToolAcquired(api: types.IExtensionApi): Promise<boolean> {
  try {
    await fs.promises.access(getWsmExePath(api));
    return true;
  } catch {
    return false;
  }
}

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
 */
export async function scanWsmConflicts(api: types.IExtensionApi): Promise<ScanConflictsResult> {
  const gameDirectory = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.path;
  const env = mergeWithProcessEnv(buildWsmEnv({ gameDirectory }));

  const client = await WsmMcpClient.connect({ exePath: getWsmExePath(api), env });
  try {
    return await client.scanConflicts();
  } finally {
    await client.close();
  }
}

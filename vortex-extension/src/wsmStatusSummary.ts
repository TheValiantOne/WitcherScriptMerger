import * as path from 'path';
import { selectors, types } from 'vortex-api';
import { DetectedBundleTools, detectBundleTools, fileExists } from './bundleTools';
import { WITCHER3_GAME_ID } from './gating';
import { GetStatusResult, WsmMcpClient } from './mcpClient';
import { getWsmToolDir } from './storage';
import { WSM_HEADLESS_EXE_NAME } from './toolAcquisition';
import { WsmEnvConfig, buildWsmEnv, mergeWithProcessEnv } from './wsmEnv';

/**
 * Pure(ish) data-fetching logic behind `statusTile.ts`'s dashlet - kept separate from
 * the React component so it's directly unit-testable (`WsmMcpClient.connect` spawns a
 * real child process, so it's injected here the same way `client`/`extractor` are
 * injected in `toolAcquisition.ts`) without needing any React/DOM test harness.
 *
 * Spawns a short-lived `WsmMcpClient` per `mcpClient.ts`'s own documented lifecycle
 * policy ("spawn per user-initiated workflow, tear down when the caller is done with
 * it") - one spawn per dashlet refresh, closed in a `finally` regardless of outcome.
 */

export type WsmStatusSummary =
  | { kind: 'not-acquired' }
  | { kind: 'error'; message: string }
  | { kind: 'ok'; status: GetStatusResult; bundleTools: DetectedBundleTools };

export interface GetWsmStatusSummaryOptions {
  /** Test-only seam - defaults to the real `WsmMcpClient.connect`. */
  connect?: typeof WsmMcpClient.connect;
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/**
 * Reports the WSM tool's dependency/status snapshot for the dashlet, or a clear reason
 * it couldn't be obtained (`not-acquired` when no WSM build has been downloaded yet at
 * all - see `toolAcquisition.ts` - versus `error` for every other failure, e.g. the
 * spawned process crashing or `get_status` itself failing).
 *
 * Builds the spawned process's environment from this unit's own bundle-tool detection
 * (`bundleTools.ts`'s `detectBundleTools`) plus the currently-discovered Witcher 3 game
 * directory, via `wsmEnv.ts`'s `buildWsmEnv`/`mergeWithProcessEnv` - the same
 * `WSM_<KeyName>` mechanism `toolAcquisition.integration.test.ts` already proves reaches
 * a real spawned WSM process, so `status.bundleDependenciesValid` reflects what this
 * extension itself has detected/acquired, not whatever `WitcherScriptMerger.Headless.
 * dll.config`'s own on-disk defaults happen to be.
 */
export async function getWsmStatusSummary(
  api: types.IExtensionApi,
  options: GetWsmStatusSummaryOptions = {},
): Promise<WsmStatusSummary> {
  const exePath = path.join(getWsmToolDir(api), WSM_HEADLESS_EXE_NAME);

  let bundleTools: DetectedBundleTools;
  let env: NodeJS.ProcessEnv;
  try {
    if (!(await fileExists(exePath))) {
      return { kind: 'not-acquired' };
    }

    bundleTools = await detectBundleTools(api);
    const gameDirectory = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.path;

    const envConfig: WsmEnvConfig = { gameDirectory, ...bundleTools };
    env = mergeWithProcessEnv(buildWsmEnv(envConfig));
  } catch (err) {
    // Covers fileExists/detectBundleTools above - this function's own doc comment
    // promises every caller a WsmStatusSummary, `not-acquired` vs `error` for every
    // *other* failure; without this, a permission/lock error scanning
    // getBundleToolsDir(api) (e.g. an AV lock on a freshly-extracted wcc_lite tree)
    // would surface as an unhandled rejection instead of the documented contract -
    // currently masked only because this module's sole wired-up caller
    // (statusTile.ts's own refresh()) happens to add its own .catch() on top.
    return { kind: 'error', message: errorMessage(err) };
  }

  const connect = options.connect ?? WsmMcpClient.connect;

  let client: WsmMcpClient;
  try {
    client = await connect({ exePath, env });
  } catch (err) {
    return { kind: 'error', message: errorMessage(err) };
  }

  try {
    const status = await client.getStatus();
    return { kind: 'ok', status, bundleTools };
  } catch (err) {
    return { kind: 'error', message: errorMessage(err) };
  } finally {
    try {
      await client.close();
    } catch {
      // A close failure must never shadow a successful getStatus() result above (a
      // `finally` block that throws replaces whatever the `try` was about to
      // return/throw) - best-effort cleanup only.
    }
  }
}

import * as path from 'path';
import { actions, types } from 'vortex-api';
import { WITCHER3_GAME_ID } from './gating';

/**
 * There is no `context.registerTool` API in `vortex-api` (re-confirmed against
 * `lib/api.d.ts` - `docs/vortex-extension-design.md` section 1) - tool discovery is
 * always `actions.addDiscoveredTool(gameId, toolId, toolDetails, isCustom)`, dispatched
 * via `api.store.dispatch(...)`, exactly like Vortex's own built-in `game-witcher3`
 * extension already registers its `W3ScriptMerger` tool (`docs/vortex-extension-design.md`
 * section 0).
 *
 * Deliberately a **different** tool ID from that one - `W3ScriptMerger` belongs to
 * `game-witcher3`, a separate extension this one is a companion to, not a replacement
 * of (see `gating.ts`'s own doc comment). There is no API to hide/disable another
 * extension's existing tool registration, so both tools coexist in Vortex's Tools
 * dashboard: `game-witcher3`'s `W3ScriptMerger` (which downloads and launches the GUI of
 * a different, older WSM fork - `IDCs/WitcherScriptMerger` - per the design doc's
 * research) and this one, clearly and distinctly labeled, pointing at a build of *this*
 * repo instead.
 */
export const WSM_TOOL_ID = 'WitcherScriptMergerEnhanced';

export interface WsmDiscoveredToolOptions {
  /** Absolute path to the acquired `WitcherScriptMerger.Headless.exe`. */
  exePath: string;
  /**
   * `WSM_<KeyName>` environment-variable overrides (see `wsmEnv.ts`'s `buildWsmEnv`) to
   * attach to this tool's registration, applied by Vortex if the user launches it
   * manually from the Tools dashboard. This is a secondary use of `buildWsmEnv`'s
   * output - the primary one, per this unit's own instructions, is passing it straight
   * into a spawned child process's `env` (`mcpClient.ts`'s `WsmMcpClientOptions.env`,
   * demonstrated in `test/toolAcquisition.integration.test.ts`), not this static field.
   */
  environment?: Record<string, string>;
}

/**
 * Builds the `IDiscoveredTool` object `registerWsmDiscoveredTool` dispatches.
 *
 * **Known, unavoidable serialization caveat, not unique to this extension:**
 * `ITool.executable` (which `IDiscoveredTool` inherits) is typed as a function
 * (`(discoveredPath?: string) => string`), and Vortex's discovered-tools state is
 * ordinarily persisted to disk across restarts. A function cannot survive a
 * `JSON.stringify` round-trip - the same shape `game-witcher3`'s own `scriptmerger.ts`
 * uses in production for `W3ScriptMerger` (per the design doc's direct source review),
 * so this isn't a novel risk this unit introduces, just an inherited one. Untested here
 * against real Vortex persistence (no real Vortex host in this repo's test setup) -
 * `discoveredTool.test.ts` instead asserts every *other* field round-trips through
 * `JSON.parse(JSON.stringify(...))` correctly, and re-registration happens on every
 * `index.ts` `context.once` regardless (see that file), which would paper over a stale
 * persisted `executable` field even if persistence does drop it.
 */
export function buildWsmDiscoveredTool(options: WsmDiscoveredToolOptions): types.IDiscoveredTool {
  return {
    id: WSM_TOOL_ID,
    name: 'WitcherScriptMerger (Enhanced)',
    shortName: 'WSM+',
    // We already know the exact acquired path - no on-disk discovery scan needed, so
    // requiredFiles (which drives that scan) is deliberately empty.
    requiredFiles: [],
    executable: () => path.basename(options.exePath),
    // `parameters: ['merge', '--overwrite']`: WitcherScriptMerger.Headless.exe with no
    // args just prints usage and exits 1 (see WitcherScriptMerger.Headless/CLAUDE.md's
    // routing section) - a human clicking this tile in Vortex's Tools dashboard used to
    // get exactly that and nothing else, which looked like the tool silently did
    // nothing (observed live: a genuine unresolved script conflict stayed unresolved
    // after "running" this tile). `merge` is the one verb that's actually useful for a
    // direct click; `--overwrite` mirrors resolveScriptConflicts.ts's own real-merge
    // call so a plain Tools-dashboard launch resolves conflicts the same way the
    // "Resolve Script Conflicts" action's confirmed merge does (including re-merging
    // stale output), not a weaker subset of it. `GameDirectory`/`ModsDirectory` still
    // come from `environment` below (the same `WSM_*` overrides `resolveScriptConflicts`
    // passes), so this works unattended without the user having configured WSM's own
    // App.config by hand. The one real difference from "Resolve Script Conflicts": no
    // preview/confirmation dialog first, and results only surface via the process exit
    // code/console output rather than a Vortex dialog - Vortex's discovered-tool
    // launcher (`api.runExecutable`) has no callback hook to reuse that dialog flow
    // itself (re-confirmed against `docs/vortex-extension-design.md` section 1: there is
    // no `context.registerTool` API, `addDiscoveredTool` + a fixed executable/parameters
    // pair is the entire mechanism).
    parameters: ['merge', '--overwrite'],
    environment: options.environment ?? {},
    path: options.exePath,
    hidden: false,
    custom: true,
    workingDirectory: path.dirname(options.exePath),
  };
}

/**
 * Dispatches `actions.addDiscoveredTool` for Witcher 3 specifically - this extension
 * never registers a tool for any other game.
 *
 * `IExtensionApi.store` is typed optional, but this extension only ever calls this from
 * inside `index.ts`'s `context.once` (or code reachable from it), by which point Vortex
 * guarantees a real store exists - a missing store there would be a genuine, unexpected
 * problem, not a normal condition to swallow. Throwing here (rather than the previous
 * `api.store?.dispatch(...)`, a silent no-op) matters concretely: `ensureWsmToolRegistered`/
 * `acquireWsmTool` (`toolAcquisition.ts`) both treat this call completing without
 * throwing as "the tool is now registered" and return `true` accordingly - a swallowed
 * no-op here would make both of those report success while nothing was actually
 * dispatched to the Redux store.
 */
export function registerWsmDiscoveredTool(api: types.IExtensionApi, tool: types.IDiscoveredTool): void {
  if (!api.store) {
    throw new Error('Cannot register the WSM discovered tool: api.store is unavailable.');
  }
  api.store.dispatch(actions.addDiscoveredTool(WITCHER3_GAME_ID, WSM_TOOL_ID, tool, true));
}

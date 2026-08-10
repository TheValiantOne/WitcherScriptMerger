import * as fs from 'fs';

// Minimal runtime stand-in for the real `vortex-api` module. At Vortex runtime, that bare
// specifier is injected by Vortex's own extension loader (see webpack.config.cjs's
// `externals` comment) - there is no installable runtime package to resolve it against.
// This stub exists purely so `vitest` (which, unlike webpack, actually executes the
// code and so must resolve every import) has something real to import; it's wired in via
// `vitest.config.ts`'s `resolve.alias` and is never part of the webpack bundle.
//
// Deliberately not an attempt to replicate Vortex's real, much larger Redux state shape
// (confirmed via the actual Vortex monorepo source that `selectors.activeGameId` really
// derives from `state.settings.profiles.activeProfileId` via a profile lookup) - tests
// using this stub exercise this extension's own gating/tool-acquisition logic
// (`gating.ts`, `toolAcquisition.ts`), not Vortex's selector implementation, so a
// simplified fake state shape is intentional here. `discoveryByGame` (real signature:
// `(state, gameId) => IDiscoveryResult`, per @nexusmods/vortex-api's own
// `re-reselect`-based `ParametricSelector` type) added alongside `toolAcquisition.ts`,
// same simplified-fake-state philosophy - deliberately keyed by gameId (unlike a
// same-shape "whichever game is active" selector) since `toolAcquisition.ts` always
// registers its tool under a fixed game id and needs that game's own discovery
// specifically, not whatever happens to be active when it runs.
export const selectors = {
  activeGameId: (state: { activeGameId?: string }): string | undefined => state?.activeGameId,
  discoveryByGame: (
    state: { discoveryByGame?: Record<string, { path?: string } | undefined> },
    gameId: string,
  ): { path?: string } | undefined => state?.discoveryByGame?.[gameId],
  // Added alongside nexusDownloader.ts (bundle-tooling acquisition): real signature
  // `(state: IState, gameId?: string) => string`, per @nexusmods/vortex-api's own
  // `lib/api.d.ts`. Deliberately keyed by an explicit fake `downloadPathForGame` map
  // (same simplified-fake-state philosophy as `discoveryByGame` above) rather than
  // Vortex's real `state.settings.downloads.path` + per-game-override resolution
  // logic - nexusDownloader.ts's own tests only need a deterministic directory to join
  // `IDownload.localPath` onto, not a faithful reimplementation of that resolution.
  downloadPathForGame: (state: { downloadPathForGame?: Record<string, string> }, gameId?: string): string =>
    state?.downloadPathForGame?.[gameId ?? ''] ?? '/unexpected/downloadPathForGame',
};

// `actions.addDiscoveredTool` needs a real (if simplified) implementation, not just a
// type, because `discoveredTool.ts`'s `registerWsmDiscoveredTool` calls it as a value at
// runtime (`api.store.dispatch(actions.addDiscoveredTool(...))`) - unlike a type-only
// import (e.g. `types`, deliberately never exported by this stub - see `gating.test.ts`'s
// own comment on why that's safe), vitest actually executes this call, so it needs
// something real to invoke. Shape matches the real `ComplexActionCreator4`'s payload
// closely enough for `discoveredTool.test.ts`'s dispatch-argument assertions; the actual
// action `type` string is never asserted on since it's an internal Vortex implementation
// detail this extension has no business depending on.
export const actions = {
  addDiscoveredTool: (gameId: string, toolId: string, result: unknown, manual: boolean) => ({
    type: 'ADD_DISCOVERED_TOOL',
    payload: { gameId, toolId, result, manual },
  }),
};

// `util.writeFileAtomic` needs a real implementation for the same reason as
// `actions.addDiscoveredTool` above - `toolAcquisition.ts` calls it as a value at
// runtime to write its installed-version marker. A plain (non-atomic) write is a
// perfectly adequate fake here: these tests exercise `toolAcquisition.ts`'s own
// orchestration, not vortex-api's atomicity guarantee, which this extension trusts
// rather than re-verifies.
export const util = {
  writeFileAtomic: async (filePath: string, input: string | Buffer): Promise<void> => {
    await fs.promises.writeFile(filePath, input);
  },
  // `util.opn` needs a real (no-op) implementation for the same reason as
  // `writeFileAtomic` above - `statusTile.ts` calls it as a value from a click handler
  // to open an external link (QuickBMS's homepage, wcc_lite's Nexus mod page) via
  // Vortex's own "open with the OS default browser" mechanism rather than a bare
  // `<a target="_blank">` (see that module's own doc comment for why). No test in this
  // repo actually clicks that link (no React/DOM rendering harness - see
  // statusTile.test.ts's own doc comment), so this is currently unexercised, but is
  // still needed for the module to *load* without an undefined `util.opn`.
  opn: async (_target: string, _wait?: boolean): Promise<void> => undefined,
};

// `log` needs a real (no-op) implementation because `index.ts` calls it as a value at
// every branch of its own logic - now that index.test.ts actually executes index.ts's
// `main()` (rather than only wiring/untested code, as when this scaffold had no real
// registration logic), this stub must resolve it rather than leaving it undefined.
export const log = (_level: string, _message: string, _metadata?: unknown): void => {
  // Intentionally a no-op in tests - nothing here asserts on log output.
};

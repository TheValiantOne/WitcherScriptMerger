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
};

// `log` needs a real (no-op) implementation because `index.ts` calls it as a value at
// every branch of its own logic - now that index.test.ts actually executes index.ts's
// `main()` (rather than only wiring/untested code, as when this scaffold had no real
// registration logic), this stub must resolve it rather than leaving it undefined.
export const log = (_level: string, _message: string, _metadata?: unknown): void => {
  // Intentionally a no-op in tests - nothing here asserts on log output.
};

// `Dashlet` needs a real (if trivial) implementation, not just a type - added alongside
// mergeHistoryDashlet.ts, which imports it as a value and passes it to
// `React.createElement` inside its own `MergeHistoryDashlet.render()`. Under real ESM
// semantics, importing a named binding a module doesn't actually export is a hard error
// at import time, even if the binding is never called at runtime - and
// mergeHistoryDashlet.test.ts's own module-level import of mergeHistoryDashlet.ts
// reaches this import whether or not any test actually renders the component (it
// doesn't - see that test file's own comment on why: no jsdom in vitest.config.ts).
// Same "real (if simplified) implementation, not type-only" reasoning as
// `actions`/`util`/`log` above. A plain function returning its own `children` prop is
// good enough here - nothing in this repo's tests ever mounts/renders a Dashlet.
export const Dashlet = (props: { className?: string; title?: string; children?: unknown }): unknown =>
  props.children ?? null;

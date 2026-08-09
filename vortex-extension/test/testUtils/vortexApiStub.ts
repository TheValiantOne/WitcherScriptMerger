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
// using this stub exercise this extension's own gating logic (`gating.ts`), not Vortex's
// selector implementation, so a simplified fake state shape is intentional here.
export const selectors = {
  activeGameId: (state: { activeGameId?: string }): string | undefined => state?.activeGameId,
};

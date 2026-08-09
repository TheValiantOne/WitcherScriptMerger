# witcherscriptmerger-vortex

A [Vortex](https://www.nexusmods.com/about/vortex/) (Nexus Mods' mod manager) companion
extension for WitcherScriptMerger (WSM). It drives WSM's `mcp` server mode (see the repo
root `CLAUDE.md` and `WitcherScriptMerger.Core/Mcp/CLAUDE.md`) from inside Vortex, as a
companion to Vortex's own built-in `game-witcher3` extension - it does **not** register
the `witcher3` game itself, and every feature it adds is gated on Witcher 3 being the
currently active game.

## This is a separate toolchain

Everything under this folder is TypeScript/Node, built with its own `package.json`,
independent of the rest of this repository's .NET solution
(`WitcherScriptMerger.sln`). `dotnet build`/`dotnet format` at the repo root never look
inside this folder, and nothing here is reachable from them.

```
cd vortex-extension
npm install
npm run build      # typecheck + webpack bundle -> dist/index.js
npm run lint
npm test           # fast, Node-only unit tests
```

`npm test` only runs the fast, Node-only unit tests (`src/**/*.test.ts`) - no .NET SDK
needed. `src/mcpClient.ts`'s real, spawned-process integration test
(`test/mcpClient.integration.test.ts`) is a separate script, `npm run test:integration`,
since it needs a local .NET SDK and a buildable `WitcherScriptMerger.Headless` (it will
run `dotnet build` itself if the exe isn't already present) - kept out of the default
`npm test` so a Node-only environment (e.g. a contributor machine or CI runner without
the .NET SDK on `PATH`) isn't forced through a multi-minute .NET build just to iterate on
this extension's own TypeScript.

## Status

This is the foundation scaffold (info.json manifest, build tooling, the `init(context)`
entry point with only game-activity gating wired up, and the shared MCP stdio client in
`src/mcpClient.ts`). No actual features - tool acquisition, conflict scanning, the merge
panel, dashlets - are implemented here; those are separate, later units built on top of
this scaffold. See `docs/vortex-extension-design.md` (once merged) for the fuller design
context this scaffold follows.

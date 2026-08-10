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
needed. The real, spawned-process integration tests are a separate script, `npm run
test:integration`, since they need a local .NET SDK and a built/published
`WitcherScriptMerger.Headless` - kept out of the default `npm test` so a Node-only
environment (e.g. a contributor machine or CI runner without the .NET SDK on `PATH`)
isn't forced through a multi-minute .NET build just to iterate on this extension's own
TypeScript. Two different `WitcherScriptMerger.Headless` invocations are involved:
`test/mcpClient.integration.test.ts` runs a plain `dotnet build` itself if the exe isn't
already present (framework-dependent, fast); `test/toolAcquisition.integration.test.ts`
instead runs `dotnet publish -c Release -p:PublishProfile=win-x64` (self-contained,
single-file, matching `.github/workflows/release.yml`'s own publish step exactly) if
that specific publish output isn't already present - slower on a cold run (produces a
~78 MB standalone exe) since it stands in for a downloaded-and-extracted release asset,
which the plain `dotnet build` output doesn't represent.

## Status

The foundation scaffold (info.json manifest, build tooling, the `init(context)` entry
point, and the shared MCP stdio client in `src/mcpClient.ts`) is in place, plus one real
feature: **tool acquisition**. `src/toolAcquisition.ts` downloads a WSM release build
from GitHub Releases, verifies/extracts it, and registers it as a discovered Vortex tool
(`src/discoveredTool.ts`, tool ID `WitcherScriptMergerEnhanced` - distinct from Vortex's
own built-in `game-witcher3` extension's `W3ScriptMerger`). `src/wsmEnv.ts` builds the
`WSM_<KeyName>` environment-variable overrides (see
`WitcherScriptMerger.Core/AppSettings.cs`) used to configure a spawned WSM process -
never by editing its `.exe.config`/`.dll.config` XML. **The actual GitHub-Releases
download path is unverified against a real release** - no version tag has been pushed to
this repo yet, so no release exists; see `src/githubRelease.ts`'s own doc comment and
this feature's own PR description for exactly what was verified instead (a mocked-HTTP
unit test for the download logic, plus a full acquisition/registration/env-var-config
integration test using a locally-built binary standing in for a downloaded one).

Conflict scanning, the merge panel, and dashlets are separate, later units not yet built
on top of this scaffold. See `docs/vortex-extension-design.md` for the fuller design
context this scaffold and the tool-acquisition unit follow.

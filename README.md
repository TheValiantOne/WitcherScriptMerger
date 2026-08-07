# Script Merger for The Witcher 3

A tool for detecting and merging conflicting Witcher 3 mod script files. It scans your
Mods folder, finds `.ws`/`.xml` files (including inside `.bundle` packages) that more
than one mod modifies, and drives a 3-way merge (vanilla + mod1 + mod2) to combine them.

This is [`TheValiantOne/WitcherScriptMerger`](https://github.com/TheValiantOne/WitcherScriptMerger),
a fork of the original [`AnotherSymbiote/WitcherScriptMerger`](https://github.com/AnotherSymbiote/WitcherScriptMerger),
mid-modernization: a .NET modernization pass, an in-process merge engine replacing the
external KDiff3 tool, a headless CLI mode, an MCP server mode, and a Linux-capable host
have all landed since the fork. See `CLAUDE.md` at the repo root (and each project's own
`CLAUDE.md`) for full architecture detail if you're contributing.

## What it does

- Checks your Mods folder for mod conflicts. Uses [QuickBMS](http://aluigi.altervista.org/quickbms.htm)
  to scan `.bundle` packages for conflicting internal content.
- Merges `.ws` scripts or `.xml` files (including those inside bundle packages) using an
  in-process 3-way merge engine built on [DiffPlex](https://github.com/mmanela/diffplex)
  — no external merge tool required. A conflict that can't be auto-solved is written to a
  conflict-marker file (git/diff3-style markers) and opened for manual review instead.
  (This fork previously used the external tool KDiff3 for this; see
  [`docs/decisions/kdiff3-retirement.md`](docs/decisions/kdiff3-retirement.md) for why it
  was retired.)
- Packages new `.bundle` packages using the official mod tool
  [wcc_lite](http://www.nexusmods.com/witcher3/news/12625/?).
- Detects updated merge source files using the [xxHash](https://github.com/Cyan4973/xxHash)
  algorithm (xxHash32), via the [`System.IO.Hashing`](https://www.nuget.org/packages/System.IO.Hashing)
  NuGet package.

## Ways to run it

There are three entry points in the Windows GUI application, plus a fourth,
Linux-capable host that drops the GUI:

- **GUI** (Windows only) — launch `WitcherScriptMerger.exe` with no arguments, or
  `dotnet run --project WitcherScriptMerger/WitcherScriptMerger.csproj`. The familiar
  point-and-click conflict tree and merge workflow.
- **Headless CLI** (Windows only, same executable) —
  `WitcherScriptMerger.exe merge [--order-file <path.json>]` merges every
  auto-solvable conflict with no window at all, then exits.
- **MCP server** (Windows only, same executable) — `WitcherScriptMerger.exe mcp` runs an
  MCP (Model Context Protocol) server over stdio, so an MCP client (e.g. a Claude Code
  session) can inspect conflicts and drive merges directly.
- **`WitcherScriptMerger.Headless`** (Windows or Linux) — a second, smaller executable
  with the same `merge` and `mcp` verbs and no GUI dependency at all, for
  CLI/agent-driven workflows on either OS. Supports flat-file (`.ws`/`.xml`) conflicts
  only — see "Dependencies" below.

## Building

```
dotnet build WitcherScriptMerger.sln
```

Single solution, four projects: `WitcherScriptMerger.Core` (shared domain logic),
`WitcherScriptMerger` (the WinForms host, all three entry points above),
`WitcherScriptMerger.Headless` (the Linux-capable CLI/MCP-only host), and
`WitcherScriptMerger.Tests`. See the root `CLAUDE.md` for the full breakdown and each
project's own `CLAUDE.md` for that project's build/run/publish details, including
self-contained single-file publish commands for both hosts (`win-x64`, plus `linux-x64`
for the headless host).

## Dependencies

**QuickBMS and wcc_lite aren't included in this source code.** Both are Windows-only
binaries with no license file in their own distribution, so they aren't committed here —
you'll need to source them separately and point `App.config`'s `QuickBmsPath`,
`QuickBmsPluginPath`, and `WccLitePath` settings at them. They're needed **only** for
`.bundle`-content conflicts; plain `.ws`/`.xml` file conflicts merge without them, on
either host.

DiffPlex, the library behind the merge engine, is MIT-licensed and pulled in as an
ordinary NuGet package — no separate download or licensing concern, unlike QuickBMS/
wcc_lite. (KDiff3, an earlier external dependency for merging, has been fully retired —
see [`docs/decisions/kdiff3-retirement.md`](docs/decisions/kdiff3-retirement.md).)

## License

Script Merger for The Witcher 3 is licensed under the **GNU General Public License v2.0**
— see [`LICENSE`](LICENSE).

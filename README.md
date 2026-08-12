# Script Merger for The Witcher 3

A tool for detecting and merging conflicting Witcher 3 mod script files. It scans your
Mods folder, finds `.ws`/`.xml` files (including inside `.bundle` packages) that more than
one mod modifies, and drives a 3-way merge (vanilla + mod1 + mod2) to combine them.

This is [`TheValiantOne/WitcherScriptMerger`](https://github.com/TheValiantOne/WitcherScriptMerger),
a fork of [`AnotherSymbiote/WitcherScriptMerger`](https://github.com/AnotherSymbiote/WitcherScriptMerger),
under an ongoing modernization pass — see below for what's changed and why.

## About this fork

The upstream tool works, and it's been the standard tool for this job for years. It's also
a single WinForms executable that shells out to an external merge binary, which makes it hard to automate,
hard to run anywhere but a Windows desktop, and dependent on a third-party tool staying
available. This fork keeps the behavior and reworks the structure.

**What's landed since the fork:**

| Change | Why |
|---|---|
| **In-process 3-way merge engine** on [DiffPlex](https://github.com/mmanela/diffplex), replacing external KDiff3 | Removes a separately-installed external binary from the critical path. Unresolvable conflicts now emit git/diff3-style marker files for manual review instead of blocking on a GUI. See [`docs/decisions/kdiff3-retirement.md`](docs/decisions/kdiff3-retirement.md). |
| **Split into `WitcherScriptMerger.Core` + hosts** | One implementation of the merge logic behind every entry point, instead of logic married to the WinForms layer. |
| **Headless CLI mode** | Merging is a batch operation. It shouldn't require a window. |
| **MCP server mode** | Lets an MCP client (e.g. a Claude Code session) enumerate conflicts and drive merges directly — conflict resolution is judgment work, and judgment work is worth handing to an agent. |
| **Cross-platform headless host** | Same `merge`/`mcp` verbs, no GUI dependency, runs on Linux. |
| **Architecture decision record** for retiring KDiff3 | The reversible-but-costly call is written down with its reasoning, not just its outcome. |

Architecture detail lives in `CLAUDE.md` at the repo root and in each project's own
`CLAUDE.md`. Start there if you're contributing.

## What it does

- Checks your Mods folder for mod conflicts. Uses [QuickBMS](http://aluigi.altervista.org/quickbms.htm)
  to scan `.bundle` packages for conflicting internal content.
- Merges `.ws` scripts or `.xml` files (including those inside bundle packages) using the
  in-process 3-way merge engine — no external merge tool required. A conflict that can't be
  auto-solved is written to a conflict-marker file and opened for manual review.
- Packages new `.bundle` packages using the official mod tool
  [wcc_lite](http://www.nexusmods.com/witcher3/news/12625/?).
- Detects updated merge source files using [xxHash](https://github.com/Cyan4973/xxHash)
  (xxHash32), via the [`System.IO.Hashing`](https://www.nuget.org/packages/System.IO.Hashing)
  NuGet package.

## Ways to run it

Three entry points in the Windows GUI application, plus a fourth, Linux-capable host that
drops the GUI:

- **GUI** (Windows) — launch `WitcherScriptMerger.exe` with no arguments, or
  `dotnet run --project WitcherScriptMerger/WitcherScriptMerger.csproj`. The familiar
  point-and-click conflict tree and merge workflow.
- **Headless CLI** (Windows, same executable) —
  `WitcherScriptMerger.exe merge [--order-file <path.json>]` merges every auto-solvable
  conflict with no window at all, then exits.
- **MCP server** (Windows, same executable) — `WitcherScriptMerger.exe mcp` runs an MCP
  (Model Context Protocol) server over stdio, so an MCP client can inspect conflicts and
  drive merges directly.
- **`WitcherScriptMerger.Headless`** (Windows or Linux) — a second, smaller executable with
  the same `merge` and `mcp` verbs and no GUI dependency, for CLI and agent-driven
  workflows on either OS. Flat-file (`.ws`/`.xml`) conflicts only — see Dependencies.

## Building

```
dotnet build WitcherScriptMerger.sln
```

One solution, four projects:

- `WitcherScriptMerger.Core` — shared domain logic
- `WitcherScriptMerger` — WinForms host, all three entry points above
- `WitcherScriptMerger.Headless` — Linux-capable CLI/MCP-only host
- `WitcherScriptMerger.Tests`

See the root `CLAUDE.md` for the full breakdown, and each project's `CLAUDE.md` for
build/run/publish details including self-contained single-file publish commands for both
hosts (`win-x64`, plus `linux-x64` for the headless host).

## Dependencies

**QuickBMS and wcc_lite aren't included in this source.** Both are Windows-only binaries
with no license file in their own distribution, so they aren't committed here — source them
separately and point `App.config`'s `QuickBmsPath`, `QuickBmsPluginPath`, and `WccLitePath`
at them. They're needed **only** for `.bundle`-content conflicts; plain `.ws`/`.xml`
conflicts merge without them, on either host.

DiffPlex, the library behind the merge engine, is MIT-licensed and pulled in as an ordinary
NuGet package — no separate download or licensing concern, unlike QuickBMS/wcc_lite.
KDiff3, the earlier external merge dependency, has been fully retired
([why](docs/decisions/kdiff3-retirement.md)).

## License

Script Merger for The Witcher 3 is licensed under the **GNU General Public License v2.0** —
see [`LICENSE`](LICENSE).

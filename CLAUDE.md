# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in
this repository. It's deliberately short: detailed, project-specific guidance lives in
each project's own `CLAUDE.md`, linked below. Read this file first for orientation, then
follow the pointer to whichever project you're actually changing.

## Project overview

Script Merger for The Witcher 3 (WSM) — a mod-script-merging tool. It scans a mod
folder, finds `.ws`/`.xml` files (including inside `.bundle` packages) that multiple mods
modify, and drives a 3-way merge (vanilla + mod1 + mod2) via an in-process,
DiffPlex-based merge engine — no external merge tool is required for flat-file
conflicts. `.bundle` package contents are unpacked with QuickBMS and repacked with
wcc_lite (external, Windows-only tools — see "External tool dependencies" below).

There are three ways to run it: a WinForms GUI, a headless CLI verb, and an MCP server
mode (so an MCP client, e.g. Claude Code, can drive merges directly). A fourth,
Linux-capable host offers the CLI and MCP modes without the GUI. See "Architecture"
below.

## Fork history

This is a fork of the upstream `AnotherSymbiote/WitcherScriptMerger` repo, currently
mid-modernization. A separate fork, `TheValiantOne/WitcherScriptMerger`, is this
project's actual `origin` remote (default branch `main`) — `upstream` is a second
configured remote pointing at `AnotherSymbiote/WitcherScriptMerger` (default branch
`master`), kept for reference/pulling upstream changes, not as a PR target.

**This distinction is load-bearing for tooling, not just trivia.** An earlier version of
this file claimed no separate fork existed and that `origin` was still the upstream repo
— false by the time it was written, and it cost a real mistake: it caused
`gh pr create` (run with no `--repo`/`--base`) to silently default to opening a PR
against `AnotherSymbiote/WitcherScriptMerger`'s `master` instead of this fork's `main`,
visible in that unrelated repo's history until closed. **Always pass
`--repo TheValiantOne/WitcherScriptMerger --base main` explicitly** (or otherwise confirm
the target) when opening a PR from this repo.

See the local, gitignored `HANDOFF.md` at the repo root (not present in a fresh clone —
it's session-continuity context, not committed) for the full rationale behind the fork
and detailed gotchas hit during the .NET modernization, if it's present in your working
copy.

## Build & run

- Build everything: `dotnet build WitcherScriptMerger.sln` from the repo root. Single
  `.sln`, four projects (see "Architecture" below) — there's no independent build for
  any one of them beyond `dotnet build <project>.csproj`.
- Test: `dotnet test WitcherScriptMerger.sln` — see `WitcherScriptMerger.Tests/CLAUDE.md`
  for what's covered and its constraints.
- Format check (required before a PR): `dotnet format whitespace WitcherScriptMerger.sln --verify-no-changes`.
- Run/publish each entry point — see that project's own `CLAUDE.md`:
  - GUI + CLI + MCP (Windows only): `WitcherScriptMerger/CLAUDE.md`.
  - CLI + MCP, Linux-capable: `WitcherScriptMerger.Headless/CLAUDE.md`.

## Architecture

Four projects, one `.sln`:

- **`WitcherScriptMerger.Core`** (`net10.0`, no WinForms reference) — all domain logic:
  file scanning, merge orchestration, load-order handling, settings/paths, the
  DiffPlex-based merge engine, and the CLI/MCP entry-point logic shared by both hosts.
  See `WitcherScriptMerger.Core/CLAUDE.md` (and `WitcherScriptMerger.Core/Mcp/CLAUDE.md`
  for the MCP tools' minimal-permissions detail specifically).
- **`WitcherScriptMerger`** (`net10.0-windows7.0`, `WinExe`) — the original WinForms
  host: GUI + CLI + MCP entry points, references Core. See `WitcherScriptMerger/CLAUDE.md`.
- **`WitcherScriptMerger.Headless`** (`net10.0`, `Exe`) — the Linux-capable CLI/MCP-only
  host, no GUI, references Core only. See `WitcherScriptMerger.Headless/CLAUDE.md`.
- **`WitcherScriptMerger.Tests`** (xunit, `net10.0`) — covers Core only. See
  `WitcherScriptMerger.Tests/CLAUDE.md`.

Domain code in Core never calls into WinForms directly — it goes through
`AppState.Notifier` (an `IMergeNotifier`, defined against neutral types so Core never
needs `System.Windows.Forms`), which is what makes both headless hosts possible. Each
host's own `CLAUDE.md` covers its own startup flow, entry-point wiring, and
verification status in detail — this file doesn't restate it.

## Architecture decisions (`docs/decisions/`)

Bigger design decisions than fit comfortably in a `CLAUDE.md` note live here as their own
documents:

- `docs/decisions/kdiff3-retirement.md` — why the external KDiff3 tool was retired in
  favor of the in-process DiffPlex-based engine, including the full empirical writeup of
  KDiff3's process behavior (window-title polling, poll-interval sensitivity, failed
  suppression attempts, unverified focus restoration) now that the code itself is gone.
- `docs/decisions/bundle-format-replacement-spike.md` — a research spike into whether
  `WolvenKit.Modkit` could replace QuickBMS/wcc_lite for `.bundle` handling
  (cross-platform, clearly licensed); no follow-on implementation was recommended.

## External tool dependencies & licensing

WSM itself is **GPLv2-licensed** — see the root `LICENSE` file.

Two bundled Windows executables are invoked via `Process.Start`, with relative paths
configured in each host's `App.config` (`QuickBmsPath`, `QuickBmsPluginPath`,
`WccLitePath`):

- **QuickBMS** (`quickbms.exe` + `witcher3.bms` plugin) — no license file found; not
  committed to source control.
- **wcc_lite** (`wcc_lite.exe`) — no license file found; not committed to source control.

Neither binary is committed to this repo (matches the original upstream project's
precedent) — keep it that way; if packaging is tackled later, it belongs in a separate
release artifact, not source control. Both are required only for `.bundle`-content
conflicts — flat-file (`.ws`/`.xml`) conflicts need neither (see
`WitcherScriptMerger.Core/CLAUDE.md`'s "Dependency validation" section).

KDiff3 (GPL-licensed, formerly a third such dependency, safe to bundle) was retired in
favor of an in-process engine built on **DiffPlex** (MIT-licensed) — an ordinary NuGet
package, not an external binary, so it carries none of the "not in source control"
concerns above. See `docs/decisions/kdiff3-retirement.md`.

Of the fork's original list of open goals, whitespace/diff-noise (see
`WitcherScriptMerger.Core/CLAUDE.md`'s "Text-merge input encoding") and a CLI mode (see
each host's own `CLAUDE.md`) are done. **Dependency-packaging/licensing — specifically,
whether/how to ship QuickBMS/wcc_lite in a release build — is still an open decision**,
not something this batch resolved; re-confirm with the repo owner before changing the
"not in source control" policy above. An MCP server mode was added afterward, beyond
that original goals list.

## Coding standards & SOP

See `CONTRIBUTING.md` for observed code style (bracing, naming, region conventions) and
repository process (branching, commit style, AI-assisted-development disclosure).

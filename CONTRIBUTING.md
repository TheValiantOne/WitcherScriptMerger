# Contributing

This is a public repository, and contributions — human or AI-agent-assisted — are welcome. See the root `CLAUDE.md` for build commands and architecture, and each project's own `CLAUDE.md` (`WitcherScriptMerger.Core/CLAUDE.md`, `WitcherScriptMerger/CLAUDE.md`, `WitcherScriptMerger.Headless/CLAUDE.md`, `WitcherScriptMerger.Tests/CLAUDE.md`) for that project's compatibility constraints; this file covers style and process. For the project's own fork lineage (this repo vs. the upstream `AnotherSymbiote/WitcherScriptMerger` project), see the root `CLAUDE.md`'s "Fork history" section.

## Code style

Match the existing source (e.g. `Inventory/FileMerger.cs`, `Controls/SMTree.cs`) rather than introducing a different style in new code. Enforced via `.editorconfig` — run `dotnet format whitespace WitcherScriptMerger.sln` before opening a PR if you're not sure your editor is honoring it:

- **Tabs, not spaces**, for indentation (`.editorconfig`: `indent_style = tab`, `indent_size = 4`).
- Allman brace style (opening brace on its own line).
- Larger classes group members under `#region Types` / `#region Members` blocks.
- Private fields are `_camelCase` with the access modifier omitted (the codebase's own history includes a commit removing unnecessary explicit access modifiers — don't reintroduce them).
- Expression-bodied members for simple, single-expression methods/properties.
- Single-statement `if` bodies are sometimes left unbraced on the following line; this isn't universal, use judgment based on surrounding code.
- `.cs` files are UTF-8 **with a BOM**, CRLF line endings — matches the existing codebase and `.editorconfig`.

## Repository SOP

- **`main` is protected.** No direct commits or pushes — all changes land via pull request. Force-pushes and branch deletion are disabled on `main` at the GitHub level.
- **Branch per feature/fix**, off `main`: `feature/<short-description>` for new functionality, `fix/<short-description>` for bug fixes, `chore/<short-description>` for tooling/process/docs changes not tied to a feature or bug. Keep the description short and kebab-case (e.g. `fix/kdiff3-encoding-mismatch`).
- **Pull requests require 2 approving reviews** before merge (GitHub branch protection on `main`). This applies to everyone, including repository admins in normal circumstances — admin bypass exists at the platform level for genuine emergencies, not as a routine shortcut.
- **PR description should cover**: what changed and why, and specifically *how you verified it* (see Testing below). "Builds successfully" is necessary but not sufficient for anything touching hash output, `MergeInventory.xml` schema, QuickBMS/wcc_lite invocation, the DiffPlex-based merge engine, or encoding handling; see `WitcherScriptMerger.Core/CLAUDE.md`'s "Hash format", "DiffPlexMergeEngine", and "Text-merge input encoding" sections for why those are load-bearing, and `WitcherScriptMerger.Tests/CLAUDE.md` for the verification pattern this codebase uses to cover them.
- Commit messages are short, descriptive sentences (e.g. `Fixed crash after canceling file-open.`, `Replace hand-ported xxHash32 with System.IO.Hashing`). A `Category:` prefix (`Fixed:`, etc.) shows up occasionally but isn't enforced. No Conventional Commits format required.
- GitHub Actions CI (`.github/workflows/build.yml`) runs `dotnet build --configuration Release` and `dotnet format whitespace --verify-no-changes` on every PR targeting `main`, but don't rely on it to catch problems for you — run both locally first: `dotnet build WitcherScriptMerger.sln --configuration Release` and `dotnet format whitespace WitcherScriptMerger.sln --verify-no-changes` before opening a PR. Catching failures before CI does saves a round trip.
- External binary dependencies (QuickBMS, wcc_lite — see the root `CLAUDE.md`'s "External tool dependencies & licensing" section) aren't in source control, so a fresh clone needs them sourced separately before the app runs end-to-end. PRs that only touch code not exercising those tools don't need them to build and review. (KDiff3 used to be a third such dependency; it was retired — see `docs/decisions/kdiff3-retirement.md`.)

## Testing

`WitcherScriptMerger.Tests` (xunit) covers `WitcherScriptMerger.Core` — see `WitcherScriptMerger.Tests/CLAUDE.md` for what it covers and its constraints. For anything not covered there — especially further hash output or `MergeInventory.xml` schema changes — use a disposable, non-committed scratch console app: exercise synthetic edge cases plus a cross-check against a real value already recorded in a live `MergeInventory.xml`. Describe what you actually ran in your PR description — see Repository SOP above.

## AI-assisted development

This repository is developed with AI coding agents (Claude Code, and expect others), openly — that's not hidden, and it's not discouraged. The federated `CLAUDE.md` files (a short root one, plus one per project) carry the operational guidance these tools use when working in this repo, kept up to date as the codebase changes; read the root one, plus whichever project's you're touching, before pointing an agent at this repo. If these guidelines are silent on something and you're using an agent, defer to the explicit rules below over whatever the agent proposes on its own.

- **Disclose it.** If a PR was substantially produced or assisted by an AI coding agent, say so in the PR description. Commits already carry a `Co-Authored-By` trailer when an agent is involved (Claude Code does this automatically) — that's necessary but not sufficient; the PR description is where a reviewer looks first.
- **You own what you submit, regardless of how it was produced.** Be able to explain any part of your own PR if a reviewer asks — "the agent wrote it that way" isn't an answer to "why does this work." If you can't explain a change, that's a signal to understand it better before submitting, not to submit it anyway.
- **The verification bar doesn't move for AI-assisted changes — if anything, hold it higher.** This codebase has a thin, Core-only test suite and several genuinely load-bearing, non-obvious compatibility constraints (hash format, text-merge input encoding normalization, the DiffPlex upstream bug `DiffPlexMergeEngine` has to defend against on every merge — all documented in `WitcherScriptMerger.Core/CLAUDE.md`). Agents are good at producing code that looks plausible and compiles; they have no way to know these constraints exist unless `CLAUDE.md` tells them, and no way to know their fix actually works unless it's actually run against real data. "Should work" is not verification — see Testing above.
- **Scrub machine-specific state before submitting.** Agent-assisted sessions tend to accumulate absolute local paths, scratch config pointing at a personal install, or test artifacts from the working process — check your diff for anything like a `G:\SteamLibrary\...`-style path or a personal game install location before opening a PR. `.gitignore` excludes common agent runtime-state directories (`.claude/`, `.cursor/`, etc.) and session handoff notes (`HANDOFF*.md`) for the same reason — extend it rather than working around it if your tool of choice uses a different local-state convention.
- **You're responsible for license compatibility of anything an agent produces**, same as for hand-written code — this project cares about this already (see the root `CLAUDE.md`'s "External tool dependencies & licensing" section on why QuickBMS/wcc_lite specifically aren't bundled). Don't accept agent output that reproduces code from a source with an incompatible license.
- **Bulk or automated PRs still go through the same process.** A large refactor being agent-generated isn't a reason to skip branch-per-change, PR review, or the two-approval requirement — if anything, larger diffs benefit more from review, not less.

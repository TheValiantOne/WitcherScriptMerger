# Contributing

Notes on how this repository is actually developed, kept close to what's observed in the codebase rather than a prescriptive ideal. See `CLAUDE.md` for build commands, architecture, and compatibility constraints for code changes — this file covers style and process.

## Code style

Observed in the existing source (e.g. `Inventory/FileMerger.cs`, `Controls/SMTree.cs`) — match it rather than introducing a different style in new code:

- Allman brace style (opening brace on its own line).
- 4-space indentation.
- Larger classes group members under `#region Types` / `#region Members` blocks.
- Private fields are `_camelCase` with the access modifier omitted (the codebase's own history includes a commit removing unnecessary explicit access modifiers — don't reintroduce them).
- Expression-bodied members for simple, single-expression methods/properties.
- Single-statement `if` bodies are sometimes left unbraced on the following line; this isn't universal, use judgment based on surrounding code.

## Repository SOP

- Single `master` branch; commits are made directly, there's no pull-request workflow in use currently.
- Commit messages are short, descriptive sentences (e.g. `Fixed crash after canceling file-open.`, `Replace hand-ported xxHash32 with System.IO.Hashing`). A `Category:` prefix (`Fixed:`, etc.) shows up occasionally but isn't enforced. No Conventional Commits format.
- No CI is configured. Build with `dotnet build WitcherScriptMerger.sln` before committing.

## Testing

There's no test project in this repo. For changes that touch hash output, `MergeInventory.xml` schema, or KDiff3 invocation, use a disposable, non-committed scratch console app: exercise synthetic edge cases plus a cross-check against a real value already recorded in a live `MergeInventory.xml`. See `CLAUDE.md`'s Tests section for the specifics of why this matters for this codebase.

## AI-assisted development

This repository is developed with Claude Code assistance, openly — that's not hidden. `CLAUDE.md` carries the operational guidance Claude Code itself uses when working in this repo. Commits authored with Claude Code carry a `Co-Authored-By` trailer identifying that.

Separately, session-scoped handoff/continuity notes (files matching `HANDOFF*.md`) are gitignored — that's for machine- and session-privacy reasons (they tend to contain local file paths and in-progress personal task context), not to obscure that AI tools are used on this project.

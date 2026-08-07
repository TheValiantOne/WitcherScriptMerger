# CLAUDE.md — Mcp/

Guidance specific to `WsmMcpTools.cs`. See `../CLAUDE.md`'s "CLI & MCP orchestration"
section for the tool list, transport rationale, and per-call state model — this file
covers only what that section doesn't: exactly what the process touches, and at what
privilege level. See each host's own `CLAUDE.md` (`WitcherScriptMerger/CLAUDE.md`,
`WitcherScriptMerger.Headless/CLAUDE.md`) for that host's own `mcp` verb startup gate,
which is not necessarily the same as the per-call gate described here.

## Minimal required permissions

- **Standard user-level file I/O — plus process spawning for conflict review.** No
  admin/elevated rights are needed to run any of the four tools, but `merge_conflicts`
  is not I/O-only: `DiffPlexMergeEngine.MergeHeadless` calls `Tools/FileOpener.Open`
  (`Process.Start` with `UseShellExecute = true`) once per genuinely-conflicting file it
  processes, launching whatever the OS has associated with that sidecar's file type —
  there is no cap, batching, or opt-out on the *number* of conflicts merged in one call
  (only `dryRun` suppresses the open entirely, see below). A `merge_conflicts` call with
  no `relativePaths` filter against a mods folder with many genuine conflicts can
  therefore open that many windows on the host desktop in one call.
- **Four filesystem roots, all ordinary user-writable locations:**
  - The configured mods directory (`Paths.ModsDirectory`) and game directory
    (`Paths.GameDirectory`) — read for scanning conflicts and vanilla/mod source files,
    write for merged output (flat-file merges land inside the mods directory; a bundle
    merge additionally repacks `blob0.bundle` there).
  - The app's own install directory — `Paths.Inventory` (`MergeInventory.xml`),
    `Paths.TempBundleContent` (`tempbundlecontent`), and `Paths.MergedBundleContent`
    (`Merged Bundle Content`) are all relative paths, resolved against
    `Environment.CurrentDirectory`, not against the mods/game tree. The WinForms host's
    `Program.RunCli` and the Headless host's `Program.Main` both pin
    `Environment.CurrentDirectory = AppContext.BaseDirectory` before dispatching to
    either the `merge` or `mcp` verb, so in practice this is always the directory the
    executable itself lives in, regardless of what directory an MCP client launches it
    from — verified empirically (a client-supplied working directory had no effect;
    `MergeInventory.xml` always landed next to the executable).
  - `Paths.DiffPlexConflictsDirectory` (`DiffPlexConflicts`, next to the executable for
    the same `Environment.CurrentDirectory` reason as the previous bullet) — every
    genuine conflict `merge_conflicts` processes writes a git/diff3-style conflict-marker
    sidecar file here (see `../CLAUDE.md`'s "DiffPlexMergeEngine (the text-merge engine)"
    section), even for a `dryRun` call. Nothing sweeps this directory automatically,
    unlike `TempBundleContent`.
- **`merge_conflicts`'s `relativePaths` and `orderOverrides` keys are validated against
  `Paths.ModsDirectory`** before any scan or merge runs (`WsmMcpTools.EnsureInScope` /
  `IsWithinModsDirectory`) — an entry that doesn't resolve inside that directory (absolute
  path, UNC path, or a `..\` escape) is rejected with a clear error rather than silently
  matching nothing or being joined into a path outside the intended scope. Neither value
  is actually joined into a filesystem path anywhere in this codebase today
  (`relativePaths` is only ever compared for equality against already-scanned
  `ModFile.RelativePath` values; `orderOverrides` values reach `Path.Combine` only after
  being validated against `ModFile.ContainsMod`, a whitelist of real scanned mod folder
  names) — this check is defense-in-depth, not a fix for a live traversal. This applies
  mods-directory-relative semantics uniformly to every category; for
  `Categories.BundleText` specifically, `conflict.RelativePath` is actually a path
  *internal to a bundle archive* (from `QuickBms.GetBundleContentPaths`), not one rooted
  at `Paths.ModsDirectory` — an ordinary internal path (e.g. `engine\foo.ws`) still
  validates fine, but a bundle whose internal listing itself contained a rooted or
  `..`-bearing entry would make that specific conflict unreachable via `relativePaths`
  (rejected as out-of-scope even though it's a legitimate conflict). Unexercised: every
  scratch config used to verify this was built with `CheckBundleContents=false`,
  consistent with the bundle path's "code-reviewed but not round-tripped" verification
  status described in each host's own `CLAUDE.md`.
- **No network access beyond the transport itself.** The MCP SDK speaks JSON-RPC over the
  process's stdin/stdout pipes to whatever spawned it (`WithStdioServerTransport()`) — that
  is the only inbound/outbound channel this process opens on its own.

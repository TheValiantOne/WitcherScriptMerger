# CLAUDE.md — Mcp/

Guidance specific to `WsmMcpTools.cs`. See the root `CLAUDE.md`'s "MCP mode" section for
the tool list, transport rationale, and per-call state model — this file covers only what
that section doesn't: exactly what the process touches, and at what privilege level.

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
    `Environment.CurrentDirectory`, not against the mods/game tree. `Program.RunCli`
    pins `Environment.CurrentDirectory = AppContext.BaseDirectory` before dispatching to
    either the `merge` or `mcp` verb, so in practice this is always the directory the
    executable itself lives in, regardless of what directory an MCP client launches it
    from — verified empirically (a client-supplied working directory had no effect;
    `MergeInventory.xml` always landed next to the executable).
  - `Paths.DiffPlexConflictsDirectory` (`DiffPlexConflicts`, next to the executable for
    the same `Environment.CurrentDirectory` reason as the previous bullet) — every
    genuine conflict `merge_conflicts` processes writes a git/diff3-style conflict-marker
    sidecar file here (see the root `CLAUDE.md`'s "Interactive vs. headless split"
    section), even for a `dryRun` call. Nothing sweeps this directory automatically,
    unlike `TempBundleContent`.
- **`merge_conflicts`'s `relativePaths` and `orderOverrides` keys are validated against
  `Paths.ModsDirectory`** before any scan or merge runs (`WsmMcpTools.EnsureInScope` /
  `IsWithinModsDirectory`) — an entry that doesn't resolve inside that directory (absolute
  path, UNC path, or a `..\` escape) is rejected with a clear error rather than silently
  matching nothing or being joined into a path outside the intended scope.
- **No network access beyond the transport itself.** The MCP SDK speaks JSON-RPC over the
  process's stdin/stdout pipes to whatever spawned it (`WithStdioServerTransport()`) — that
  is the only inbound/outbound channel this process opens on its own.

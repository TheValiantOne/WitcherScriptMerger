# Spike: Can WolvenKit replace QuickBMS + wcc_lite for `.bundle` handling?

**Status:** Researched, no follow-on implementation recommended at this time.
**Type:** Research spike (Wave 0, Unit 3) — no code changes.

## Question

WSM currently shells out to two Windows binaries with no license file in their
distribution — QuickBMS (`quickbms.exe` + `witcher3.bms`) to unpack `.bundle`
contents, and wcc_lite (`wcc_lite.exe`) to repack them and regenerate
`metadata.store` — neither of which is committed to source control (see
`CLAUDE.md`'s "External tool dependencies"). Could `WolvenKit.Modkit`, an
open-source, clearly-licensed (GPL-3.0) NuGet package from the WolvenKit
project, replace both, in-process, as a managed C# dependency?

## Current behavior (what has to be replaced)

- `WitcherScriptMerger/Tools/QuickBms.cs`: unpacks a single file with
  `quickbms.exe -Y -f "<contentRelativePath>" "<PluginPath>" "<bundlePath>" "<outputDir>"`
  and lists a bundle's contents with `quickbms.exe -l "<PluginPath>" "<bundlePath>"`,
  scraping the list-mode stdout as text (`GetBundleContentPaths`).
- `WitcherScriptMerger/Tools/WccLite.cs`: repacks with
  `wcc_lite.exe pack -dir="<sourceDir>" -outdir="<outputDir>"` and regenerates
  the archive's integrity/index file with
  `wcc_lite.exe metadatastore -path="<bundleDir>"`.
- The QuickBMS plugin script bundled with WSM, `witcher3.bms`, documents the
  `.bundle` container format in its own comments: a `POTATO70` magic header,
  a bundle size / dummy size / data-offset triple, then a table of
  0x100-byte-padded name + 16-byte hash + size/zsize/offset/timestamp +
  zip-type (0=none, 1=zlib, 2=snappy, 3=doboz, 4/5=lz4) records. Notably,
  `witcher3.bms` only covers `.bundle`/`.cache` content — it says nothing
  about `metadata.store`, which is a separate file wcc_lite alone generates.
- No `doc/`-style folder ships next to QuickBMS or wcc_lite in the local
  toolset the way KDiff3 ships `doc/options.html` — confirmed by listing the
  tool directories in a local Witcher 3 installation that had WSM's
  `Tools/` folder populated. (This environment did have that installation
  reachable; if a future reader's doesn't, the absence is otherwise
  consistent with upstream's silence on the topic.) The only proof of format
  ever having been written down at all is inside `witcher3.bms`'s comments
  and, as this spike found, independently inside WolvenKit's own source.

## Finding 1: `WolvenKit.Modkit` targets the wrong game

The task's starting premise was that `WolvenKit.Modkit` (NuGet) could be
evaluated against WSM's `.bundle` needs. It cannot — it's the wrong package
for the wrong engine generation:

- `WolvenKit.Modkit`'s own NuGet Gallery listing describes it simply as
  "Modding tools for Cyberpunk 2077," tagged `wolvenkit`, `cyberpunk2077`
  (https://www.nuget.org/packages/WolvenKit.Modkit). It belongs to the main
  `WolvenKit/WolvenKit` repository (https://github.com/WolvenKit/WolvenKit),
  which targets **REDengine 4 / Cyberpunk 2077's `.archive` format**, not
  REDengine 3 / Witcher 3's `.bundle` format.
- The tool that actually targets Witcher 3 is a *separate* repository,
  `WolvenKit/WolvenKit-7` ("WolvenKit for Witcher 3" per its own GitHub
  description, https://github.com/WolvenKit/WolvenKit-7), **created
  2021-10-04** per `gh api repos/WolvenKit/WolvenKit-7`. Confirmed via that
  same call: GPL-3.0 licensed, last pushed 2025-12-28 — roughly seven
  months before this spike (today is 2026-08-07) — 102 stars, 19 open
  issues. Seven months without a push is not "actively maintained" in any
  strong sense; treat it as "not abandoned, but not fast-moving either."
- `WolvenKit-7` does **not** publish a `Modkit`-named NuGet package (its
  README doesn't reference one, and no such package turned up in NuGet
  search). There is a Witcher-3-scoped package published under the
  WolvenKit name — `WolvenKit.RED3.CR2W` (MIT-licensed, owner "WolvenKit",
  project URL `github.com/WolvenKit/Wolven-kit`, per its NuGet Gallery page)
  — but its own description is "File formats (The Witcher 3) for the
  WolvenKit Mod Editor," it depends only on `WolvenKit.Core`/`FastMember`/
  `Newtonsoft.Json`, and neither its description nor dependency list
  mentions bundles or `metadata.store`. RED3 CR2W handles the CR2W
  *resource* file format (individual asset files) — a different format from
  the bundle *archive* container WSM needs. No `WolvenKit.RED3.Bundle`-style
  sibling package turned up in repeated NuGet search. `WolvenKit.CLI`/
  `WolvenKit.Modkit` remain the Cyberpunk-only packages.

So the honest reframe of the research question is: **does `WolvenKit-7`
(not `WolvenKit.Modkit`) expose anything usable for `.bundle` unpack/repack?**

## Finding 2: read path exists in source, but isn't a consumable library

`WolvenKit-7` does contain a pure-C# `.bundle`/`metadata.store` reader,
in a project named `WolvenKit.Bundles`
(https://github.com/WolvenKit/WolvenKit-7/tree/main/WolvenKit.Bundles):

- `Bundle.cs` has a constructor that reads `.bundle` contents directly via
  `BinaryReader`, independent of any external process.
- `Metadata_Store.cs`'s constructor (`public Metadata_Store(string filepath)`)
  fully parses `metadata.store` — header, file string table, file info list,
  file entry info list, bundle info list, buffers, dir/file init info, and
  hashes — again with a plain `BinaryReader`, no external tool. Its doc
  comment describes the file candidly: *"This game file at the root of the
  witcher3 content/ folder is used extensively by wcc_lite. It is used to
  keep track of archived files and to control their integrity."*

This read capability is real and matches the task's expectation ("very
likely" a read API exists — confirmed). But it is **not something WSM could
just add as a NuGet dependency**:

- `WolvenKit.Bundles.csproj` targets `net481` (.NET Framework 4.8.1), not
  a modern TFM compatible with WSM's `net10.0-windows7.0`.
- It references `System.Windows.Forms` and `System.ServiceModel` directly
  (legacy WinForms/WCF coupling baked into what's nominally a parsing
  library), plus a third-party `VVVV.FreeImage` reference of unclear license.
- It is not packaged/published to NuGet under any name — it's an internal
  project of the `WolvenKit-7` solution, not a reusable artifact. Consuming
  it would mean vendoring and substantially reworking that source, not
  `dotnet add package`.

## Finding 3: the write path — the actual discriminator — is unimplemented

This is the load-bearing finding for the whole spike, and it was directly
verifiable in source rather than inferred:

- GitHub issue **"Metadata.store parser" (WolvenKit/WolvenKit #33)**
  (https://github.com/WolvenKit/WolvenKit/issues/33), opened March 2017,
  states the goal plainly in its own body: *"We need a way to parse&write
  metadata.store files so we don't have to use wcc_lite for it which is
  slow"* — with an explicit two-item checklist: `- [x] Parser` and
  `- [ ] Writer`. This issue lives in the `WolvenKit/WolvenKit` repo, which
  today is the Cyberpunk-2077-only tool — but it was opened in 2017, four
  years *before* `WolvenKit-7` existed as a separate repository (created
  2021-10-04, above). In 2017 there was only one WolvenKit codebase, and it
  targeted Witcher 3; the issue is a direct historical record of that
  shared codebase's `.bundle`/`metadata.store` work, not a citation from an
  unrelated project. The issue was closed in February 2022 (`state_reason:
  "completed"`, confirmed via `gh api repos/WolvenKit/WolvenKit/issues/33`)
  — i.e. **closed, not open** — but the **Writer checkbox was never
  checked**, even at closing time. Comments on the issue (fetched via
  `gh issue view 33 --comments`) describe reverse-engineering the
  header/paths/file-record layout and confirm only the parser being
  implemented and demoed — no comment claims a working writer.
- Reading `WolvenKit-7`'s current `Metadata_Store.cs` directly confirms the
  gap still exists in the Witcher-3-specific codebase today: its `Write`
  method is a literal stub —
  ```csharp
  public void Write(string OutPutPath, params Bundle[] Bundles)
  {
      //TODO: Code this when everything is figured out.
  }
  ```
  and every constituent record type's serializer (`UBundleInfo.Serialize`,
  `UFileInfo.Serialize`, `UFileEntryInfo.Serialize`, `UDirInitInfo.Serialize`,
  `UFileInitInfo.Serialize`, `UHash.Serialize`) throws
  `NotImplementedException()`. There is a `DeserializeFromCsv` stub too,
  also `throw new NotImplementedException();` — the class can read the
  binary format and dump it to CSV for inspection, but cannot write
  `metadata.store` back out in any form.
- `Bundle.cs`'s raw `.bundle`-writing method (`public static void
  Write(string Outputpath, string rootfolder)`) does have real writing
  logic (unlike the metadata.store writer), but is marked with substantive
  open questions in its own comments: `//TODO Calculate the resulting
  bundle's size`, `//TODO: Figure out what the hell is this.` (for a
  12-byte header constant), and `//TODO: Check if the game actually cares`
  (for whether a CRC32 field is even validated by the game). This reads as
  an experimental prototype, not a proven repacker.
- Consistent with the writer never landing: `WolvenKit-7`'s own GUI pack/cook
  workflow (`WolvenKit.App/Model/WccHelper.cs`'s `Cook()` method) does not
  use `WolvenKit.Bundles` to write anything — it shells out to the real
  external tool. The wrapper class it calls,
  `WolvenKit.Common.Wcc.WccLite`, documents exactly what it is in its own
  doc comment: *"Closed-source program published by CDPR in the official
  Witcher 3 modkit. Provides a wide range of utilities, mainly
  cooking/uncooking..."* — invoked via `Process.Start` with logged
  `WCC_TASK: <args>` lines, the same architecture as WSM's own
  `Tools/WccLite.cs` today. (A real user bug report,
  https://github.com/WolvenKit/WolvenKit-7/issues/21, shows this in the
  wild: a failing `WCC_TASK: analyze r4dlc ...` invocation.)
- The only CLI surface in the repo, `WolvenKit.Console`
  (`WolvenKit.Console/Options.cs`), has no working pack/repack verb: its
  `bundle` verb is an empty stub with zero options, and the only
  metadata.store-related verb is `dumpMetadataStore` — read-only, for
  inspection.

**Conclusion on the discriminator**: neither the NuGet-published
`WolvenKit.Modkit` (wrong game/format entirely) nor `WolvenKit-7`'s
in-repo `WolvenKit.Bundles` code (right format, but an admittedly
unimplemented writer, `net481`/WinForms-coupled, and not published as a
library) can write a `.bundle` + `metadata.store` pair today. `WolvenKit-7`
itself — the actual maintained Witcher 3 tool — still depends on shelling
out to the same closed-source `wcc.exe`/`wcc_lite.exe` WSM already uses for
that half of the job.

## License analysis

WSM's own `LICENSE` is bare GNU GPL v2, **no** "or later version" clause
(confirmed by reading the file — the standard GPLv2 boilerplate at the
bottom offers the "or (at your option) any later version" language, but the
committed `LICENSE` doesn't fill that clause in as "or later"). The license
*text* lives only in `LICENSE`; checked for a separate license *grant* that
might upgrade it elsewhere, with two greps: `README.md` for the GPL-specific
terms `General Public License`/`GNU GPL`/`GPL-2`/`GPL-3`/`or later`
(case-insensitive) — zero matches — and every `.cs` file under
`WitcherScriptMerger/` for the broader terms `copyright`/`license`
(case-insensitive), which turned up exactly one file,
`Properties/AssemblyInfo.cs` — but its only relevant content is a bare
`Copyright ©  2015` string with no license grant language (it doesn't
contain any of the GPL-specific terms either). Between the two greps, no
"or later" grant exists anywhere in the repo outside `LICENSE` itself, and
`LICENSE` doesn't contain one. `WolvenKit-7` is
GPL-3.0 (confirmed via `gh api repos/WolvenKit/WolvenKit-7` →
`"license":{"key":"gpl-3.0", ...}`). GPLv3 code generally cannot be linked
into a strictly-GPLv2-only binary — the two licenses are not compatible in
that direction. Three options, as framed by the task:

**(a) Relicense WSM to GPLv2-or-later or GPLv3.** This would legally permit
linking GPL-3.0 code as a compiled-in dependency — *if* it's actually within
this project's power to do. It may not be a simple maintainer decision:
WSM is a fork of `AnotherSymbiote/WitcherScriptMerger` (per this repo's own
`CLAUDE.md`), and `Properties/AssemblyInfo.cs` carries a bare
`Copyright ©  2015`, predating this fork. Relicensing a GPLv2 codebase
generally requires consent from every copyright-holding contributor, not
just current maintainers — this spike did not attempt to identify or
contact upstream/original copyright holders, so whether relicensing is even
achievable is itself an open question, separate from whether it's worth
doing. And per Finding 3, there is currently no working `.bundle`-write /
`metadata.store`-write managed library to link in even if the license
problem were fully clear and solved — `WolvenKit-7`'s own writer is
unimplemented, and it doesn't publish `WolvenKit.Bundles` as a package
anyway. Relicensing now would mean taking on a non-trivial, possibly
infeasible legal effort (identifying and clearing consent from all
pre-fork copyright holders) that would remove a linking blocker unlocking
no practical capability today.

**(b) Shell out to a WolvenKit-provided CLI/console tool as a separate
process**, avoiding the linking question by keeping it a separate process
(same shape as today's QuickBMS/wcc_lite calls, but a clearly-licensed
dependency). This doesn't hold up either: `WolvenKit.Console` has no
pack/repack verb, and `WolvenKit-7`'s own GUI pack pipeline is itself just a
wrapper around the closed-source `wcc.exe`. Shelling out to WolvenKit-7 for
packing would mean depending on a GPL-3.0 GUI-oriented application that
*itself* still requires the user to separately provide the same
ambiguously-licensed CDPR binary WSM already needs — net new dependency
surface for zero reduction in the actual QuickBMS/wcc_lite exposure.

**(c) Keep QuickBMS/wcc_lite as a Windows-only fallback path indefinitely
and don't pursue a replacement further right now.** This is what the
evidence supports. Nothing found in this spike gives WSM a path to drop
wcc_lite for the write/repack side without either (i) still needing a
closed-source CDPR binary somewhere in the chain (whether called directly,
as today, or indirectly through a WolvenKit-7 GUI wrapper), or (ii) writing
a from-scratch `metadata.store`/`.bundle` writer that nobody has actually
finished — including WolvenKit, the most visible open-source Witcher 3
modding project, whose own tracking issue on exactly this was opened in
2017 and closed in 2022 with the writer checklist item still unchecked.

## Recommendation

**Adopt option (c).** Do not pursue WolvenKit (neither `WolvenKit.Modkit`
nor `WolvenKit-7`) as a QuickBMS/wcc_lite replacement right now, and do not
scope a follow-on implementation unit for this. The premise didn't survive
contact with the source: `WolvenKit.Modkit` targets the wrong game engine
entirely, and `WolvenKit-7` — the tool that actually targets Witcher 3 — has
an explicitly unfinished `metadata.store` writer. That's not an inference;
it's a direct read of two things: the originating GitHub issue's
`- [ ] Writer` checklist item, opened in 2017 and never checked even when
the issue was closed in 2022, and the literal `//TODO: Code this when
everything is figured out.` stub still present in `WolvenKit-7`'s current
`Metadata_Store.Write` (source pinned at commit
`c3c1c2028177de37c97a2706412b499a5c04cbf4` — see Sources). For packing,
`WolvenKit-7` depends on the very same closed-source `wcc.exe` WSM already
shells out to. Replacing WSM's ambiguous-license QuickBMS+wcc_lite pairing
with a GPL-3.0-licensed tool that still can't write the format doesn't
reduce risk or unblock anything; it adds a licensing obligation (a full WSM
relicense, for option (a)) or a new heavyweight dependency (for option (b))
in exchange for nothing beyond a marginally-better-licensed *read* path
that isn't even packaged for reuse.

One narrower, genuinely open thread worth flagging separately (**not** as
this unit's follow-on, and explicitly out of scope for a recommendation
here): two independent reverse-engineering efforts — WSM's own bundled
`witcher3.bms` and `WolvenKit-7`'s `WolvenKit.Bundles` reader — agree
closely enough on the `.bundle` container's layout that a from-scratch,
WSM-native managed reader (replacing QuickBMS's *read* path only, with no
WolvenKit dependency at all) looks plausible as a much later, separate
research question. That's a different question from "can WolvenKit replace
these tools" (this spike's actual scope), it still leaves the harder
`metadata.store`-write / `.bundle`-write problem completely unsolved, and it
shouldn't be scheduled ahead of the dependency-ordered waves already
planned. Park it; revisit only if a future spike is explicitly chartered to
ask "should WSM reimplement the bundle format itself," not "does an
existing library already do it."

## Sources consulted

- `WitcherScriptMerger/Tools/QuickBms.cs`, `WitcherScriptMerger/Tools/WccLite.cs`,
  `WitcherScriptMerger/CLAUDE.md` ("External tool dependencies"), root
  `LICENSE` — this repository, read directly.
- `witcher3.bms` (QuickBMS plugin shipped with WSM's configured tooling) —
  read directly for `.bundle` format comments.
- https://www.nuget.org/packages/WolvenKit.Modkit — package description.
- https://github.com/WolvenKit/WolvenKit — main (Cyberpunk 2077) repo.
- https://github.com/WolvenKit/WolvenKit-7 — Witcher 3 repo; fetched repo
  metadata via `gh api repos/WolvenKit/WolvenKit-7` for license/activity.
- https://github.com/WolvenKit/WolvenKit/issues/33 — "Metadata.store parser,"
  full issue body, checklist, and comments fetched via `gh issue view 33
  --repo WolvenKit/WolvenKit --comments` and `gh api
  repos/WolvenKit/WolvenKit/issues/33`.
- `WolvenKit.Bundles/Metadata_Store.cs`, `WolvenKit.Bundles/Bundle.cs`,
  `WolvenKit.Bundles/WolvenKit.Bundles.csproj`,
  `WolvenKit.Common/Model/Wcc/wcc_task.cs` (`WccLite` class),
  `WolvenKit.App/Model/WccHelper.cs`, `WolvenKit.Console/Options.cs` — all
  read directly from `WolvenKit/WolvenKit-7` via `gh api
  repos/WolvenKit/WolvenKit-7/contents/<path>`, pinned at commit
  `c3c1c2028177de37c97a2706412b499a5c04cbf4` (the ref returned by the
  `gh api search/code` calls used to locate these files) — re-verify
  against current `main` if reading this doc much later, in case the
  writer has since been implemented.
- https://github.com/WolvenKit/WolvenKit-7/issues/21 — real-world
  `WCC_TASK` shell-out failure, corroborating the external-process
  architecture.
- https://www.nuget.org/packages/WolvenKit.RED3.CR2W/3.32.3 — checked
  description, dependency list, owner, and license (MIT) to confirm it's
  scoped to the CR2W resource format only, not bundle archives, and that no
  separate `WolvenKit.Bundles`-equivalent package exists for Witcher 3.
- https://wiki.redmodding.org/wolvenkit — checked for a Witcher-3-specific
  bundle/pack documentation page; none found (its packing docs are
  Cyberpunk-2077-oriented import/export and texture-CLI pages).

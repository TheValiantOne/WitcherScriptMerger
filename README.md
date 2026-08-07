# Script Merger for The Witcher 3

I threw together this tool because I got tired of manually merging script files.

- Checks your Mods folder for mod conflicts.  Uses [QuickBMS](http://aluigi.altervista.org/quickbms.htm) to scan .bundle packages.
- Merges .ws scripts or .xml files inside bundle packages using an in-process 3-way merge engine built on [DiffPlex](https://github.com/mmanela/diffplex) — no external merge tool required. A conflict that can't be auto-solved is written to a conflict-marker file and opened for manual review instead. (This fork previously used the external tool KDiff3 for this; see `docs/decisions/kdiff3-retirement.md` for why it was retired.)
- Packages new .bundle packages using the official mod tool [wcc_lite](http://www.nexusmods.com/witcher3/news/12625/?).
- Detects updated merge source files using the [xxHash](https://github.com/Cyan4973/xxHash) algorithm by Yann Collet, [implemented in .NET](https://github.com/wilhelmliao/xxHash.NET) by Wilhelm Liao.

**QuickBMS & wcc_lite aren't included in this source code.**

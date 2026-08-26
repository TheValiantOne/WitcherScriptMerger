using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using Xunit;

namespace WitcherScriptMerger.Tests.Inventory
{
	// Deliberately narrow. Every rule in MergeInventoryHygiene that concerns a FLAT-file
	// record resolves a real path through Merge.GetMergedFile()/GetModFile(), which reads
	// Paths.ModsDirectory -> AppState.Settings, whose constructor calls
	// Environment.Exit(1) when it can't find a config file - killing the whole test host,
	// not just one test (see WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-
	// safety constraints"). So these cover exactly the branches that return before any
	// path is resolved: the bundle exemption, the null/empty guards, and the pure
	// message-formatting.
	public class MergeInventoryHygieneTests
	{
		static Merge BundleMerge(string relativePath = @"scripts\game\thing.ws") =>
			new Merge { RelativePath = relativePath, BundleName = "blob0.bundle", MergedModName = "mod0000_MergedFiles" };

		[Fact]
		public void HasMergedFile_NullMerge_ReportsPresent()
		{
			// "Can't prove it's missing" must never read as "it's missing" - a false
			// positive here would make HasResolvedConflict re-merge a healthy file.
			Assert.True(MergeInventoryHygiene.HasMergedFile(null));
		}

		// Bundle-content records are exempt on purpose: their GetMergedFile() resolves
		// under Paths.MergedBundleContent, which is working-directory-relative scratch
		// space cleared between runs, so absence there says nothing about whether the
		// merge is live - the real artifact is the packed bundle.
		[Fact]
		public void HasMergedFile_BundleContentRecord_ReportsPresentWithoutTouchingDisk()
		{
			var merge = BundleMerge();

			Assert.Equal(Categories.BundleText, merge.Category);
			Assert.True(MergeInventoryHygiene.HasMergedFile(merge));
		}

		[Fact]
		public void FindStale_NullInventory_ReturnsEmpty()
		{
			Assert.Empty(MergeInventoryHygiene.FindStale(null));
		}

		[Fact]
		public void FindStale_InventoryWithNoRecords_ReturnsEmpty()
		{
			Assert.Empty(MergeInventoryHygiene.FindStale(new MergeInventory()));
		}

		// A bundle record with no source mods exercises FindStale's loop without ever
		// reaching GetModFile - it confirms the bundle exemption holds through the
		// top-level walk, not just in HasMergedFile itself.
		[Fact]
		public void FindStale_BundleRecordWithNoMods_ReportsNothing()
		{
			var inventory = new MergeInventory();
			inventory.Merges.Add(BundleMerge());

			Assert.Empty(MergeInventoryHygiene.FindStale(inventory));
		}

		[Fact]
		public void Describe_SourceModFileMissing_NamesTheFileAndTheMod()
		{
			var record = new StaleMergeRecord(
				new Merge { RelativePath = @"game\r4Game.ws" },
				StaleMergeReason.SourceModFileMissing,
				"modGone");

			var text = record.Describe();

			Assert.Contains(@"game\r4Game.ws", text);
			Assert.Contains("modGone", text);
			Assert.Contains("no longer has this file", text);
		}

		[Fact]
		public void Describe_SourceModDisabled_SaysTheGameWouldNotLoadIt()
		{
			var record = new StaleMergeRecord(
				new Merge { RelativePath = @"game\vehicles\horse\states\exploration.ws" },
				StaleMergeReason.SourceModDisabled,
				"modFearlessRoach");

			var text = record.Describe();

			Assert.Contains("modFearlessRoach", text);
			Assert.Contains("disabled in mods.settings", text);
		}

		[Fact]
		public void StaleMergeRecord_KeepsTheMergeItDescribes()
		{
			var merge = new Merge { RelativePath = @"game\npc\npc.ws" };
			var record = new StaleMergeRecord(merge, StaleMergeReason.MergedFileMissing);

			Assert.Same(merge, record.Merge);
			Assert.Equal(StaleMergeReason.MergedFileMissing, record.Reason);
			Assert.Null(record.ModName);
		}
	}

	// ModFileIndex.ExcludeDisabledModPaths - the pure function behind the disabled-mod
	// scan filter, testable without a real mods.settings or AppState.Settings for the
	// same reason (and in the same shape) as BuildIgnoredModNames.
	public class ExcludeDisabledModPathsTests
	{
		static readonly string[] Paths =
		{
			@"G:\game\Mods\modAlwaysFullExp",
			@"G:\game\Mods\modFearlessRoach",
			@"G:\game\Mods\modImprovedHorseControls",
		};

		[Fact]
		public void ExcludesOnlyTheDisabledMod()
		{
			var kept = ModFileIndex.ExcludeDisabledModPaths(
				Paths, name => name == "modFearlessRoach", out var skipped);

			Assert.Equal(new[] { @"G:\game\Mods\modAlwaysFullExp", @"G:\game\Mods\modImprovedHorseControls" }, kept);
			Assert.Equal(new[] { "modFearlessRoach" }, skipped);
		}

		// The predicate is asked about the folder NAME - the key mods.settings uses and
		// the name the game loads by - never the full path.
		[Fact]
		public void AsksThePredicateForTheFolderNameNotTheFullPath()
		{
			var seen = new List<string>();
			ModFileIndex.ExcludeDisabledModPaths(Paths, name => { seen.Add(name); return false; }, out _);

			Assert.Equal(new[] { "modAlwaysFullExp", "modFearlessRoach", "modImprovedHorseControls" }, seen);
		}

		[Fact]
		public void NothingDisabled_KeepsEveryPathAndSkipsNothing()
		{
			var kept = ModFileIndex.ExcludeDisabledModPaths(Paths, _ => false, out var skipped);

			Assert.Equal(Paths, kept);
			Assert.Empty(skipped);
		}

		// A missing/unreadable mods.settings must exclude nobody rather than everybody -
		// otherwise a fresh install, or a Linux host with no Documents\The Witcher 3,
		// would scan zero mods and silently report zero conflicts.
		[Fact]
		public void NullPredicate_ExcludesNothing()
		{
			var kept = ModFileIndex.ExcludeDisabledModPaths(Paths, null, out var skipped);

			Assert.Equal(Paths, kept);
			Assert.Empty(skipped);
		}

		[Fact]
		public void NullPathList_ReturnsEmptyRatherThanThrowing()
		{
			var kept = ModFileIndex.ExcludeDisabledModPaths(null, _ => true, out var skipped);

			Assert.Empty(kept);
			Assert.Empty(skipped);
		}

		[Fact]
		public void SkippedNamesAreSortedForStableReporting()
		{
			var paths = new[] { @"M\modZeta", @"M\modAlpha", @"M\modMiddle" };

			ModFileIndex.ExcludeDisabledModPaths(paths, _ => true, out var skipped);

			Assert.Equal(new[] { "modAlpha", "modMiddle", "modZeta" }, skipped);
		}
	}
}

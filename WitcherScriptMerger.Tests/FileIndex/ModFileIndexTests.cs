using WitcherScriptMerger.FileIndex;
using Xunit;

namespace WitcherScriptMerger.Tests.FileIndex
{
	// Regression coverage for ModFileIndex.BuildIgnoredModNames - the mod-directory filter
	// BuildAsync applies to Directory.GetDirectories(ModsDirectory, "mod*").
	//
	// Before this, that filter honored only the IgnoreModNames setting, so the merged mod
	// (mod0000_MergedFiles by default) was scanned as an ordinary source mod: its name
	// starts with "mod", so it matches the same glob. That made every re-merge cumulative
	// rather than idempotent - the previous run's output became an input alongside the mods
	// it was built from, so each run re-applied those mods' edits on top of already-merged
	// text. Observed on a real 249-mod install: a single modBloodAndSteel insertion present
	// 6 times in actor.ws and a modCriSlowMoCR one 6 times in damageManagerProcessor.ws
	// (both appear exactly once in the mods' own files), 37 duplicated mod-added lines
	// across 11 of 42 merged files, plus one modTTMutagenSwap edit reverted outright when
	// the re-ingested output lost a most-distinct-from-vanilla tiebreak.
	//
	// These exercise the pure two-argument overload rather than GetIgnoredModNames, which
	// reads AppState.Settings - see WitcherScriptMerger.Tests/CLAUDE.md's
	// "AppState.Settings-safety constraints" for why tests must not touch that. Reaching
	// Paths.NormalizeMergedModName/Paths.MergedModNameMaxLength through it is safe for the
	// same reason those constraints exist: every static *field* initializer on Paths is
	// settings-free by design (Path.Combine/literals only - see Paths.cs's own comment on
	// why ScriptsDirectory et al. are properties, not cached fields), so touching a plain
	// string helper there can't force AppState.Settings to construct and Environment.Exit
	// the test host.
	public class ModFileIndexTests
	{
		// The bug itself: nothing configured in IgnoreModNames must still not leave the
		// merged mod in the scan.
		[Fact]
		public void BuildIgnoredModNames_NoIgnoreListConfigured_StillExcludesMergedMod()
		{
			var result = ModFileIndex.BuildIgnoredModNames("", "mod0000_MergedFiles");

			Assert.Equal(new[] { "mod0000_MergedFiles" }, result);
		}

		[Fact]
		public void BuildIgnoredModNames_NullIgnoreList_StillExcludesMergedMod()
		{
			var result = ModFileIndex.BuildIgnoredModNames(null, "mod0000_MergedFiles");

			Assert.Equal(new[] { "mod0000_MergedFiles" }, result);
		}

		// A non-default MergedModName must be honored too - the exclusion follows the
		// setting, not a hardcoded "mod0000_MergedFiles".
		[Fact]
		public void BuildIgnoredModNames_CustomMergedModName_ExcludesThatName()
		{
			var result = ModFileIndex.BuildIgnoredModNames("", "modAAA_MyMerges");

			Assert.Equal(new[] { "modAAA_MyMerges" }, result);
		}

		// The user's own IgnoreModNames entries keep working alongside the added exclusion.
		[Fact]
		public void BuildIgnoredModNames_WithIgnoreList_KeepsBothUserEntriesAndMergedMod()
		{
			var result = ModFileIndex.BuildIgnoredModNames("modFoo,modBar", "mod0000_MergedFiles");

			Assert.Equal(new[] { "modFoo", "modBar", "mod0000_MergedFiles" }, result);
		}

		[Fact]
		public void BuildIgnoredModNames_IgnoreListEntriesAreTrimmed()
		{
			var result = ModFileIndex.BuildIgnoredModNames("  modFoo , modBar  ", "mod0000_MergedFiles");

			Assert.Equal(new[] { "modFoo", "modBar", "mod0000_MergedFiles" }, result);
		}

		[Fact]
		public void BuildIgnoredModNames_BlankIgnoreListEntriesAreDropped()
		{
			var result = ModFileIndex.BuildIgnoredModNames("modFoo,,   ,modBar,", "mod0000_MergedFiles");

			Assert.Equal(new[] { "modFoo", "modBar", "mod0000_MergedFiles" }, result);
		}

		// Already listing the merged mod by hand (the pre-fix workaround) must not produce a
		// duplicate entry, and must stay case-insensitive to match BuildAsync's own
		// EqualsIgnoreCase comparison against DirectoryInfo.Name.
		[Theory]
		[InlineData("mod0000_MergedFiles")]
		[InlineData("MOD0000_MERGEDFILES")]
		[InlineData("Mod0000_mergedfiles")]
		public void BuildIgnoredModNames_MergedModAlreadyInIgnoreList_NotDuplicated(string alreadyListed)
		{
			var result = ModFileIndex.BuildIgnoredModNames(alreadyListed, "mod0000_MergedFiles");

			Assert.Single(result);
			Assert.Equal(alreadyListed, result[0]);
		}

		[Fact]
		public void BuildIgnoredModNames_MergedModAlreadyListedAmongOthers_NotDuplicated()
		{
			var result = ModFileIndex.BuildIgnoredModNames("modFoo,mod0000_MergedFiles,modBar", "mod0000_MergedFiles");

			Assert.Equal(new[] { "modFoo", "mod0000_MergedFiles", "modBar" }, result);
			Assert.Single(result, name => name.EqualsIgnoreCase("mod0000_MergedFiles"));
		}

		// An unconfigured/blank MergedModName must not add a phantom empty entry that would
		// then match nothing (or, worse, everything).
		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void BuildIgnoredModNames_NoMergedModNameConfigured_AddsNothing(string mergedModName)
		{
			var result = ModFileIndex.BuildIgnoredModNames("modFoo", mergedModName);

			Assert.Equal(new[] { "modFoo" }, result);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void BuildIgnoredModNames_NothingConfiguredAtAll_ReturnsEmpty(string mergedModName)
		{
			Assert.Empty(ModFileIndex.BuildIgnoredModNames("", mergedModName));
		}

		// Surrounding whitespace in the setting must not stop the match: the comparison is
		// against a DirectoryInfo.Name, which never carries any.
		[Fact]
		public void BuildIgnoredModNames_MergedModNamePadded_StillMatchesDirectoryName()
		{
			var result = ModFileIndex.BuildIgnoredModNames("", "  mod0000_MergedFiles  ");

			Assert.Equal(new[] { "mod0000_MergedFiles" }, result);
		}

		// A MergedModName longer than the cap is truncated when the merge writes its output
		// directory (Paths.RetrieveMergedModName), so the scan has to exclude the truncated
		// name - excluding the untruncated one would miss the directory that actually exists.
		[Fact]
		public void BuildIgnoredModNames_OverlongMergedModName_ExcludesTheTruncatedName()
		{
			var overlong = "mod" + new string('A', WitcherScriptMerger.Paths.MergedModNameMaxLength);
			var expected = overlong.Substring(0, WitcherScriptMerger.Paths.MergedModNameMaxLength);

			var result = ModFileIndex.BuildIgnoredModNames("", overlong);

			Assert.Equal(new[] { expected }, result);
			Assert.Equal(WitcherScriptMerger.Paths.MergedModNameMaxLength, result[0].Length);
		}
	}
}

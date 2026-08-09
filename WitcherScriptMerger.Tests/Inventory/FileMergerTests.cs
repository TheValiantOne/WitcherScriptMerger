using WitcherScriptMerger.Inventory;
using Xunit;

namespace WitcherScriptMerger.Tests.Inventory
{
	// Regression coverage for FileMerger.IsVanillaDlcBundleFolder - the DLC-folder-name
	// filter GetUnpackedFiles uses to find a matching vanilla bundle. A different fork of
	// this project (github.com/IDCs/WitcherScriptMerger) found and fixed two real gaps
	// here that this repo had inherited unmodified from upstream: no "bob" (Blood & Wine's
	// internal folder codename) alternative, and a case-sensitive match that only ever
	// worked by luck of Windows' case-insensitive filesystem - see
	// FileMerger.cs's own comment on VanillaDlcBundleFolderPattern and Core's CLAUDE.md.
	public class FileMergerTests
	{
		[Theory]
		[InlineData(@"C:\Witcher3\DLC\DLC1")]
		[InlineData(@"C:\Witcher3\DLC\DLC13")]
		[InlineData(@"C:\Witcher3\DLC\DLC")]
		[InlineData(@"C:\Witcher3\DLC\ep1")]
		[InlineData(@"C:\Witcher3\DLC\ep2")]
		[InlineData(@"C:\Witcher3\DLC\bob")]
		public void IsVanillaDlcBundleFolder_KnownVanillaDlcFolders_ReturnsTrue(string path)
		{
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(path));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\dlc1")]
		[InlineData(@"C:\Witcher3\DLC\Dlc1")]
		[InlineData(@"C:\Witcher3\DLC\EP1")]
		[InlineData(@"C:\Witcher3\DLC\Ep1")]
		[InlineData(@"C:\Witcher3\DLC\BOB")]
		[InlineData(@"C:\Witcher3\DLC\Bob")]
		public void IsVanillaDlcBundleFolder_DifferentCasing_StillMatches(string path)
		{
			// The prior implementation used a case-sensitive regex, which only ever worked
			// by luck of Windows' case-insensitive filesystem - would have silently never
			// matched any of these on a case-sensitive filesystem (e.g.
			// WitcherScriptMerger.Headless running on Linux).
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(path));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\some_other_mod")]
		[InlineData(@"C:\Witcher3\DLC\bobsleigh")]
		[InlineData(@"C:\Witcher3\DLC\episode1")]
		[InlineData(@"")]
		public void IsVanillaDlcBundleFolder_NonVanillaFolders_ReturnsFalse(string path)
		{
			// "bobsleigh"/"episode1" specifically confirm the pattern is anchored to the end
			// of the path (via "$") rather than matching "bob"/"ep" + a digit as a bare
			// substring anywhere earlier in a longer, unrelated folder name.
			Assert.False(FileMerger.IsVanillaDlcBundleFolder(path));
		}
	}
}

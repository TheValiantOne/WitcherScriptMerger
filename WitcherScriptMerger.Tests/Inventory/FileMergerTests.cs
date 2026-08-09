using System;
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
	//
	// The two-arg overload's own tests below additionally cover the
	// "AdditionalVanillaDlcFolderNames" App.config setting (parsed and passed in by
	// FileMerger.GetUnpackedFiles) - see IsVanillaDlcBundleFolder's own comment on why
	// this must stay a strict allowlist, never existence-based auto-discovery.
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
			// The prior implementation used a case-sensitive regex - since .NET's Regex is
			// case-sensitive by default regardless of platform, it could silently miss a real
			// vanilla DLC folder whose on-disk casing simply differs (e.g. a
			// differently-sourced or repacked install), on any platform.
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

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\ImmersiveDLC")]
		[InlineData(@"C:\Witcher3\DLC\Step1")]
		[InlineData(@"C:\Witcher3\DLC\SomeBob")]
		[InlineData(@"C:\Witcher3\DLC\PrefixDLC12")]
		public void IsVanillaDlcBundleFolder_FolderNameMerelyEndsInPattern_ReturnsFalse(string path)
		{
			// Regression test for a real bug caught in code review while adding the
			// two-arg overload below: VanillaDlcBundleFolderPattern used to be matched
			// against the full path with only an end anchor ("(DLC[0-9]*|ep[0-9]|bob)$"),
			// and .NET Regex.IsMatch has no implicit start anchor - so it matched ANY
			// folder name merely ending in one of those substrings, not just a folder
			// name that IS one of those substrings (optionally + digits). "ImmersiveDLC"
			// (ends in "DLC"), "Step1" (ends in "ep1"), "SomeBob" (ends in "bob"), and
			// "PrefixDLC12" (ends in "DLC12") would all have incorrectly matched under
			// the old pattern. This is exactly the collision this whole feature's
			// allowlist has to guard against - a Vortex "witcher3dlc"-deployed mod folder
			// with an unlucky name would have silently qualified as a vanilla merge
			// baseline. Fixed by matching a full "^...$"-anchored pattern against just the
			// extracted folder-name segment instead of an end-anchored pattern against the
			// raw path.
			Assert.False(FileMerger.IsVanillaDlcBundleFolder(path));
			Assert.False(FileMerger.IsVanillaDlcBundleFolder(path, Array.Empty<string>()));
		}

		[Fact]
		public void IsVanillaDlcBundleFolder_NullAdditionalFolderNames_ReturnsRegexResult()
		{
			// The two-arg overload's additionalFolderNames is a public parameter a caller
			// could pass null for - confirms that degrades gracefully to "regex-only"
			// instead of throwing, for both a regex-matching and a non-matching path.
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(@"C:\Witcher3\DLC\DLC1", null));
			Assert.False(FileMerger.IsVanillaDlcBundleFolder(@"C:\Witcher3\DLC\some_other_mod", null));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\DLC1")]
		[InlineData(@"C:\Witcher3\DLC\DLC13")]
		[InlineData(@"C:\Witcher3\DLC\DLC")]
		[InlineData(@"C:\Witcher3\DLC\ep1")]
		[InlineData(@"C:\Witcher3\DLC\ep2")]
		[InlineData(@"C:\Witcher3\DLC\bob")]
		public void IsVanillaDlcBundleFolder_TwoArgOverload_EmptyExtraNames_StillMatchesRegex(string path)
		{
			// The two-arg overload must keep matching everything the regex alone already
			// matches when the extra-names list is empty - i.e. adding the overload must
			// not regress the single-arg overload's existing behavior (which now forwards
			// to this one with Array.Empty<string>()).
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(path, Array.Empty<string>()));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\SongsOfThePast", "SongsOfThePast")]
		// Trailing separator on the path is trimmed before comparing the folder name.
		[InlineData(@"C:\Witcher3\DLC\SongsOfThePast\", "SongsOfThePast")]
		public void IsVanillaDlcBundleFolder_ExtraNameInAllowlist_ReturnsTrue(string path, string extraName)
		{
			// A synthetic future DLC/expansion folder name (not in the built-in regex at
			// all) matches once it's supplied via the extra-names list - the escape hatch
			// this overload exists for (e.g. CD Projekt Red's "Songs of the Past",
			// announced in 2026 with no folder codename known yet).
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(path, new[] { extraName }));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\SongsOfThePast", "songsofthepast")]
		[InlineData(@"C:\Witcher3\DLC\SONGSOFTHEPAST", "SongsOfThePast")]
		[InlineData(@"C:\Witcher3\DLC\SoNgSoFtHePaSt", "sOnGsOfThEpAsT")]
		public void IsVanillaDlcBundleFolder_ExtraNameCaseInsensitive_ReturnsTrue(string path, string extraName)
		{
			Assert.True(FileMerger.IsVanillaDlcBundleFolder(path, new[] { extraName }));
		}

		[Theory]
		[InlineData(@"C:\Witcher3\DLC\SomeUnlistedMod", new[] { "SongsOfThePast" })]
		[InlineData(@"C:\Witcher3\DLC\some_other_mod", new string[0])]
		public void IsVanillaDlcBundleFolder_ExtraNameNotInAllowlist_ReturnsFalse(string path, string[] additionalNames)
		{
			// Confirms the extra-names list is a strict allowlist, not "treat any DLC
			// subfolder as vanilla" - a folder that's neither in additionalFolderNames nor
			// matched by the built-in regex must still return false. This is the case that
			// matters most for the real-world risk this overload has to guard against:
			// Vortex's "witcher3dlc" mod type deploys ordinary user mods into the identical
			// GameDirectory\DLC\<modname>\content\... shape as real vanilla DLC content, so
			// an accidental wildcard here would risk silently merging against a mod's own
			// bundle instead of vanilla's.
			Assert.False(FileMerger.IsVanillaDlcBundleFolder(path, additionalNames));
		}
	}
}

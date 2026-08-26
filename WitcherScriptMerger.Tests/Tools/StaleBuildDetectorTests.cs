using System.Linq;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Covers StaleBuildDetector.FindMissingVanillaDeclarations only - the pure, two-string
	// core of the pre-flight check. The ModFile-taking Analyze overloads read
	// Paths.ScriptsDirectory/ModsDirectory and Paths.RetrieveMergedModName, which force
	// AppState.Settings to construct; see WitcherScriptMerger.Tests/CLAUDE.md's
	// "AppState.Settings-safety constraints" for why a test must never do that.
	public class StaleBuildDetectorTests
	{
		const string Vanilla = @"
class CR4Game extends CCommonGame
{
	public function GetExpModifier() : float
	{
		return 1.0f;
	}

	event OnHDRChangedEvent()
	{
		GetGuiManager().OnHDRChanged();
	}

	event OnUserSignedOut()
	{
		isSignedIn = false;
	}
}
";

		// The real modAlwaysFullExp shape: a whole-file copy from an older game build, so
		// the event the newer vanilla added simply isn't there.
		const string StaleModCopy = @"
class CR4Game extends CCommonGame
{
	public function GetExpModifier() : float
	{
		return 1.0f;
	}

	event OnUserSignedOut()
	{
		isSignedIn = false;
	}
}
";

		[Fact]
		public void FindMissingVanillaDeclarations_ModCopyFromOlderBuild_ReportsTheMissingDeclaration()
		{
			var missing = StaleBuildDetector.FindMissingVanillaDeclarations(Vanilla, StaleModCopy);

			Assert.Equal(new[] { "CR4Game::OnHDRChangedEvent" }, missing);
		}

		[Fact]
		public void FindMissingVanillaDeclarations_ModCopyMatchesVanilla_ReportsNothing()
		{
			Assert.Empty(StaleBuildDetector.FindMissingVanillaDeclarations(Vanilla, Vanilla));
		}

		// The check is deliberately one-directional. A mod ADDING declarations is the
		// entire point of a mod; only vanilla content the mod's copy lacks is evidence
		// that the copy predates the installed game build.
		[Fact]
		public void FindMissingVanillaDeclarations_ModAddsDeclarations_ReportsNothing()
		{
			var modded = Vanilla.Replace(
				"	event OnUserSignedOut()",
				"	public function ModAddedHelper() : bool\r\n	{\r\n		return true;\r\n	}\r\n\r\n	event OnUserSignedOut()");

			Assert.Empty(StaleBuildDetector.FindMissingVanillaDeclarations(Vanilla, modded));
		}

		// Scoped, not bare, names - so a method removed from one class isn't masked by a
		// same-named method surviving in another. This is the identity
		// FunctionLevelMergeEngine's own invariant reports, which is what lets a
		// pre-flight warning and a post-hoc violation name the same thing.
		[Fact]
		public void FindMissingVanillaDeclarations_SameNameInAnotherClass_DoesNotMaskTheLoss()
		{
			const string vanilla = @"
class CR4Game extends CCommonGame
{
	event OnHDRChangedEvent()
	{
		GetGuiManager().OnHDRChanged();
	}
}

class CR4MapMenu extends CR4MenuBase
{
	event OnHDRChangedEvent()
	{
		DoSomethingElse();
	}
}
";
			const string mod = @"
class CR4Game extends CCommonGame
{
}

class CR4MapMenu extends CR4MenuBase
{
	event OnHDRChangedEvent()
	{
		DoSomethingElse();
	}
}
";
			var missing = StaleBuildDetector.FindMissingVanillaDeclarations(vanilla, mod);

			Assert.Equal(new[] { "CR4Game::OnHDRChangedEvent" }, missing);
		}

		[Fact]
		public void FindMissingVanillaDeclarations_MultipleMissing_ReportsAllOrdered()
		{
			const string vanilla = @"
class CR4MapMenu extends CR4MenuBase
{
	private function SetInitialFilters()
	{
	}

	event OnFiltersChanged(id : int)
	{
	}

	public function Keep()
	{
	}
}
";
			const string mod = @"
class CR4MapMenu extends CR4MenuBase
{
	public function Keep()
	{
	}
}
";
			var missing = StaleBuildDetector.FindMissingVanillaDeclarations(vanilla, mod);

			Assert.Equal(
				new[] { "CR4MapMenu::OnFiltersChanged", "CR4MapMenu::SetInitialFilters" },
				missing.OrderBy(n => n).ToArray());
		}

		// An unscannable file must produce NO finding. Returning an empty set from the
		// extractor instead would make every vanilla declaration look missing and turn
		// one malformed mod file into a wall of false "older game build" warnings.
		[Fact]
		public void FindMissingVanillaDeclarations_UnscannableModText_ReportsNothing()
		{
			Assert.Empty(StaleBuildDetector.FindMissingVanillaDeclarations(Vanilla, "class Broken {"));
		}

		[Fact]
		public void FindMissingVanillaDeclarations_UnscannableVanillaText_ReportsNothing()
		{
			Assert.Empty(StaleBuildDetector.FindMissingVanillaDeclarations("class Broken {", Vanilla));
		}

		[Fact]
		public void Describe_NamesTheModTheFileAndTheRemedy()
		{
			var finding = new StaleBuildFinding(
				@"game\r4Game.ws", "modAlwaysFullExp", new[] { "CR4Game::OnHDRChangedEvent" }, 42);

			var text = finding.Describe();

			Assert.Contains("modAlwaysFullExp", text);
			Assert.Contains(@"game\r4Game.ws", text);
			Assert.Contains("CR4Game::OnHDRChangedEvent", text);
			Assert.Contains("older game version", text);
			Assert.Contains("disable it", text);

			// The check runs before any merge, so it must not assert an outcome it can't
			// know - a small drift often merges cleanly. It reports the drift and the
			// usual consequence, never a verdict on this specific conflict.
			Assert.DoesNotContain("can't be auto-merged", text);
			Assert.Contains("usual reason", text);
		}

		// Long lists get truncated so a badly out-of-date mod doesn't print hundreds of
		// names, but the true total still has to be visible.
		[Fact]
		public void Describe_ManyMissingDeclarations_TruncatesNamesButKeepsTheCount()
		{
			var names = Enumerable.Range(0, StaleBuildDetector.MaxNamesInDescription + 4)
				.Select(i => $"CR4Game::Fn{i}")
				.ToArray();
			var finding = new StaleBuildFinding(@"game\r4Game.ws", "modStale", names, 99);

			var text = finding.Describe();

			Assert.Contains($"missing {names.Length} of the 99 declaration(s)", text);
			Assert.Contains("+4 more", text);
			Assert.DoesNotContain($"CR4Game::Fn{names.Length - 1}", text);
		}
	}
}

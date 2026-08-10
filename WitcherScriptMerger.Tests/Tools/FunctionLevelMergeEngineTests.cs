using System.Linq;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Regression coverage for FunctionLevelMergeEngine (see
	// WitcherScriptMerger.Core/CLAUDE.md once this lands there, and the plan this
	// class was built from). Fixtures use small synthetic WitcherScript-shaped
	// excerpts, never literal game script text (copyrighted) - only structurally
	// similar stand-ins for the real collision shapes this engine was built to
	// handle.
	public class FunctionLevelMergeEngineTests
	{
		const string OldLabel = "modA";
		const string NewLabel = "modB";
		const string OldDesc = "modA";
		const string NewDesc = "modB";

		static string Fn(string name, string body) =>
			"function " + name + "()\r\n{\r\n" + body + "}\r\n";

		static FunctionLevelMergeResult Merge(string baseText, string oldText, string newText) =>
			FunctionLevelMergeEngine.TryMerge(baseText, oldText, newText, OldLabel, NewLabel, OldDesc, NewDesc);

		[Fact]
		public void TryMerge_BothSidesUnchanged_KeepsVanillaTextForEveryFunction()
		{
			var baseText = Fn("A", "\tx = 1;\r\n") + Fn("B", "\ty = 2;\r\n");

			var result = Merge(baseText, baseText, baseText);

			Assert.True(result.Applied);
			Assert.Equal(baseText, result.MergedText);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_OnlyOldSideChangedOneFunction_KeepsOldVersionNoBuildMergeNeeded()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 2;\r\n");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Equal(oldText, result.MergedText);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_OnlyNewSideChangedOneFunction_KeepsNewVersion()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var newText = Fn("A", "\tx = 3;\r\n");

			var result = Merge(baseText, baseText, newText);

			Assert.True(result.Applied);
			Assert.Equal(newText, result.MergedText);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_BothSidesMadeIdenticalEdit_KeepsItOnceNoDecisionNote()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var sameEdit = Fn("A", "\tx = 9;\r\n");

			var result = Merge(baseText, sameEdit, sameEdit);

			Assert.True(result.Applied);
			Assert.Equal(sameEdit, result.MergedText);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_NonOverlappingEditsWithinFunction_CleanlyMergedByBuildMerge()
		{
			var baseText = "function A()\r\n{\r\n\ta();\r\n\tb();\r\n\tc();\r\n}\r\n";
			var oldText = "function A()\r\n{\r\n\ta();\r\n\tMOD1();\r\n\tb();\r\n\tc();\r\n}\r\n";
			var newText = "function A()\r\n{\r\n\ta();\r\n\tb();\r\n\tc();\r\n\tMOD2();\r\n}\r\n";

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Contains("MOD1();", result.MergedText);
			Assert.Contains("MOD2();", result.MergedText);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_GenuineCollision_TiebreakPicksMoreDistinctSideAndRecordsDecision()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 2;\r\n"); // 1 changed line vs. vanilla
			var newText = Fn("A", "\tx = 2;\r\n\ty = 3;\r\n\tz = 4;\r\n"); // 3 changed lines vs. vanilla

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Equal(newText, result.MergedText);
			var note = Assert.Single(result.Decisions);
			Assert.Contains("modB", note);
			Assert.Contains("more distinct", note);
		}

		[Fact]
		public void TryMerge_GenuineCollisionExactTie_FallsBackDeterministicallyToOldSide()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 2;\r\n");
			var newText = Fn("A", "\tx = 3;\r\n");

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Equal(oldText, result.MergedText);
			var note = Assert.Single(result.Decisions);
			Assert.Contains("modA", note);
		}

		[Fact]
		public void TryMerge_DeletedOnOldSideOnly_NewSideAlsoUnchanged_FunctionDroppedGapsGetCaveatNotes()
		{
			var baseText = Fn("A", "\tx = 1;\r\n") + Fn("B", "\ty = 2;\r\n");
			var oldText = Fn("B", "\ty = 2;\r\n"); // deleted A entirely, didn't touch B

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.DoesNotContain("function A", result.MergedText);
			Assert.Contains("function B", result.MergedText);
			// A's deletion makes both neighboring gap slots (before A, between A and B)
			// ineligible for precise comparison - each gets a conservative caveat note
			// rather than silence, since non-function content near a deletion has no
			// other signal at all (see GetGapEligibility.IneligibleDeletion).
			Assert.Equal(2, result.Decisions.Count);
			Assert.All(result.Decisions, d => Assert.Contains("wasn't automatically verified", d));
		}

		[Fact]
		public void TryMerge_EditSurvivesCompetingDeletion_KeepsEditAndRecordsDecision()
		{
			var baseText = Fn("A", "\tx = 1;\r\n") + Fn("B", "\ty = 2;\r\n");
			var oldText = Fn("B", "\ty = 2;\r\n"); // old deleted A
			var newText = Fn("A", "\tx = 99;\r\n") + Fn("B", "\ty = 2;\r\n"); // new edited A

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Contains("x = 99;", result.MergedText);
			var deletionNote = Assert.Single(result.Decisions, d => d.Contains("deleted"));
			Assert.Contains("modB", deletionNote);
			// Plus the same two ineligible-deletion caveat notes as the case above -
			// A's deletion on the old side still makes its neighboring gaps
			// unverifiable regardless of how the function itself was resolved.
			Assert.Equal(3, result.Decisions.Count);
		}

		[Fact]
		public void TryMerge_DeletedOnBothSides_FunctionDroppedGapsGetCaveatNotes()
		{
			var baseText = Fn("A", "\tx = 1;\r\n") + Fn("B", "\ty = 2;\r\n");
			var bothDeleteA = Fn("B", "\ty = 2;\r\n");

			var result = Merge(baseText, bothDeleteA, bothDeleteA);

			Assert.True(result.Applied);
			Assert.DoesNotContain("function A", result.MergedText);
			Assert.Equal(2, result.Decisions.Count);
			Assert.All(result.Decisions, d => Assert.Contains("wasn't automatically verified", d));
		}

		[Fact]
		public void TryMerge_NoDeletionsOrInsertionsAnywhere_NoIneligibleGapCaveatNotes()
		{
			// Sanity check for the caveat-note feature itself: a file where nothing is
			// ever deleted or inserted should never emit an "wasn't automatically
			// verified" caveat - only real, deletion-adjacent uncertainty should.
			var baseText = Fn("A", "\tx = 1;\r\n") + Fn("B", "\ty = 2;\r\n");
			var oldText = Fn("A", "\tx = 9;\r\n") + Fn("B", "\ty = 2;\r\n");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.DoesNotContain(result.Decisions, d => d.Contains("wasn't automatically verified"));
		}

		[Fact]
		public void TryMerge_InsertionOnOldSideOnly_IsKept()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 1;\r\n") + Fn("NewFunc", "\tz = 1;\r\n");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Contains("function NewFunc", result.MergedText);
		}

		[Fact]
		public void TryMerge_IdenticalInsertionOnBothSides_KeptOnce()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var withNewFunc = Fn("A", "\tx = 1;\r\n") + Fn("NewFunc", "\tz = 1;\r\n");

			var result = Merge(baseText, withNewFunc, withNewFunc);

			Assert.True(result.Applied);
			var occurrences = result.MergedText.Split(new[] { "function NewFunc" }, System.StringSplitOptions.None).Length - 1;
			Assert.Equal(1, occurrences);
		}

		[Fact]
		public void TryMerge_SameNameDifferentBodyInsertionOnBothSides_DeclinesWholeFile()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 1;\r\n") + Fn("NewFunc", "\tz = 1;\r\n");
			var newText = Fn("A", "\tx = 1;\r\n") + Fn("NewFunc", "\tz = 2;\r\n"); // same name, different body

			var result = Merge(baseText, oldText, newText);

			Assert.False(result.Applied);
		}

		[Fact]
		public void TryMerge_DuplicateNamedInsertionsOnOneSide_DeclinesRatherThanThrowing()
		{
			// Two insertions with the SAME name on one side (e.g. a mod's own
			// copy-paste mistake) used to throw from ReconcileInsertions's
			// ToDictionary call - regression test for that crash (caught upstream by
			// DiffPlexMergeEngine.TryFunctionLevelRescue's bare catch, but this engine
			// should decline cleanly on its own, not rely on a caller's safety net).
			var baseText = Fn("A", "\tx = 1;\r\n");
			var oldText = Fn("A", "\tx = 1;\r\n") + Fn("Dup", "\ty = 1;\r\n") + Fn("Dup", "\ty = 2;\r\n");

			var result = Merge(baseText, oldText, baseText);

			Assert.False(result.Applied);
		}

		[Fact]
		public void TryMerge_VanillaHasNoExtractedUnitsAtAll_DeclinesRatherThanDiscardingBothEdits()
		{
			// A file with zero functions/@addField fields (e.g. only top-level
			// consts/enums) has nothing for a FUNCTION-level engine to offer - the
			// whole document is one gap, and reverting a whole file's real,
			// substantive edits to vanilla while reporting it as a successful
			// AutoSolved merge would be a materially worse outcome than declining.
			// Also a regression test for a real crash this case used to trigger
			// (GetSideGapIndex indexing MatchedSideIndex[-1] on an empty array).
			var baseText = "const X = 1;\r\n";
			var oldText = "const X = 2;\r\n";
			var newText = "const X = 3;\r\n";

			var result = Merge(baseText, oldText, newText);

			Assert.False(result.Applied);
		}

		[Fact]
		public void TryMerge_ExtractionFailsOnAnySide_Declines()
		{
			var baseText = Fn("A", "\tx = 1;\r\n");
			var unbalanced = "function A()\r\n{\r\n\tx = 1;\r\n"; // never closes

			var result = Merge(baseText, unbalanced, baseText);

			Assert.False(result.Applied);
		}

		[Fact]
		public void TryMerge_GapCommentDifference_NotedButVanillaGapTextKept()
		{
			var baseText = Fn("A", "\treturn;\r\n") + "\r\n// vanilla comment\r\n\r\n" + Fn("B", "\treturn;\r\n");
			var oldText = Fn("A", "\treturn;\r\n") + "\r\n// modA's own comment here\r\n\r\n" + Fn("B", "\treturn;\r\n");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Contains("vanilla comment", result.MergedText);
			Assert.DoesNotContain("modA's own comment", result.MergedText);
			var note = Assert.Single(result.Decisions);
			Assert.Contains("modA", note);
		}

		[Fact]
		public void TryMerge_GapWhitespaceOnlyDifference_NoDecisionNote()
		{
			var baseText = Fn("A", "\treturn;\r\n") + "\r\n\r\n" + Fn("B", "\treturn;\r\n");
			var oldText = Fn("A", "\treturn;\r\n") + "\r\n\r\n\r\n" + Fn("B", "\treturn;\r\n"); // extra blank line only

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Empty(result.Decisions);
		}

		[Fact]
		public void TryMerge_FunctionLevelDiffAlgorithmException_FallsBackToTiebreakRatherThanDeclining()
		{
			// Same interleaved-edit shape DiffPlexMergeEngineTests uses to trigger the
			// confirmed upstream DiffPlex bug, scaled down to function size: one side
			// inserts a line right after "a();", the other independently changes
			// "b()" to "B()".
			var baseText = Fn("A", "\ta();\r\n\tb();\r\n\tc();\r\n");
			var oldText = Fn("A", "\ta();\r\n\tnewline();\r\n\tb();\r\n\tc();\r\n");
			var newText = Fn("A", "\ta();\r\n\tB();\r\n\tc();\r\n");

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Single(result.Decisions);
		}
	}
}

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

		#region Gap-handling v2 (docs/bugs/function-level-merge-gap-handling.md)

		// Defect 1's exact shape: a mod appends new members at the END of a class, so
		// its insertions align to the slot between vanilla's last class member and the
		// next global-scope unit - and vanilla's gap at that slot contains the
		// class-closing brace. The old emission appended the inserted units AFTER that
		// brace (global scope, "'public' has no sense for global function ...") with
		// their separators eaten. The fix emits the inserting side's own span, keeping
		// both position and separators.
		[Fact]
		public void TryMerge_ModAppendsMembersAtClassEnd_InsertedInsideClassNotAfterClosingBrace()
		{
			var baseText =
				"class C\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n\r\n" +
				"exec function E()\r\n{\r\n\treturn;\r\n}\r\n";
			var oldText =
				"class C\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\tfunction B()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\tprivate var voiceLast : float;\r\n}\r\n\r\n" +
				"exec function E()\r\n{\r\n\treturn;\r\n}\r\n";

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			// The inserting side's span is emitted verbatim, so with the other side
			// untouched the whole file should equal the inserting side's own text.
			Assert.Equal(oldText, result.MergedText);
			// Belt and braces: the inserted declaration sits BEFORE the class-closing
			// brace, and nothing got mangled onto a brace line.
			var closingBrace = result.MergedText.IndexOf("\r\n}", System.StringComparison.Ordinal);
			Assert.True(result.MergedText.IndexOf("voiceLast", System.StringComparison.Ordinal) < result.MergedText.LastIndexOf("}\r\n\r\nexec", System.StringComparison.Ordinal));
			Assert.True(FunctionLevelMergeEngine.PassesReassemblySanityGate(result.MergedText, out _));
		}

		// Defect 2's exact shape: one mod adds a plain member declaration (previously
		// gap content, silently reverted to vanilla), the other edits a different
		// function. Both changes must survive.
		[Fact]
		public void TryMerge_OneModAddsMemberDeclarationOtherEditsFunction_BothSurvive()
		{
			var baseText =
				"class C\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\tfunction B()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";
			var oldText =
				"class C\r\n{\r\n\tprivate var mCS : int;\r\n\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\tfunction B()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";
			var newText =
				"class C\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\tfunction B()\r\n\t{\r\n\t\tx = 2;\r\n\t}\r\n}\r\n";

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Contains("private var mCS : int;", result.MergedText);
			Assert.Contains("x = 2;", result.MergedText);
			Assert.True(FunctionLevelMergeEngine.PassesReassemblySanityGate(result.MergedText, out _));
		}

		// A mod CHANGING a default value (not just adding one) now resolves through
		// per-unit resolution: only one side touched it, so that side wins - no
		// vanilla-gap revert, no note needed.
		[Fact]
		public void TryMerge_OneModChangesDefaultValue_ChangeSurvives()
		{
			var baseText = "class C\r\n{\r\n\tvar d : float;\r\n\tdefault d = 4.5f;\r\n\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n";
			var oldText = baseText.Replace("default d = 4.5f;", "default d = 9.0f;");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Contains("default d = 9.0f;", result.MergedText);
			Assert.DoesNotContain("4.5f", result.MergedText);
		}

		[Fact]
		public void TryMerge_BothSidesInsertAtSameGlobalSlot_BothEmittedOnSeparateLines()
		{
			var baseText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("B", "\tb();\r\n");
			var oldText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("X", "\tx();\r\n") + "\r\n" + Fn("B", "\tb();\r\n");
			var newText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("Y", "\ty();\r\n") + "\r\n" + Fn("B", "\tb();\r\n");

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Contains("function X()", result.MergedText);
			Assert.Contains("function Y()", result.MergedText);
			// Never two declarations run together on one line.
			Assert.DoesNotContain("}function", result.MergedText);
			Assert.True(FunctionLevelMergeEngine.PassesReassemblySanityGate(result.MergedText, out _));
		}

		// Both sides inserting into a slot whose vanilla gap carries a structural brace
		// (a class boundary) has no safe placement answer - decline rather than guess.
		[Fact]
		public void TryMerge_BothSidesInsertAtClassBoundarySlot_Declines()
		{
			var baseText =
				"class C\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n\r\n" +
				"exec function E()\r\n{\r\n\treturn;\r\n}\r\n";
			var oldText = baseText.Replace(
				"\t}\r\n}",
				"\t}\r\n\r\n\tfunction FromOld()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}");
			var newText = baseText.Replace(
				"\t}\r\n}",
				"\t}\r\n\r\n\tfunction FromNew()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}");

			var result = Merge(baseText, oldText, newText);

			Assert.False(result.Applied);
		}

		[Fact]
		public void PassesReassemblySanityGate_MemberShapedDeclarationAtGlobalScope_Fails()
		{
			var text =
				"class C\r\n{\r\n\tvar x : int;\r\n}\r\n\r\n" +
				"\tpublic function Orphaned() : float\r\n\t{\r\n\t\treturn 1;\r\n\t}\r\n";

			Assert.False(FunctionLevelMergeEngine.PassesReassemblySanityGate(text, out var reason));
			Assert.Contains("global scope", reason);
		}

		// The bug's exact mangled shape: a declaration glued onto the tail of the
		// class-closing-brace line ("}<TAB>private var q : int;").
		[Fact]
		public void PassesReassemblySanityGate_DeclarationMangledOntoClosingBraceLine_Fails()
		{
			var text = "class C\r\n{\r\n\tvar x : int;\r\n}\tprivate var q : int;\r\n";

			Assert.False(FunctionLevelMergeEngine.PassesReassemblySanityGate(text, out var reason));
			Assert.Contains("global scope", reason);
		}

		[Fact]
		public void PassesReassemblySanityGate_ValidGlobalScopeShapes_Pass()
		{
			var text =
				"statemachine class CR4Player extends CPlayer\r\n{\r\n\tprivate var x : int;\r\n\tdefault x = 1;\r\n}\r\n\r\n" +
				"exec function foo()\r\n{\r\n\tvar local : int;\r\n\tlocal = 1;\r\n}\r\n\r\n" +
				"function globalHelper() : bool\r\n{\r\n\treturn true;\r\n}\r\n";

			Assert.True(FunctionLevelMergeEngine.PassesReassemblySanityGate(text, out _));
		}

		[Fact]
		public void PassesReassemblySanityGate_UnbalancedBraces_Fails()
		{
			Assert.False(FunctionLevelMergeEngine.PassesReassemblySanityGate("class C\r\n{\r\n\tvar x : int;\r\n", out var reason));
			Assert.Contains("unbalanced", reason);
		}

		#endregion

		#region Chain-step validation & global insertion reconciliation (round 2)

		// The re-merge shape that produced a real, live duplication: the accumulated
		// side (a previous full merge) already contains the function a later chain
		// step's mod inserts - at a DIFFERENT position than the mod's own copy, so
		// per-slot reconciliation alone never sees the pair.
		[Fact]
		public void TryMerge_SameInsertionAtDifferentSlotsOnBothSides_EmitsExactlyOneCopy()
		{
			var baseText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("B", "\tb();\r\n") + "\r\n" + Fn("C", "\tc();\r\n");
			var newFn = Fn("NEW", "\tn();\r\n");
			var oldText = Fn("A", "\ta();\r\n") + "\r\n" + newFn + "\r\n" + Fn("B", "\tb();\r\n") + "\r\n" + Fn("C", "\tc();\r\n");
			var newText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("B", "\tb();\r\n") + "\r\n" + newFn + "\r\n" + Fn("C", "\tc();\r\n");

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result.MergedText, @"function NEW\(").Count);
			Assert.True(FunctionLevelMergeEngine.PassesReassemblySanityGate(result.MergedText, out _));
		}

		// Whitespace-only differences between the two sides' copies of the same
		// insertion resolve like the whole-file engine's whitespace-only conflicts:
		// take one side (the accumulated side's copy), never both, never a decline.
		[Fact]
		public void TryMerge_SameInsertionDifferingOnlyInWhitespace_KeepsOldSideCopy()
		{
			var baseText = Fn("A", "\ta();\r\n");
			var oldText = Fn("A", "\ta();\r\n") + "\r\n" + "function NEW()\r\n{\r\n\tn();\r\n}\r\n";
			var newText = Fn("A", "\ta();\r\n") + "\r\n" + "function NEW()\r\n{\r\n\tn();  \r\n}\r\n";

			var result = Merge(baseText, oldText, newText);

			Assert.True(result.Applied);
			Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result.MergedText, @"function NEW\(").Count);
			Assert.Contains("\tn();\r\n}", result.MergedText);
		}

		[Fact]
		public void TryMerge_SameInsertionWithDifferentContent_StillDeclines()
		{
			var baseText = Fn("A", "\ta();\r\n");
			var oldText = baseText + "\r\n" + Fn("NEW", "\tfromOld();\r\n");
			var newText = baseText + "\r\n" + Fn("NEW", "\tfromNew();\r\n");

			var result = Merge(baseText, oldText, newText);

			Assert.False(result.Applied);
		}

		// An upstream silently-corrupted accumulated file can carry byte-identical
		// duplicate insertions; refusing the whole file over copies that agree helps
		// no one - they collapse to one.
		[Fact]
		public void TryMerge_IdenticalDuplicateInsertionsOnOneSide_CollapseToOne()
		{
			var baseText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("B", "\tb();\r\n");
			var dup = Fn("NEW", "\tn();\r\n");
			var oldText = Fn("A", "\ta();\r\n") + "\r\n" + dup + "\r\n" + Fn("B", "\tb();\r\n") + "\r\n" + dup;

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result.MergedText, @"function NEW\(").Count);
		}

		[Fact]
		public void ValidateWholeFileMergeOutput_DetectsSilentDuplication()
		{
			var baseText = Fn("A", "\ta();\r\n");
			var oldText = baseText + "\r\n" + Fn("NEW", "\tn();\r\n");
			var corrupted = baseText + "\r\n" + Fn("NEW", "\tn();\r\n") + "\r\n" + Fn("NEW", "\tn();\r\n");

			Assert.False(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(baseText, oldText, baseText, corrupted, "x.ws", out var violation));
			Assert.Contains("duplicated", violation);
		}

		[Fact]
		public void ValidateWholeFileMergeOutput_DetectsSilentLossOfAnInsertion()
		{
			var baseText = Fn("A", "\ta();\r\n");
			var oldText = baseText + "\r\n" + Fn("NEW", "\tn();\r\n");

			Assert.False(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(baseText, oldText, baseText, baseText, "x.ws", out var violation));
			Assert.Contains("lost", violation);
		}

		[Fact]
		public void ValidateWholeFileMergeOutput_AllowsALegitimateDeletionPropagating()
		{
			var baseText = Fn("A", "\ta();\r\n") + "\r\n" + Fn("B", "\tb();\r\n");
			var oldText = baseText;
			var newText = Fn("A", "\ta();\r\n");
			var mergedText = Fn("A", "\ta();\r\n");

			Assert.True(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(baseText, oldText, newText, mergedText, "x.ws", out _));
		}

		[Fact]
		public void ValidateWholeFileMergeOutput_NonScriptFiles_AlwaysTrusted()
		{
			Assert.True(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput("<a/>", "<b/>", "<c/>", "<d/>", "x.xml", out _));
		}

		// The combat.ws shape: a clean-looking splice duplicated a LOCAL declaration
		// inside a function body - invisible to unit-level counts, fatal at compile
		// ("Variable 'mCSMCR' is already defined").
		[Fact]
		public void ValidateWholeFileMergeOutput_DetectsDuplicatedLocalVarInsideAFunction()
		{
			var baseText = Fn("A", "\tvar x : int;\r\n\tx = 1;\r\n");
			var oldText = Fn("A", "\tvar x : int;\r\n\tvar mCS : CCS;\r\n\tx = 1;\r\n");
			var corrupted = Fn("A", "\tvar x : int;\r\n\tvar mCS : CCS;\r\n\tvar mCS : CCS;\r\n\tx = 1;\r\n");

			Assert.False(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(baseText, oldText, baseText, corrupted, "x.ws", out var violation));
			Assert.Contains("mCS", violation);
		}

		// An input that itself declares a local twice is tolerated when the merge
		// carries it through unchanged - the invariant only flags duplication the
		// merge INTRODUCED.
		[Fact]
		public void ValidateWholeFileMergeOutput_ToleratesPreexistingDuplicateLocalFromAnInput()
		{
			var baseText = Fn("A", "\tvar x : int;\r\n");
			var oldText = Fn("A", "\tvar x : int;\r\n\tvar mCS : CCS;\r\n\tvar mCS : CCS;\r\n");

			Assert.True(FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(baseText, oldText, baseText, oldText, "x.ws", out _));
		}

		// The hud.ws shape: one mod extends a vanilla enum with a new member while the
		// other side leaves it untouched - the enum is a unit now, so the extending
		// side's whole block wins instead of vanilla's gap text silently reverting it.
		[Fact]
		public void TryMerge_OneModAddsAnEnumMember_AdditionSurvives()
		{
			var baseText =
				"enum EVis\r\n{\r\n\tHVS_None,\r\n\tHVS_Combat\r\n}\r\n\r\n" +
				Fn("A", "\ta();\r\n");
			var oldText = baseText.Replace("\tHVS_Combat\r\n}", "\tHVS_Combat,\r\n\tHVS_Modcrab\r\n}");

			var result = Merge(baseText, oldText, baseText);

			Assert.True(result.Applied);
			Assert.Contains("HVS_Modcrab", result.MergedText);
		}

		[Fact]
		public void HasDuplicatedLocalVarDecls_IgnoresCommentedOutDeclarations()
		{
			var merged = Fn("A", "\tvar x : int;\r\n\t// var x : int;\r\n");
			var input = Fn("A", "\tvar x : int;\r\n");

			Assert.False(FunctionLevelMergeEngine.HasDuplicatedLocalVarDecls(merged, input, input, input, out _));
		}

		#endregion
	}
}

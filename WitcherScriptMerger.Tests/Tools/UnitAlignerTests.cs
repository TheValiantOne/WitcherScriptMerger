using System.Collections.Generic;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Regression coverage for UnitAligner - the function-level merge engine's
	// vanilla-vs-one-side alignment (see WitcherScriptMerger.Core/CLAUDE.md once this
	// lands there). Builds ScriptUnit fixtures directly via its public constructor
	// (offsets/FullText are irrelevant to alignment, which only reads Name) rather than
	// running the real extractor - keeps these fixtures focused purely on the
	// alignment algorithm.
	public class UnitAlignerTests
	{
		static ScriptUnit Unit(string name) =>
			new ScriptUnit(name, ScriptUnitKind.Function, hasBody: true, startOffset: 0, endOffset: 0, fullText: name);

		static List<ScriptUnit> Units(params string[] names)
		{
			var list = new List<ScriptUnit>();
			foreach (var name in names)
				list.Add(Unit(name));
			return list;
		}

		[Fact]
		public void Align_IdenticalSequences_EveryUnitMatchedNoInsertionsOrDeletions()
		{
			var vanilla = Units("A", "B", "C");
			var side = Units("A", "B", "C");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(new int?[] { 0, 1, 2 }, alignment.MatchedSideIndex);
			foreach (var slot in alignment.InsertionsAtSlot)
				Assert.Empty(slot);
		}

		[Fact]
		public void Align_SideDeletesOneVanillaUnit_ThatUnitIsUnmatched()
		{
			var vanilla = Units("A", "B", "C");
			var side = Units("A", "C");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(0, alignment.MatchedSideIndex[0]);
			Assert.Null(alignment.MatchedSideIndex[1]);
			Assert.Equal(1, alignment.MatchedSideIndex[2]);
		}

		[Fact]
		public void Align_SideDeletesAllVanillaUnits_EveryUnitIsUnmatched()
		{
			var vanilla = Units("A", "B", "C");
			var side = Units();

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.All(alignment.MatchedSideIndex, m => Assert.Null(m));
		}

		[Fact]
		public void Align_SideInsertsOneNewUnit_AttributedToCorrectSlot()
		{
			var vanilla = Units("A", "B", "C");
			var side = Units("A", "NEW", "B", "C");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(new int?[] { 0, 2, 3 }, alignment.MatchedSideIndex);
			Assert.Equal(new[] { 1 }, alignment.InsertionsAtSlot[1]); // slot 1 = between vanilla[0] and vanilla[1]
			Assert.Empty(alignment.InsertionsAtSlot[0]);
			Assert.Empty(alignment.InsertionsAtSlot[2]);
			Assert.Empty(alignment.InsertionsAtSlot[3]);
		}

		[Fact]
		public void Align_SideInsertsAtStartAndEnd_AttributedToOuterSlots()
		{
			var vanilla = Units("A", "B");
			var side = Units("BEFORE", "A", "B", "AFTER");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(new[] { 0 }, alignment.InsertionsAtSlot[0]);
			Assert.Equal(new[] { 3 }, alignment.InsertionsAtSlot[2]);
		}

		[Fact]
		public void Align_MultipleInsertionsAtSameSlot_PreserveRelativeOrder()
		{
			var vanilla = Units("A", "B");
			var side = Units("A", "X", "Y", "Z", "B");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(new[] { 1, 2, 3 }, alignment.InsertionsAtSlot[1]);
		}

		[Fact]
		public void Align_SimultaneousInsertionAndDeletion_BothHandledIndependently()
		{
			var vanilla = Units("A", "B", "C");
			var side = Units("A", "NEW", "C"); // deletes B, inserts NEW before C

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Equal(0, alignment.MatchedSideIndex[0]);
			Assert.Null(alignment.MatchedSideIndex[1]); // B deleted
			Assert.Equal(2, alignment.MatchedSideIndex[2]);
			Assert.Equal(new[] { 1 }, alignment.InsertionsAtSlot[2]); // slot before vanilla[2] = "C"
		}

		[Fact]
		public void Align_EmptyVanilla_EverySideUnitIsAnInsertionAtSlotZero()
		{
			var vanilla = Units();
			var side = Units("A", "B");

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.Empty(alignment.MatchedSideIndex);
			Assert.Equal(new[] { 0, 1 }, alignment.InsertionsAtSlot[0]);
		}

		[Fact]
		public void Align_EmptySide_EveryVanillaUnitIsDeletedNoInsertions()
		{
			var vanilla = Units("A", "B");
			var side = Units();

			var alignment = UnitAligner.Align(vanilla, side);

			Assert.All(alignment.MatchedSideIndex, m => Assert.Null(m));
			foreach (var slot in alignment.InsertionsAtSlot)
				Assert.Empty(slot);
		}
	}
}

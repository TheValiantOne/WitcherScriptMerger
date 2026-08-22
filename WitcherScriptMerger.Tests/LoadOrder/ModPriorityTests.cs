using System;
using System.Collections.Generic;
using WitcherScriptMerger.LoadOrder;
using Xunit;

namespace WitcherScriptMerger.Tests.LoadOrder
{
	// Coverage for ModPriority - the user-supplied mod ranking that decides whose version of
	// a function survives when two mods edit it differently, at the point
	// FunctionLevelMergeEngine would otherwise fall back to its most-distinct-from-vanilla
	// tiebreak.
	//
	// Deliberately a different lever from FileMerger.ResolveMergeOrder's per-file order
	// override: that sets the chain ORDER and must name every mod for the file it covers.
	// This one reorders nothing and is partial by design - a pair where neither side is
	// ranked must behave exactly as it did before the ranking existed.
	//
	// Pure static over plain strings: no filesystem, no AppState - see
	// WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety constraints".
	public class ModPriorityTests
	{
		static readonly string[] Ranking = { "modWinner", "modMiddle", "modLoser" };

		[Fact]
		public void Resolve_NoRanking_HasNoOpinion()
		{
			Assert.Equal(PreferredSide.None, ModPriority.Resolve(null, new[] { "modA" }, "modB"));
			Assert.Equal(PreferredSide.None, ModPriority.Resolve(new string[0], new[] { "modA" }, "modB"));
		}

		// The whole point of "partial rankings allowed": adding a ranking must not change the
		// outcome of a conflict it doesn't mention.
		[Fact]
		public void Resolve_NeitherSideRanked_HasNoOpinion()
		{
			Assert.Equal(PreferredSide.None, ModPriority.Resolve(Ranking, new[] { "modUnknownA" }, "modUnknownB"));
		}

		[Fact]
		public void Resolve_HigherRankedOnNewSide_PrefersNew()
		{
			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, new[] { "modLoser" }, "modWinner"));
		}

		[Fact]
		public void Resolve_HigherRankedOnOldSide_PrefersOld()
		{
			Assert.Equal(PreferredSide.Old, ModPriority.Resolve(Ranking, new[] { "modWinner" }, "modLoser"));
		}

		// A ranked mod beats an unranked one - that's what ranking it means.
		[Fact]
		public void Resolve_RankedBeatsUnranked_EitherSide()
		{
			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, new[] { "modUnranked" }, "modMiddle"));
			Assert.Equal(PreferredSide.Old, ModPriority.Resolve(Ranking, new[] { "modMiddle" }, "modUnranked"));
		}

		// The accumulated side carries every mod merged into it so far, so it takes the BEST
		// rank among them. Without this a highly-ranked mod would stop winning the moment one
		// more mod merged on top of it - the opposite of what ranking it means.
		[Fact]
		public void Resolve_AccumulatedSideTakesItsBestRank()
		{
			var accumulated = new[] { "modUnranked", "modWinner", "modLoser" };

			Assert.Equal(PreferredSide.Old, ModPriority.Resolve(Ranking, accumulated, "modMiddle"));
		}

		[Fact]
		public void Resolve_AccumulatedSideAllUnranked_LosesToARankedNewMod()
		{
			var accumulated = new[] { "modUnrankedA", "modUnrankedB" };

			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, accumulated, "modLoser"));
		}

		[Fact]
		public void Resolve_IsCaseInsensitive()
		{
			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, new[] { "MODLOSER" }, "modwinner"));
		}

		[Fact]
		public void Resolve_SameModOnBothSides_HasNoOpinion()
		{
			Assert.Equal(PreferredSide.None, ModPriority.Resolve(Ranking, new[] { "modWinner" }, "modWinner"));
		}

		[Fact]
		public void Resolve_EmptyOrNullAccumulatedSide_StillRanksTheNewMod()
		{
			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, null, "modWinner"));
			Assert.Equal(PreferredSide.New, ModPriority.Resolve(Ranking, new string[0], "modWinner"));
		}

		#region Order-file plumbing

		[Fact]
		public void ExtractRanking_NoReservedEntry_ReturnsNull()
		{
			var overrides = new Dictionary<string, string[]> { [@"game\actor.ws"] = new[] { "modA", "modB" } };

			Assert.Null(ModPriority.ExtractRanking(overrides));
		}

		[Fact]
		public void ExtractRanking_ReturnsTheReservedEntry()
		{
			var overrides = new Dictionary<string, string[]>
			{
				[ModPriority.OrderFileKey] = new[] { "modWinner", "modLoser" },
				[@"game\actor.ws"] = new[] { "modA", "modB" },
			};

			Assert.Equal(new[] { "modWinner", "modLoser" }, ModPriority.ExtractRanking(overrides));
		}

		// A ranking is advisory - a stray blank in a hand-edited order file trims away
		// rather than failing a whole merge run.
		[Fact]
		public void ExtractRanking_DropsBlankEntriesAndTrims()
		{
			var overrides = new Dictionary<string, string[]>
			{
				[ModPriority.OrderFileKey] = new[] { "  modWinner  ", "", "   ", "modLoser" },
			};

			Assert.Equal(new[] { "modWinner", "modLoser" }, ModPriority.ExtractRanking(overrides));
		}

		[Fact]
		public void ExtractRanking_EmptyRanking_ReturnsNull()
		{
			var overrides = new Dictionary<string, string[]> { [ModPriority.OrderFileKey] = new string[0] };

			Assert.Null(ModPriority.ExtractRanking(overrides));
		}

		[Fact]
		public void ExtractRanking_NullRanking_ReturnsNull()
		{
			var overrides = new Dictionary<string, string[]> { [ModPriority.OrderFileKey] = null };

			Assert.Null(ModPriority.ExtractRanking(overrides));
		}

		// ResolveMergeOrder looks entries up by relative path; the reserved key must never
		// reach it, or a file literally named "*" would be the only thing standing between a
		// ranking and a bogus "unknown mod" failure.
		[Fact]
		public void WithoutRankingEntry_RemovesOnlyTheReservedKey()
		{
			var overrides = new Dictionary<string, string[]>
			{
				[ModPriority.OrderFileKey] = new[] { "modWinner" },
				[@"game\actor.ws"] = new[] { "modA", "modB" },
			};

			var stripped = ModPriority.WithoutRankingEntry(overrides);

			Assert.False(stripped.ContainsKey(ModPriority.OrderFileKey));
			Assert.Equal(new[] { "modA", "modB" }, stripped[@"game\actor.ws"]);
			Assert.Single(stripped);
		}

		// Regression: a plain ToDictionary rebuilds with the default ordinal comparer and
		// silently undoes the case-insensitive one WsmMcpTools.MergeConflicts deliberately
		// installs, turning correctly-but-differently-cased path keys into no-ops. Only
		// reachable once a ranking is supplied, so it would have hidden until the feature
		// was actually used.
		[Fact]
		public void WithoutRankingEntry_PreservesACaseInsensitiveKeyComparer()
		{
			var overrides = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
			{
				[ModPriority.OrderFileKey] = new[] { "modWinner" },
				[@"game\actor.ws"] = new[] { "modA", "modB" },
			};

			var stripped = ModPriority.WithoutRankingEntry(overrides);

			Assert.True(stripped.ContainsKey(@"GAME\ACTOR.WS"));
		}

		// ...and does not IMPOSE one where the caller never asked for it: the CLI hosts build
		// their order dictionary with the default comparer, and lookups there must stay exactly
		// as case-sensitive as they were before a ranking existed.
		[Fact]
		public void WithoutRankingEntry_DoesNotImposeACaseInsensitiveComparer()
		{
			var overrides = new Dictionary<string, string[]>
			{
				[ModPriority.OrderFileKey] = new[] { "modWinner" },
				[@"game\actor.ws"] = new[] { "modA", "modB" },
			};

			var stripped = ModPriority.WithoutRankingEntry(overrides);

			Assert.False(stripped.ContainsKey(@"GAME\ACTOR.WS"));
			Assert.True(stripped.ContainsKey(@"game\actor.ws"));
		}

		[Fact]
		public void WithoutRankingEntry_NoReservedKey_ReturnsInputUnchanged()
		{
			var overrides = new Dictionary<string, string[]> { [@"game\actor.ws"] = new[] { "modA", "modB" } };

			Assert.Same(overrides, ModPriority.WithoutRankingEntry(overrides));
		}

		[Fact]
		public void WithoutRankingEntry_Null_IsTolerated()
		{
			Assert.Null(ModPriority.WithoutRankingEntry(null));
		}

		#endregion
	}
}

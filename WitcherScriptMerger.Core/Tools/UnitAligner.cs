using System;
using System.Collections.Generic;

namespace WitcherScriptMerger.Tools
{
	// The result of aligning one side's (old's or new's) ScriptUnit sequence against
	// vanilla's, by name, for the function-level merge engine (see
	// FunctionLevelMergeEngine's own header comment).
	public sealed class UnitAlignment
	{
		// One entry per vanilla unit, in vanilla order: the matching index into this
		// side's own unit list, or null if this side deleted that vanilla unit
		// entirely (present in vanilla, absent here).
		public IReadOnlyList<int?> MatchedSideIndex { get; }

		// vanillaUnitCount + 1 entries, one per gap slot (0 = before vanilla's first
		// unit, i = between vanilla unit i-1 and i, last = after vanilla's last unit).
		// Each entry lists this side's own unit indices that don't correspond to any
		// vanilla unit at all (this side's insertions), in their original relative
		// order, attributed to the slot they fall between.
		public IReadOnlyList<IReadOnlyList<int>> InsertionsAtSlot { get; }

		public UnitAlignment(IReadOnlyList<int?> matchedSideIndex, IReadOnlyList<IReadOnlyList<int>> insertionsAtSlot)
		{
			MatchedSideIndex = matchedSideIndex;
			InsertionsAtSlot = insertionsAtSlot;
		}
	}

	// Aligns one side's extracted units against vanilla's, by scoped name, so
	// FunctionLevelMergeEngine can tell - per vanilla unit - whether each side kept
	// it unchanged, edited it, or deleted it outright, and which units on each side are
	// wholly new (not in vanilla at all). Matching uses ScriptUnit.ScopedName
	// ("CR4Player::mCSMCR"), not the bare Name: member-declaration names recur across
	// the multiple classes a real .ws file contains (function names were measured not
	// to collide, but member names like "owner" absolutely do), so bare-name identity
	// would mis-align a member of one class against a same-named member of another.
	// This is a longest-common-subsequence alignment on those scoped-name tokens -
	// standard LCS DP, not a heuristic. A file that somehow did have duplicate scoped
	// names still produces *a* valid LCS, just not necessarily the one a human would
	// consider "obviously correct" - a reasonable degradation, not a crash.
	public static class UnitAligner
	{
		public static UnitAlignment Align(IReadOnlyList<ScriptUnit> vanillaUnits, IReadOnlyList<ScriptUnit> sideUnits)
		{
			var n = vanillaUnits.Count;
			var m = sideUnits.Count;

			// dp[i, j] = length of the LCS of vanillaUnits[i..n) and sideUnits[j..m).
			// Sized (n+1) x (m+1) so dp[n, *] and dp[*, m] (the "nothing left" base
			// case) are always in bounds without a separate edge check.
			var dp = new int[n + 1, m + 1];
			for (var i = n - 1; i >= 0; --i)
			{
				for (var j = m - 1; j >= 0; --j)
				{
					dp[i, j] = vanillaUnits[i].ScopedName == sideUnits[j].ScopedName
						? dp[i + 1, j + 1] + 1
						: Math.Max(dp[i + 1, j], dp[i, j + 1]);
				}
			}

			var matched = new int?[n];
			var insertionsAtSlot = new List<int>[n + 1];
			for (var s = 0; s <= n; ++s)
				insertionsAtSlot[s] = new List<int>();

			// Standard LCS backtrack, walking forward (not the more common
			// backward-from-(0,0) direction) since dp was built with the "suffix LCS
			// length" convention above - equivalent, just avoids reversing the result
			// afterward.
			var vi = 0;
			var si = 0;
			while (vi < n && si < m)
			{
				if (vanillaUnits[vi].ScopedName == sideUnits[si].ScopedName)
				{
					matched[vi] = si;
					++vi;
					++si;
				}
				else if (dp[vi + 1, si] >= dp[vi, si + 1])
				{
					// vanillaUnits[vi] contributes nothing further to the LCS from here -
					// this side deleted it. matched[vi] stays null.
					++vi;
				}
				else
				{
					// sideUnits[si] contributes nothing further to the LCS from here -
					// it's this side's own insertion, attributed to the gap slot right
					// before whichever vanilla unit is still unresolved (vi).
					insertionsAtSlot[vi].Add(si);
					++si;
				}
			}
			// Any side units left once vanilla is exhausted are trailing insertions,
			// attributed to the final slot (after vanilla's last unit).
			while (si < m)
			{
				insertionsAtSlot[n].Add(si);
				++si;
			}

			return new UnitAlignment(matched, insertionsAtSlot);
		}
	}
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace WitcherScriptMerger.LoadOrder
{
	// Which side of a pairwise merge step a mod ranking prefers. `None` means the ranking
	// has no opinion here and the engine's existing behavior applies unchanged - the normal
	// case, since a ranking is opt-in and may be partial.
	public enum PreferredSide
	{
		None,
		Old,
		New,
	}

	// A user-supplied ranking of mods, used to decide whose version of a function survives
	// when two mods edit the same function differently.
	//
	// This is a different lever from the existing per-file order override
	// (FileMerger.ResolveMergeOrder): that one sets the *chain order* mods are merged in,
	// and must name every mod for the file it covers. This one doesn't reorder anything -
	// it answers "whose code wins" at the point FunctionLevelMergeEngine would otherwise
	// fall back to its most-distinct-from-vanilla tiebreak, which is order-independent and
	// has no notion of the user preferring one mod over another.
	//
	// Deliberately partial: rank only the mods you care about. A pair where neither side is
	// ranked returns None and behaves exactly as before, so adding a ranking can never
	// change the outcome of a conflict it doesn't mention.
	public static class ModPriority
	{
		// Reserved key an order file uses for the global ranking, alongside its ordinary
		// "<relative path>": [mods...] entries. Safe as a sentinel because `*` is a
		// reserved character in Windows file and directory names, so it can never collide
		// with a real conflict's relative path.
		public const string OrderFileKey = "*";

		// Lower index = higher priority, so rank 0 beats rank 1. An unranked mod sits below
		// every ranked one.
		const int Unranked = int.MaxValue;

		static int RankOf(IReadOnlyList<string> ranking, string modName)
		{
			if (ranking == null || string.IsNullOrWhiteSpace(modName))
				return Unranked;

			for (var i = 0; i < ranking.Count; ++i)
			{
				if (ranking[i] != null && ranking[i].EqualsIgnoreCase(modName))
					return i;
			}
			return Unranked;
		}

		/// <summary>
		/// Decides which side of one pairwise chain step the ranking prefers.
		/// </summary>
		/// <param name="ranking">Mod names, highest priority first. Null/empty = no opinion.</param>
		/// <param name="oldModNames">
		/// Every mod already accumulated into the old side. A chain step's old side is the
		/// merge of everything before it, so it carries several mods at once; its rank is the
		/// BEST rank among them. Without that, a highly-ranked mod would stop winning as soon
		/// as one more mod merged on top of it, which is the opposite of what ranking it
		/// means.
		/// </param>
		/// <param name="newModName">The single mod being merged in at this step.</param>
		public static PreferredSide Resolve(IReadOnlyList<string> ranking, IEnumerable<string> oldModNames, string newModName)
		{
			if (ranking == null || ranking.Count == 0)
				return PreferredSide.None;

			var oldRank = Unranked;
			if (oldModNames != null)
			{
				foreach (var name in oldModNames)
					oldRank = Math.Min(oldRank, RankOf(ranking, name));
			}

			var newRank = RankOf(ranking, newModName);

			// Neither side is mentioned - stay out of it entirely.
			if (oldRank == Unranked && newRank == Unranked)
				return PreferredSide.None;

			if (oldRank < newRank)
				return PreferredSide.Old;
			if (newRank < oldRank)
				return PreferredSide.New;

			// Equal ranks means the same mod appears on both sides, which the chain
			// shouldn't produce. No opinion rather than an arbitrary pick.
			return PreferredSide.None;
		}

		/// <summary>
		/// Pulls the global ranking out of an order file's entries, or null if it has none.
		/// The reserved key is removed from the caller's view of the dictionary by
		/// <see cref="WithoutRankingEntry"/> so it can never be mistaken for a path override.
		/// </summary>
		public static string[] ExtractRanking(IReadOnlyDictionary<string, string[]> orderOverrides)
		{
			if (orderOverrides == null || !orderOverrides.TryGetValue(OrderFileKey, out var ranking) || ranking == null)
				return null;

			// Blank entries are dropped rather than rejected: a ranking is advisory, and a
			// stray empty string in a hand-edited file shouldn't fail a whole merge run.
			var cleaned = ranking
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Select(name => name.Trim())
				.ToArray();

			return cleaned.Length == 0 ? null : cleaned;
		}

		/// <summary>
		/// The order-file entries with the reserved ranking key removed, so
		/// <c>ResolveMergeOrder</c> only ever sees real relative-path overrides.
		/// </summary>
		public static IReadOnlyDictionary<string, string[]> WithoutRankingEntry(IReadOnlyDictionary<string, string[]> orderOverrides)
		{
			if (orderOverrides == null || !orderOverrides.ContainsKey(OrderFileKey))
				return orderOverrides;

			// Preserve the source dictionary's key comparer. A plain ToDictionary would
			// rebuild with the default ordinal one and silently undo a deliberately-installed
			// case-insensitive comparer - WsmMcpTools.MergeConflicts builds exactly that
			// (StringComparer.OrdinalIgnoreCase, alongside separator normalization) so a
			// differently-cased but otherwise-correct path key isn't ignored. Dropping it here
			// would turn those keys back into silent no-ops, but only for callers that supply
			// a ranking - a bug that hides until the feature is used.
			var comparer = (orderOverrides as Dictionary<string, string[]>)?.Comparer
				?? EqualityComparer<string>.Default;

			return orderOverrides
				.Where(kvp => !string.Equals(kvp.Key, OrderFileKey, StringComparison.Ordinal))
				.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, comparer);
		}
	}
}

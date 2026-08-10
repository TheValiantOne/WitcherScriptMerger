using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DiffPlex;

namespace WitcherScriptMerger.Tools
{
	public readonly struct FunctionLevelMergeResult
	{
		// False means every other field is meaningless - the caller keeps whatever it
		// was about to do before considering this engine (write a conflict-marker
		// sidecar, or report the DiffAlgorithmException as-is). This engine never makes
		// a file-level outcome worse than that baseline - see TryMerge's own comment.
		public bool Applied { get; }
		public string MergedText { get; }
		// Human-readable audit notes for every place this engine picked one side over
		// another - a tiebreak, an edit surviving a competing deletion, or a mod's gap
		// comment not making it into the merged output. Never empty content silently:
		// every non-mechanical decision (i.e. every one that isn't "both sides agree"
		// or "only one side touched this at all") gets a note.
		public IReadOnlyList<string> Decisions { get; }

		public FunctionLevelMergeResult(bool applied, string mergedText, IReadOnlyList<string> decisions)
		{
			Applied = applied;
			MergedText = mergedText;
			Decisions = decisions;
		}

		public static readonly FunctionLevelMergeResult Declined = new FunctionLevelMergeResult(false, null, null);
	}

	// The function-level merge fallback (see WitcherScriptMerger.Core/CLAUDE.md's
	// "Function-level merge engine" section for the full design rationale). Only ever
	// called from DiffPlexMergeEngine.MergeHeadless at the two points where a whole-file merge
	// has already failed for a given pairwise chain step - this class's whole contract
	// is "try to do better than that specific failure, never worse." Every early-exit
	// path here (extraction failure, a genuine new-functionality naming collision)
	// returns FunctionLevelMergeResult.Declined, which the caller treats identically to
	// this class not existing at all.
	//
	// Splits vanilla/old/new into ScriptUnitExtractor.Extract's function/field units,
	// aligns old and new each independently against vanilla via UnitAligner (handling
	// both insertions and deletions, not just insertions - see the plan's Step 0
	// addendum: a real, common WitcherScript modding pattern, one mod outright
	// removing several vanilla functions, showed up on 3 of the 5 real files this
	// feature was built to help with, and that removal was found to persist through
	// the merge chain even at steps where the removing mod isn't a direct input -
	// treating deletions as an automatic decline would have left this engine unable to
	// help with most of what motivated it), then resolves each vanilla function
	// independently before reassembling.
	public static class FunctionLevelMergeEngine
	{
		static readonly Regex WhitespaceRun = new Regex(@"[ \t\r\n\f\v]+", RegexOptions.Compiled);

		public static FunctionLevelMergeResult TryMerge(
			string baseText, string oldText, string newText,
			string oldMarkerLabel, string newMarkerLabel,
			string oldDescription, string newDescription)
		{
			ScriptDocument baseDoc, oldDoc, newDoc;
			try
			{
				baseDoc = ScriptUnitExtractor.Extract(baseText);
				oldDoc = ScriptUnitExtractor.Extract(oldText);
				newDoc = ScriptUnitExtractor.Extract(newText);
			}
			catch (ScriptUnitExtractor.ExtractionException)
			{
				return FunctionLevelMergeResult.Declined;
			}

			var vanillaCount = baseDoc.Units.Count;
			var oldAlignment = UnitAligner.Align(baseDoc.Units, oldDoc.Units);
			var newAlignment = UnitAligner.Align(baseDoc.Units, newDoc.Units);

			var decisions = new List<string>();

			// Reconcile insertions (units on either side with no vanilla counterpart at
			// all) before resolving vanilla units - a same-named, differently-bodied
			// insertion on both sides is a genuine new-functionality collision, a
			// different problem shape from "vanilla function edited two ways", and out
			// of scope for this engine (declines the whole file, same as if this
			// engine didn't exist for this particular pair).
			var insertionsPerSlot = new List<string>[vanillaCount + 1];
			for (var slot = 0; slot <= vanillaCount; ++slot)
			{
				var resolved = ReconcileInsertions(oldDoc, oldAlignment, newDoc, newAlignment, slot);
				if (resolved == null)
					return FunctionLevelMergeResult.Declined;
				insertionsPerSlot[slot] = resolved;
			}

			var resolvedUnits = new string[vanillaCount];
			for (var i = 0; i < vanillaCount; ++i)
			{
				resolvedUnits[i] = ResolveUnit(
					baseDoc.Units[i],
					oldAlignment.MatchedSideIndex[i].HasValue ? oldDoc.Units[oldAlignment.MatchedSideIndex[i].Value].FullText : null,
					newAlignment.MatchedSideIndex[i].HasValue ? newDoc.Units[newAlignment.MatchedSideIndex[i].Value].FullText : null,
					oldMarkerLabel, newMarkerLabel, oldDescription, newDescription, decisions);
			}

			var merged = new StringBuilder();
			for (var slot = 0; slot <= vanillaCount; ++slot)
			{
				if (IsGapComparisonEligible(oldAlignment, newAlignment, slot, vanillaCount))
				{
					NoteGapMismatchIfAny(
						baseDoc.Gaps[slot],
						oldDoc.Gaps[GetSideGapIndex(oldAlignment, slot, vanillaCount)],
						newDoc.Gaps[GetSideGapIndex(newAlignment, slot, vanillaCount)],
						oldDescription, newDescription, decisions);
				}

				merged.Append(baseDoc.Gaps[slot]);
				foreach (var insertion in insertionsPerSlot[slot])
					merged.Append(insertion);
				if (slot < vanillaCount)
					merged.Append(resolvedUnits[slot]);
			}

			return new FunctionLevelMergeResult(true, merged.ToString(), decisions);
		}

		#region Per-unit resolution

		// null oldText/newText means that side deleted this vanilla unit outright
		// (UnitAligner.MatchedSideIndex was null for it). Ten-case resolution table:
		// both-deleted, both-unchanged, only-one-side-touched-it (edited OR deleted),
		// both-made-the-same-edit, an edit surviving a competing deletion (deletions
		// never silently win over a surviving edit - deleting code is unrecoverable if
		// wrong, keeping it is - always noted either way), and a genuine edit-vs-edit
		// collision (real 3-way merge first, tiebreak on distinctness-from-vanilla if
		// that fails or still conflicts).
		static string ResolveUnit(
			ScriptUnit baseUnit, string oldText, string newText,
			string oldMarkerLabel, string newMarkerLabel, string oldDescription, string newDescription,
			List<string> decisions)
		{
			var baseText = baseUnit.FullText;

			if (oldText == null && newText == null)
				return string.Empty;

			if (oldText == null)
			{
				if (newText == baseText)
					return string.Empty; // new side never touched it either - the deletion propagates
				decisions.Add(
					$"function {baseUnit.Name}: kept {newDescription}'s edit; {oldDescription} had deleted this " +
					"function (a deletion never silently overrides a surviving edit).");
				return newText;
			}

			if (newText == null)
			{
				if (oldText == baseText)
					return string.Empty;
				decisions.Add(
					$"function {baseUnit.Name}: kept {oldDescription}'s edit; {newDescription} had deleted this " +
					"function (a deletion never silently overrides a surviving edit).");
				return oldText;
			}

			if (oldText == baseText && newText == baseText)
				return baseText;
			if (oldText == baseText)
				return newText;
			if (newText == baseText)
				return oldText;
			if (oldText == newText)
				return oldText;

			// Both sides changed this function, differently - try a real per-function
			// 3-way merge before falling back to the whole-function tiebreak. Catching
			// DiffAlgorithmException here (rather than letting it propagate) is a real
			// improvement over today's whole-file behavior, not just defensive
			// symmetry: today, this exception means "give up on the entire file";
			// here, it means "skip DiffPlex's fine-grained inline merge for just this
			// one function," and the file still gets merged via the tiebreak below.
			DiffPlexMergeEngine.MergeTextResult? mergeResult;
			try
			{
				mergeResult = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, oldMarkerLabel, newMarkerLabel);
			}
			catch (DiffPlexMergeEngine.DiffAlgorithmException)
			{
				mergeResult = null;
			}

			if (mergeResult.HasValue && !mergeResult.Value.HasConflicts)
				return mergeResult.Value.MergedText;

			var oldDistinctness = ComputeDistinctness(baseText, oldText);
			var newDistinctness = ComputeDistinctness(baseText, newText);

			if (newDistinctness > oldDistinctness)
			{
				decisions.Add(
					$"function {baseUnit.Name}: kept {newDescription}'s version ({newDistinctness} changed diff " +
					$"blocks vs. vanilla, more distinct than {oldDescription}'s {oldDistinctness}), discarded " +
					$"{oldDescription}'s conflicting change to this function.");
				return newText;
			}

			// A tie (including the newDistinctness == oldDistinctness case) falls back
			// to oldText, mirroring DiffPlexMergeEngine's own whitespace-tiebreak
			// convention of always picking one deterministic side rather than guessing
			// further - see its IsWhitespaceOnlyDifference comment.
			decisions.Add(
				$"function {baseUnit.Name}: kept {oldDescription}'s version ({oldDistinctness} changed diff " +
				$"blocks vs. vanilla" + (newDistinctness == oldDistinctness ? ", tied with" : ", more distinct than") +
				$" {newDescription}'s {newDistinctness}), discarded {newDescription}'s conflicting change to this function.");
			return oldText;
		}

		static int ComputeDistinctness(string baseText, string sideText)
		{
			var baseStripped = ScriptUnitExtractor.StripComments(baseText);
			var sideStripped = ScriptUnitExtractor.StripComments(sideText);
			var diff = Differ.Instance.CreateLineDiffs(baseStripped, sideStripped, ignoreWhitespace: true);

			var score = 0;
			foreach (var block in diff.DiffBlocks)
				score += block.DeleteCountA + block.InsertCountB;
			return score;
		}

		#endregion

		#region Insertion reconciliation

		// Returns the resolved, ordered list of FullText to emit at this slot, or null
		// if a same-named insertion on both sides has different content (a genuine
		// new-functionality collision - declines the whole file, the one case this
		// method can't resolve on its own).
		static List<string> ReconcileInsertions(
			ScriptDocument oldDoc, UnitAlignment oldAlignment, ScriptDocument newDoc, UnitAlignment newAlignment, int slot)
		{
			var oldInsertions = oldAlignment.InsertionsAtSlot[slot].Select(i => oldDoc.Units[i]).ToList();
			var newInsertions = newAlignment.InsertionsAtSlot[slot].Select(i => newDoc.Units[i]).ToList();

			if (oldInsertions.Count == 0 && newInsertions.Count == 0)
				return new List<string>();

			var newByName = newInsertions.ToDictionary(u => u.Name);
			var consumedNewNames = new HashSet<string>();
			var result = new List<string>();

			foreach (var oldUnit in oldInsertions)
			{
				if (newByName.TryGetValue(oldUnit.Name, out var newUnit))
				{
					consumedNewNames.Add(oldUnit.Name);
					if (oldUnit.FullText != newUnit.FullText)
						return null;
					result.Add(oldUnit.FullText);
				}
				else
				{
					result.Add(oldUnit.FullText);
				}
			}
			foreach (var newUnit in newInsertions)
				if (!consumedNewNames.Contains(newUnit.Name))
					result.Add(newUnit.FullText);

			return result;
		}

		#endregion

		#region Gap comparison

		// A slot is only compared when both its neighboring vanilla units (if any) are
		// present, unmatched-to-nothing, on both sides, and neither side inserted
		// anything at this slot - i.e. the simple, overwhelmingly common case (per this
		// feature's own real-data measurement: the large majority of a file's gaps sit
		// between two functions neither mod touched structurally). Once an insertion or
		// deletion touches a slot's boundary, "the equivalent gap on each side" stops
		// being a single well-defined span to compare - deferred rather than guessed at.
		static bool IsGapComparisonEligible(UnitAlignment oldAlignment, UnitAlignment newAlignment, int slot, int vanillaCount)
		{
			if (oldAlignment.InsertionsAtSlot[slot].Count > 0 || newAlignment.InsertionsAtSlot[slot].Count > 0)
				return false;
			if (slot > 0 && (!oldAlignment.MatchedSideIndex[slot - 1].HasValue || !newAlignment.MatchedSideIndex[slot - 1].HasValue))
				return false;
			if (slot < vanillaCount && (!oldAlignment.MatchedSideIndex[slot].HasValue || !newAlignment.MatchedSideIndex[slot].HasValue))
				return false;
			return true;
		}

		// Only valid when IsGapComparisonEligible(slot) is true, which guarantees
		// MatchedSideIndex[slot] (or [slot - 1], for the final slot) has a value.
		static int GetSideGapIndex(UnitAlignment alignment, int slot, int vanillaCount)
		{
			if (slot < vanillaCount && alignment.MatchedSideIndex[slot].HasValue)
				return alignment.MatchedSideIndex[slot].Value;
			return alignment.MatchedSideIndex[slot - 1].Value + 1;
		}

		// Reassembly always keeps vanilla's own gap text verbatim (deterministic,
		// matches DiffPlexMergeEngine's own "take one side" precedent elsewhere) - this
		// only ever adds an audit note when a side's gap content differs from vanilla's
		// by more than whitespace/comments, since that's real, non-mechanical content
		// (typically a mod author's own comment) silently not making it into the merged
		// output. A purely whitespace/comment difference is never noted - that's exactly
		// the class of noise this whole engine exists to stop treating as meaningful.
		static void NoteGapMismatchIfAny(string baseGap, string oldGap, string newGap, string oldDescription, string newDescription, List<string> decisions)
		{
			var baseNorm = NormalizeGap(baseGap);
			var oldDiffers = NormalizeGap(oldGap) != baseNorm;
			var newDiffers = NormalizeGap(newGap) != baseNorm;

			if (oldDiffers)
				decisions.Add($"a comment from {oldDescription} near this position was not preserved (vanilla formatting/comments kept).");
			if (newDiffers)
				decisions.Add($"a comment from {newDescription} near this position was not preserved (vanilla formatting/comments kept).");
		}

		// Deliberately whitespace-collapse only, NOT comment-stripped: this feeds the
		// note above, whose whole point is to detect when comment CONTENT differs, not
		// just formatting - stripping comments first would blank away the very thing
		// being compared, silently defeating the check (caught by
		// TryMerge_GapCommentDifference_NotedButVanillaGapTextKept). Matches
		// DiffPlexMergeEngine.NormalizeWhitespace's own whitespace-only spirit.
		static string NormalizeGap(string text) => WhitespaceRun.Replace(text, " ").Trim();

		#endregion
	}
}

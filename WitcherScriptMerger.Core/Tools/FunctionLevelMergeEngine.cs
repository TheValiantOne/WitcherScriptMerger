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

			// Nothing for a FUNCTION-level engine to offer when vanilla has zero
			// extracted functions/fields at all (e.g. a file containing only top-level
			// consts/enums) - the entire document would be a single "gap" slot, and
			// gap content always reverts to vanilla's own text on reassembly (see
			// NoteGapMismatchIfAny's comment) - silently discarding BOTH sides' real,
			// substantive edits and reporting that data loss as a successful
			// AutoSolved merge, which is a materially worse outcome than declining and
			// falling through to the existing whole-file conflict-marker behavior.
			// Declining here is the correct behavior, not just a crash-avoidance
			// workaround for the vanillaCount == 0 case GetSideGapIndex/
			// GetGapEligibility must otherwise handle correctly for the (still valid)
			// case where vanilla DOES have units but a specific slot's neighbors don't.
			if (vanillaCount == 0)
				return FunctionLevelMergeResult.Declined;

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
				var oldHasInsertions = oldAlignment.InsertionsAtSlot[slot].Count > 0;
				var newHasInsertions = newAlignment.InsertionsAtSlot[slot].Count > 0;
				// Set when this slot's emission ended on a synthesized (line-break-
				// prefixed) unit text rather than gap text - the vanilla unit that
				// follows needs its own synthesized break too, since its usual leading
				// separator lived in the vanilla gap consumed before the insertions.
				var endedOnSynthesizedUnit = false;

				if (!oldHasInsertions && !newHasInsertions)
				{
					switch (GetGapEligibility(oldAlignment, newAlignment, slot, vanillaCount))
					{
						case GapEligibility.Eligible:
							NoteGapMismatchIfAny(
								baseDoc.Gaps[slot],
								oldDoc.Gaps[GetSideGapIndex(oldAlignment, slot, vanillaCount)],
								newDoc.Gaps[GetSideGapIndex(newAlignment, slot, vanillaCount)],
								oldDescription, newDescription, decisions);
							break;

						case GapEligibility.IneligibleDeletion:
							// Non-unit content either side may have changed here has no
							// well-defined single gap index to compare at all - see
							// GetGapEligibility's own comment. Silently keeping vanilla's
							// text with zero signal would contradict this class's own
							// "never empty content silently" contract, so a conservative,
							// location-described caveat is emitted instead of a precise
							// diff.
							decisions.Add(
								$"content {DescribeSlot(baseDoc.Units, slot, vanillaCount)} wasn't automatically " +
								"verified because a nearby function was removed by one side - if either mod changed " +
								"non-function content here, review manually.");
							break;
					}

					merged.Append(baseDoc.Gaps[slot]);
				}
				else if (oldHasInsertions != newHasInsertions)
				{
					// Exactly one side inserts at this slot - the overwhelmingly common
					// case. Emit that side's own contiguous span (its gaps and inserted
					// units, verbatim and in its own order) instead of vanilla's gap
					// followed by bare concatenated unit texts. This is the fix for
					// docs/bugs/function-level-merge-gap-handling.md defect 1: vanilla's
					// gap at this slot can contain structural content (a class-closing
					// brace), and appending insertions after it emitted mod-added class
					// members at global scope; and the separators between consecutive
					// inserted units live in the *side's* gaps, which the old emission
					// discarded entirely, running declarations together onto one line.
					var insertingOld = oldHasInsertions;
					if (!TryAppendSideInsertionSpan(
						merged, slot, vanillaCount,
						insertingOld ? oldDoc : newDoc,
						insertingOld ? oldAlignment : newAlignment,
						insertingOld ? oldText : newText,
						insertingOld ? oldDescription : newDescription,
						insertingOld ? newDoc : oldDoc,
						insertingOld ? newAlignment : oldAlignment,
						insertingOld ? newDescription : oldDescription,
						baseDoc, decisions))
					{
						// The span's anchors aren't well-defined (a neighboring vanilla
						// unit was deleted on the inserting side). Fall back to
						// vanilla-gap-plus-units - but only when vanilla's gap carries no
						// structural braces (placement relative to a brace would be a
						// guess), and with a synthesized line break in front of each unit
						// so declarations can never run together.
						if (GapHasStructuralBrace(baseDoc.Gaps[slot]))
							return DeclineWithWarning(
								$"function-level merge declined: mods insert new declarations {DescribeSlot(baseDoc.Units, slot, vanillaCount)}, " +
								"where a neighboring vanilla unit was also removed and the surrounding content contains structural braces - " +
								"placement cannot be determined safely.");

						merged.Append(baseDoc.Gaps[slot]);
						foreach (var insertion in insertionsPerSlot[slot])
							AppendWithLineBreak(merged, insertion);
						endedOnSynthesizedUnit = insertionsPerSlot[slot].Count > 0;
					}
				}
				else
				{
					// Both sides insert at the same slot (rare). The reconciled unit list
					// is already collision-checked; emission keeps vanilla's gap, so if
					// that gap contains structural braces there is no safe answer to
					// "before or after the brace?" for a merged list drawn from two
					// different documents - decline rather than guess.
					if (GapHasStructuralBrace(baseDoc.Gaps[slot]))
						return DeclineWithWarning(
							$"function-level merge declined: both mods insert new declarations {DescribeSlot(baseDoc.Units, slot, vanillaCount)} " +
							"and the surrounding content contains structural braces - relative placement cannot be determined safely.");

					merged.Append(baseDoc.Gaps[slot]);
					foreach (var insertion in insertionsPerSlot[slot])
						AppendWithLineBreak(merged, insertion);
					endedOnSynthesizedUnit = insertionsPerSlot[slot].Count > 0;
				}

				if (slot < vanillaCount)
				{
					if (endedOnSynthesizedUnit && resolvedUnits[slot].Length > 0)
						AppendWithLineBreak(merged, resolvedUnits[slot]);
					else
						merged.Append(resolvedUnits[slot]);
				}
			}

			var mergedText = merged.ToString();

			// Post-reassembly sanity gate (docs/bugs/function-level-merge-gap-handling.md,
			// "Suggested regression checks" #1): member-shaped declarations at brace
			// depth 0 are invalid WitcherScript ("'public' has no sense for global
			// function ..."), and unbalanced braces never compile. Either means this
			// engine assembled something structurally wrong - decline, which falls back
			// to the whole-file conflict-marker sidecar, rather than report a
			// successful merge the game will refuse to compile. Cheap, and catches
			// whole classes of future interleaving bugs, not just the two known ones.
			if (!PassesReassemblySanityGate(mergedText, out var gateFailure))
				return DeclineWithWarning("function-level merge declined by output sanity check: " + gateFailure);

			return new FunctionLevelMergeResult(true, mergedText, decisions);
		}

		static FunctionLevelMergeResult DeclineWithWarning(string message)
		{
			AppState.Notifier.ShowMessage(message, "Function-Level Merge", NotifyButtons.OK, DialogIcon.Warning);
			return FunctionLevelMergeResult.Declined;
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
					$"{baseUnit.DescribeKind()} {baseUnit.Name}: kept {newDescription}'s edit; {oldDescription} had deleted it " +
					"(a deletion never silently overrides a surviving edit).");
				return newText;
			}

			if (newText == null)
			{
				if (oldText == baseText)
					return string.Empty;
				decisions.Add(
					$"{baseUnit.DescribeKind()} {baseUnit.Name}: kept {oldDescription}'s edit; {newDescription} had deleted it " +
					"(a deletion never silently overrides a surviving edit).");
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
					$"{baseUnit.DescribeKind()} {baseUnit.Name}: kept {newDescription}'s version ({newDistinctness} changed diff " +
					$"blocks vs. vanilla, more distinct than {oldDescription}'s {oldDistinctness}), discarded " +
					$"{oldDescription}'s conflicting change here.");
				return newText;
			}

			// A tie (including the newDistinctness == oldDistinctness case) falls back
			// to oldText, mirroring DiffPlexMergeEngine's own whitespace-tiebreak
			// convention of always picking one deterministic side rather than guessing
			// further - see its IsWhitespaceOnlyDifference comment.
			decisions.Add(
				$"{baseUnit.DescribeKind()} {baseUnit.Name}: kept {oldDescription}'s version ({oldDistinctness} changed diff " +
				$"blocks vs. vanilla" + (newDistinctness == oldDistinctness ? ", tied with" : ", more distinct than") +
				$" {newDescription}'s {newDistinctness}), discarded {newDescription}'s conflicting change here.");
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

			// Two insertions with the same scoped name on ONE side (e.g. a mod's own
			// copy-paste mistake) is an ambiguity this method can't safely resolve -
			// which occurrence did the mod author actually intend? Decline rather than
			// guess, same policy as the same-name-different-body-across-sides case
			// below. Also avoids ToDictionary throwing on the duplicate key.
			if (HasDuplicateNames(oldInsertions) || HasDuplicateNames(newInsertions))
				return null;

			var newByName = newInsertions.ToDictionary(u => u.ScopedName);
			var consumedNewNames = new HashSet<string>();
			var result = new List<string>();

			foreach (var oldUnit in oldInsertions)
			{
				if (newByName.TryGetValue(oldUnit.ScopedName, out var newUnit))
				{
					consumedNewNames.Add(oldUnit.ScopedName);
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
				if (!consumedNewNames.Contains(newUnit.ScopedName))
					result.Add(newUnit.FullText);

			return result;
		}

		static bool HasDuplicateNames(List<ScriptUnit> units) => units.Select(u => u.ScopedName).Distinct().Count() != units.Count;

		#endregion

		#region Insertion emission

		// Emits the inserting side's own contiguous text span for a slot: from the end
		// of its unit matched to vanilla unit slot-1 (or offset 0 for the leading slot)
		// to the start of its unit matched to vanilla unit slot (or end-of-text for the
		// trailing slot). The span is a verbatim substring of the side's own text, so
		// inserted units keep their exact position relative to any structural content
		// (class braces) and their own separators. Returns false - fall back to the
		// caller's conservative path - when either anchor is undefined because the
		// neighboring vanilla unit was deleted on the inserting side.
		static bool TryAppendSideInsertionSpan(
			StringBuilder merged, int slot, int vanillaCount,
			ScriptDocument insertingDoc, UnitAlignment insertingAlignment, string insertingText, string insertingDescription,
			ScriptDocument otherDoc, UnitAlignment otherAlignment, string otherDescription,
			ScriptDocument baseDoc, List<string> decisions)
		{
			int prevSideIndex; // index into insertingDoc.Units of the unit before the span, or -1
			if (slot == 0)
			{
				prevSideIndex = -1;
			}
			else
			{
				var matched = insertingAlignment.MatchedSideIndex[slot - 1];
				if (!matched.HasValue)
					return false;
				prevSideIndex = matched.Value;
			}

			int nextSideIndex; // index into insertingDoc.Units of the unit after the span, or Units.Count
			if (slot == vanillaCount)
			{
				nextSideIndex = insertingDoc.Units.Count;
			}
			else
			{
				var matched = insertingAlignment.MatchedSideIndex[slot];
				if (!matched.HasValue)
					return false;
				nextSideIndex = matched.Value;
			}

			var spanStart = prevSideIndex < 0 ? 0 : insertingDoc.Units[prevSideIndex].EndOffset;
			var spanEnd = nextSideIndex >= insertingDoc.Units.Count ? insertingText.Length : insertingDoc.Units[nextSideIndex].StartOffset;
			merged.Append(insertingText, spanStart, spanEnd - spanStart);

			// Audit notes. Taking the inserting side's span means ITS surrounding gap
			// text wins over vanilla's at this slot (necessarily - the inserted
			// declarations live inside it); note when that surrounding text differs
			// from vanilla's beyond whitespace, and separately note when the OTHER
			// side's own gap content here (comparable only if its own neighbors are
			// intact) differs from vanilla and is therefore not preserved.
			var insertingGapConcat = new StringBuilder();
			for (var g = prevSideIndex + 1; g <= nextSideIndex; ++g)
				insertingGapConcat.Append(insertingDoc.Gaps[g]);
			if (NormalizeGap(insertingGapConcat.ToString()) != NormalizeGap(baseDoc.Gaps[slot]))
				decisions.Add(
					$"content {DescribeSlot(baseDoc.Units, slot, vanillaCount)}: kept {insertingDescription}'s " +
					"surrounding text (it inserted new declarations here); vanilla's own text at this position was superseded.");

			if (GetGapEligibilityOneSide(otherAlignment, slot, vanillaCount))
			{
				var otherGap = otherDoc.Gaps[GetSideGapIndex(otherAlignment, slot, vanillaCount)];
				if (NormalizeGap(otherGap) != NormalizeGap(baseDoc.Gaps[slot]))
					decisions.Add($"content from {otherDescription} near this position was not preserved ({insertingDescription}'s text kept).");
			}

			return true;
		}

		// Appends unit text preceded by a line break unless the builder already ends
		// with one - the conservative-fallback separator synthesis, so two declarations
		// can never run together onto one line even when the side's own separator gaps
		// aren't safely identifiable.
		static void AppendWithLineBreak(StringBuilder merged, string text)
		{
			if (merged.Length > 0 && merged[merged.Length - 1] != '\n')
				merged.Append("\r\n");
			merged.Append(text);
		}

		// True when a gap contains a brace outside strings/comments - i.e. structural
		// content (a class opening/closing brace) that makes "where do inserted units
		// go relative to it?" ambiguous for any emission that isn't a verbatim
		// side-span. Unparseable gap content is treated as structural (conservative).
		static bool GapHasStructuralBrace(string gap)
		{
			if (gap.IndexOf('{') < 0 && gap.IndexOf('}') < 0)
				return false;
			try
			{
				var mask = ScriptUnitExtractor.BuildStructuralMask(gap);
				return mask.IndexOf('{') >= 0 || mask.IndexOf('}') >= 0;
			}
			catch (ScriptUnitExtractor.ExtractionException)
			{
				return true;
			}
		}

		// Like GetGapEligibility but for one side only (the non-inserting side of a
		// single-side insertion slot): its gap index for this slot is well-defined iff
		// both neighboring vanilla units are matched on it.
		static bool GetGapEligibilityOneSide(UnitAlignment alignment, int slot, int vanillaCount)
		{
			if (alignment.InsertionsAtSlot[slot].Count > 0)
				return false;
			if (slot > 0 && !alignment.MatchedSideIndex[slot - 1].HasValue)
				return false;
			if (slot < vanillaCount && !alignment.MatchedSideIndex[slot].HasValue)
				return false;
			return true;
		}

		#endregion

		#region Output sanity gate

		// Member-shaped line starts that are invalid at brace depth 0 in WitcherScript:
		// access modifiers ("'public' has no sense for global function ...", the exact
		// compile error the original bug produced), default-value statements, and var
		// declarations (the language has no globals). Deliberately narrow - global
		// `function`/`exec function`/`statemachine class` etc. are all legal at depth 0
		// and must never trip this.
		static readonly Regex DepthZeroMemberShapeRegex = new Regex(
			@"^(?:(?:public|private|protected)\b|default\s+\w+\s*=|(?:(?:editable|saved|import|const)\s+)*(?:var|autobind)\s)",
			RegexOptions.Compiled);

		// Walks the reassembled output's structural mask line by line, tracking brace
		// depth, and fails on: a member-shaped declaration appearing at depth 0
		// (including after a closing brace on the same line - the bug's
		// "}<TAB>public function ..." mangling), depth ever going negative, or a
		// nonzero final depth. Public (not just for tests): external validation
		// tooling - e.g. a live-install regression harness checking real merged
		// output - can reuse exactly the gate the engine itself applies.
		public static bool PassesReassemblySanityGate(string mergedText, out string failureReason)
		{
			string mask;
			try
			{
				mask = ScriptUnitExtractor.BuildStructuralMask(mergedText);
			}
			catch (ScriptUnitExtractor.ExtractionException ex)
			{
				failureReason = "reassembled output does not scan cleanly (" + ex.Message + ")";
				return false;
			}

			var depth = 0;
			var lineStart = 0;
			while (lineStart <= mask.Length)
			{
				var lineEnd = mask.IndexOf('\n', lineStart);
				if (lineEnd < 0)
					lineEnd = mask.Length;

				// Find the first position in this line at which depth is 0, then test
				// the remainder of the line from there - so a declaration mangled onto
				// the tail of a class-closing-brace line is still caught.
				var depthZeroAt = depth == 0 ? lineStart : -1;
				for (var i = lineStart; i < lineEnd; ++i)
				{
					if (mask[i] == '{')
					{
						++depth;
					}
					else if (mask[i] == '}')
					{
						--depth;
						if (depth < 0)
						{
							failureReason = "unbalanced braces (extra '}') in reassembled output";
							return false;
						}
						if (depth == 0 && depthZeroAt < 0)
							depthZeroAt = i + 1;
					}
				}

				if (depthZeroAt >= 0)
				{
					var content = mask.Substring(depthZeroAt, lineEnd - depthZeroAt).TrimStart(' ', '\t');
					if (DepthZeroMemberShapeRegex.IsMatch(content))
					{
						failureReason = "member-shaped declaration at global scope: \"" +
							content.TrimEnd('\r', '\n', ' ', '\t') + "\"";
						return false;
					}
				}

				lineStart = lineEnd + 1;
			}

			if (depth != 0)
			{
				failureReason = "unbalanced braces (unclosed '{') in reassembled output";
				return false;
			}

			failureReason = null;
			return true;
		}

		#endregion

		#region Gap comparison

		enum GapEligibility
		{
			Eligible,
			IneligibleInsertion,
			IneligibleDeletion,
		}

		// A slot is only precisely compared when both its neighboring vanilla units (if
		// any) are present, unmatched-to-nothing, on both sides, and neither side
		// inserted anything at this slot - i.e. the simple, overwhelmingly common case
		// (per this feature's own real-data measurement: the large majority of a file's
		// gaps sit between two functions neither mod touched structurally). Once an
		// insertion or deletion touches a slot's boundary, "the equivalent gap on each
		// side" stops being a single well-defined span to compare - deferred rather than
		// guessed at, but NOT silently: an insertion is already visible in the
		// reassembled output (no note needed), while a deletion gets a conservative
		// caveat note from TryMerge's caller (see GapEligibility.IneligibleDeletion's
		// call site) since non-function content near it has no signal at all otherwise.
		static GapEligibility GetGapEligibility(UnitAlignment oldAlignment, UnitAlignment newAlignment, int slot, int vanillaCount)
		{
			if (oldAlignment.InsertionsAtSlot[slot].Count > 0 || newAlignment.InsertionsAtSlot[slot].Count > 0)
				return GapEligibility.IneligibleInsertion;
			if (slot > 0 && (!oldAlignment.MatchedSideIndex[slot - 1].HasValue || !newAlignment.MatchedSideIndex[slot - 1].HasValue))
				return GapEligibility.IneligibleDeletion;
			if (slot < vanillaCount && (!oldAlignment.MatchedSideIndex[slot].HasValue || !newAlignment.MatchedSideIndex[slot].HasValue))
				return GapEligibility.IneligibleDeletion;
			return GapEligibility.Eligible;
		}

		// Only valid when GetGapEligibility(slot) is Eligible, which guarantees a
		// meaningful gap index exists on this side for the requested slot. slot == 0 is
		// always gap index 0 outright - the leading gap exists at a fixed position
		// regardless of alignment, unlike every other slot, which is anchored to a
		// matched vanilla unit's own index. (A prior version of this method derived
		// slot 0 via the same "matched unit's own index" branch used for slot 1..
		// vanillaCount-1, which happened to also produce 0 whenever vanillaCount > 0 -
		// but that branch requires slot < vanillaCount, which is false whenever
		// vanillaCount == 0, falling through to the "final slot" branch below and
		// indexing MatchedSideIndex[-1] on an empty array. A file with zero extracted
		// functions/fields - e.g. one containing only top-level consts/enums - is a
		// real, reachable case, not hypothetical.)
		static int GetSideGapIndex(UnitAlignment alignment, int slot, int vanillaCount)
		{
			if (slot == 0)
				return 0;
			if (slot < vanillaCount)
				return alignment.MatchedSideIndex[slot].Value;
			return alignment.MatchedSideIndex[slot - 1].Value + 1;
		}

		static string DescribeSlot(IReadOnlyList<ScriptUnit> vanillaUnits, int slot, int vanillaCount)
		{
			if (vanillaCount == 0)
				return "in this file";
			if (slot == 0)
				return $"before {vanillaUnits[0].Name}";
			if (slot == vanillaCount)
				return $"after {vanillaUnits[vanillaCount - 1].Name}";
			return $"between {vanillaUnits[slot - 1].Name} and {vanillaUnits[slot].Name}";
		}

		// Reassembly always keeps vanilla's own gap text verbatim (deterministic,
		// matches DiffPlexMergeEngine's own "take one side" precedent elsewhere) - this
		// only ever adds an audit note when a side's gap content differs from vanilla's
		// by more than whitespace, since that's real, non-mechanical content (a
		// comment, but just as easily a default value or an undecorated var - gap
		// content isn't only comments) silently not making it into the merged output. A
		// purely whitespace difference is never noted - that's exactly the class of
		// noise this whole engine exists to stop treating as meaningful.
		static void NoteGapMismatchIfAny(string baseGap, string oldGap, string newGap, string oldDescription, string newDescription, List<string> decisions)
		{
			var baseNorm = NormalizeGap(baseGap);
			var oldDiffers = NormalizeGap(oldGap) != baseNorm;
			var newDiffers = NormalizeGap(newGap) != baseNorm;

			if (oldDiffers)
				decisions.Add($"content from {oldDescription} near this position was not preserved (vanilla formatting/content kept).");
			if (newDiffers)
				decisions.Add($"content from {newDescription} near this position was not preserved (vanilla formatting/content kept).");
		}

		// Deliberately whitespace-collapse only, NOT comment-stripped: this feeds the
		// note above, whose whole point is to detect when gap CONTENT differs, not just
		// formatting - stripping comments first would blank away the very thing being
		// compared, silently defeating the check (caught by
		// TryMerge_GapCommentDifference_NotedButVanillaGapTextKept). Reuses
		// DiffPlexMergeEngine.NormalizeWhitespace directly rather than a second, private
		// copy of the same regex+trim logic - an earlier version of this method did
		// duplicate it, using the parameterless Trim() instead of NormalizeWhitespace's
		// deliberate Trim(WhitespaceChars), which silently reintroduced the exact
		// NBSP-vs-space false-equivalence bug that method's own comment documents
		// fixing (flagged in code review).
		static string NormalizeGap(string text) => DiffPlexMergeEngine.NormalizeWhitespace(new[] { text });

		#endregion
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools
{
	// In-process, external-binary-free alternative to KDiff3MergeEngine (host project),
	// built on DiffPlex (MIT-licensed NuGet package)'s ThreeWayDiffer. Not the active
	// engine by default - see Program.Main (host project) for the "MergeEngine" App.config
	// switch. See Tools/IMergeEngine.cs for why this interface exists at all: it's
	// Core/host split scaffolding, not a permanent pluggable-engine abstraction, and a
	// later unit that removes KDiff3 entirely may delete the interface and inline this
	// engine's logic directly into FileMerger.
	//
	// There's no UI here at all - unlike KDiff3MergeEngine, which can open KDiff3's own
	// GUI for the interactive path - so "interactive" and "headless" collapse to the same
	// underlying logic. Merge() just runs MergeHeadless() and maps NeedsManualResolution
	// to Failed, since IMergeEngine.Merge's contract explicitly forbids ever returning
	// NeedsManualResolution (that's a headless-only concept - see the interface's doc
	// comment). One real behavior difference from KDiff3MergeEngine as a result: the
	// "ReviewEachMerge" setting (show the merge UI even for auto-solvable merges, so the
	// user can double check it) has nothing to open here and is silently not honored -
	// there is no in-process equivalent to implement it against.
	public class DiffPlexMergeEngine : IMergeEngine
	{
		#region Types

		public readonly struct MergeTextResult
		{
			public string MergedText { get; }
			public bool HasConflicts { get; }

			public MergeTextResult(string mergedText, bool hasConflicts)
			{
				MergedText = mergedText;
				HasConflicts = hasConflicts;
			}
		}

		// Thrown by BuildMerge when DiffPlex's ThreeWayDiffer itself produces
		// internally inconsistent diff-block metadata for a given base/old/new triple
		// - a genuine, confirmed upstream bug (DiffPlex 1.9.0), not a defect in this
		// class's own loop. See BuildMerge's comment for the full empirical writeup
		// and CLAUDE.md's Compatibility constraints for measured failure rates. Kept
		// separate from ArgumentNullException (a caller-error guard) so
		// MergeHeadless can catch specifically this and only this as "the algorithm
		// itself can't be trusted here" rather than accidentally swallowing an
		// unrelated bug.
		public sealed class DiffAlgorithmException : Exception
		{
			public DiffAlgorithmException(string message) : base(message) { }
		}

		#endregion

		#region Members

		// Deliberately the classic ASCII whitespace set (space, tab, CR, LF, form feed,
		// vertical tab), not \s+: .NET's \s matches the full Unicode whitespace
		// category too (NBSP, U+2028/2029, ideographic space, etc.), and collapsing
		// those away could misclassify a genuine content difference as "purely
		// whitespace" - e.g. two mods' string-literal dialogue text differing only by
		// NBSP vs. a regular space (plausible in localized text) would otherwise be
		// silently auto-resolved instead of flagged as a conflict. CR/LF stay included
		// so a side that merely adds/removes a blank line (see
		// BuildMerge_WhitespaceOnlyConflict_ToleratesDifferingLineCounts) still
		// collapses the same as before - only the extra-exotic Unicode members of \s
		// are excluded. Flagged in code review, see CLAUDE.md.
		static readonly Regex WhitespaceRun = new Regex(@"[ \t\r\n\f\v]+", RegexOptions.Compiled);

		#endregion

		public MergeEngineResult Merge(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath)
		{
			var result = MergeHeadless(source1, source2, vanillaFile, outputPath);
			return result == MergeEngineResult.NeedsManualResolution ? MergeEngineResult.Failed : result;
		}

		public MergeEngineResult MergeHeadless(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath)
		{
			var hasVanillaVersion = vanillaFile != null && vanillaFile.Exists;

			// A 3-way merge is meaningless without a base to diff against - confirmed
			// empirically in this change's verification scratch app that feeding
			// ThreeWayDiffer an empty base string doesn't degrade gracefully to some
			// reasonable 2-way behavior: CreateThreeWayDiffBlocks' main loop is
			// `while (baseIndex < basePieces.Count)`, which never executes when
			// basePieces.Count is 0, so it silently returns zero diff blocks and a
			// merge result with IsSuccessful=true but a completely empty MergedPieces -
			// i.e. it would happily "auto-solve" straight to an empty output file. In
			// practice this is expected mainly on the bundle-content path, when no
			// vanilla bundle containing this file could be found (FileMerger.
			// GetUnpackedFiles leaves _vanillaFile null), but this guard applies
			// unconditionally to any conflict with no vanilla file, flat or bundled -
			// safest, and consistent with HeadlessMergeNotifier's non-destructive
			// defaults, is to refuse rather than guess. Note this is a real, deliberate
			// behavior difference from KDiff3MergeEngine: Tools/KDiff3.cs's BuildArgs has
			// no equivalent guard and always attempts a real (if degraded, vanilla-less)
			// 2-way --auto merge in this situation instead of refusing outright, because
			// KDiff3 itself has a coherent notion of a 2-file diff/merge - DiffPlex's
			// ThreeWayDiffer, as used here, does not, so there's no equally meaningful
			// fallback to attempt. Which conflicts even get attempted can therefore differ
			// depending on which engine is configured; flagged in code review, not fixed
			// by building a parallel 2-way DiffPlex merge path since that's new scope
			// beyond what this engine set out to replicate - see CLAUDE.md.
			if (!hasVanillaVersion)
			{
				AppState.Notifier.ShowMessage(
					$"Skipped {source1.Name} + {source2.Name}: no vanilla version of this file could be found, " +
					"so a 3-way merge isn't possible.",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			// Same "merging an updated mod file into an existing merge chain" guard
			// KDiff3.RunHeadless applies (see its comment for the full reasoning) - kept
			// duplicated here rather than hoisted into FileMerger since that's shared
			// orchestration code outside this unit's scope; a later unit collapsing the
			// merge engines should consider moving it there instead of keeping two copies.
			// One real consequence of the duplication (vs. hoisting into FileMerger,
			// which both Merge and MergeHeadless funnel through) worth calling out: since
			// Merge() (the interactive path) just delegates straight to MergeHeadless()
			// here (see this class's header comment - there's no UI to fall back to),
			// this outdated-hash case comes back as Failed on the interactive path too,
			// where KDiff3MergeEngine's own interactive Run() instead opens KDiff3's GUI
			// for manual review. That gap already exists for every other kind of conflict
			// on the DiffPlex interactive path (no UI here at all yet), so it isn't a new
			// asymmetry this guard introduces - flagged in code review, see CLAUDE.md.
			if (source1.TextFile.FullName.EqualsIgnoreCase(outputPath)
				&& source2.Hash != null && source2.Hash.IsOutdated)
			{
				AppState.Notifier.ShowMessage(
					$"Skipped {source1.Name} + {source2.Name}: merging an updated mod file into a merge " +
					"created with a previous version needs manual review (auto-solving could keep changes " +
					"from the previous version that have been removed in the new one).",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			var baseText = FileEncoding.ReadAnyEncoding(vanillaFile.FullName);
			var oldText = FileEncoding.ReadAnyEncoding(source1.TextFile.FullName);
			var newText = FileEncoding.ReadAnyEncoding(source2.TextFile.FullName);

			MergeTextResult result;
			try
			{
				result = BuildMerge(baseText, oldText, newText, source1.Name, source2.Name);
			}
			catch (DiffAlgorithmException ex)
			{
				// DiffPlex's own diff algorithm produced output it isn't safe to trust
				// (see BuildMerge's comment) - don't write anything, including a sidecar:
				// the "conflict marker" content itself would have been built from the
				// same inconsistent piece indices, so it can't be trusted either. This is
				// the one case where DiffPlexMergeEngine can't even offer a conflict-marker
				// starting point the way KDiff3 always can - genuinely needs the source
				// files opened side by side.
				AppState.Notifier.ShowMessage(
					$"Skipped {source1.Name} + {source2.Name}: the automatic 3-way merge algorithm hit " +
					$"an internal inconsistency it couldn't safely recover from ({ex.Message}) - a known " +
					"limitation of the underlying DiffPlex library for certain multi-edit conflicts, see " +
					"CLAUDE.md. Needs manual resolution (e.g. via KDiff3).",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			if (!result.HasConflicts)
			{
				// A prior attempt at this same conflict may have left a sidecar marker
				// file behind (see below) - if this attempt now auto-solves (e.g. the
				// mod files were updated to no longer conflict), remove it so it doesn't
				// sit next to the fresh output indefinitely, stale and misleading.
				DeleteIfExists(GetConflictMarkerPath(outputPath));

				FileEncoding.WriteUtf16(outputPath, result.MergedText);
				return MergeEngineResult.AutoSolved;
			}

			// Never write conflict markers to outputPath itself: FileMerger's headless
			// callers (MergeFlatConflictHeadless/MergeBundleConflictHeadless) check
			// `File.Exists(_outputPath)` BEFORE attempting a merge and, if it exists,
			// prompt to overwrite via ConfirmOutputOverwrite - which HeadlessMergeNotifier
			// always answers "no". A marker file left at outputPath would therefore
			// permanently block every future retry of this same conflict without ever
			// attempting the merge again. Writing to a separate sidecar location instead
			// (see GetConflictMarkerPath) keeps outputPath itself untouched (so retries
			// behave exactly as if this merge had never been attempted) while still
			// producing well-formed conflict-marker output at a predictable, computable
			// location for a later unit to open in the user's default text editor.
			FileEncoding.WriteUtf16(GetConflictMarkerPath(outputPath), result.MergedText);

			AppState.Notifier.ShowMessage(
				$"Skipped {source1.Name} + {source2.Name}: genuine conflict, needs manual resolution. " +
				$"Conflict markers were written to {GetConflictMarkerPath(outputPath)} for review.",
				"Skipped", NotifyButtons.OK, DialogIcon.Warning);
			return MergeEngineResult.NeedsManualResolution;
		}

		// No external executable - DiffPlex is an in-process managed library, so there's
		// nothing to validate a path for. Note this doesn't remove QuickBMS/wcc_lite from
		// Paths.ValidateDependencyPaths()'s checks - those are still required for bundle
		// content regardless of which text-merge engine is active.
		public bool ValidateExePath() => true;

		// Where a conflict-marker file is written when a merge can't be auto-solved -
		// never at outputPath itself (see MergeHeadless's comment above). Originally
		// this wrote to "<outputPath>.conflict", right beside the real output - but code
		// review (see CLAUDE.md) caught two real problems with that: (1) for a flat-file
		// (.ws/.xml) conflict, outputPath sits inside the live, user-facing
		// Paths.ModsDirectory tree, and nothing ever cleans up a sidecar left there,
		// unlike Paths.TempBundleContent, which is documented as safe to clear between
		// runs; (2) for a bundle-content conflict, outputPath sits inside
		// Paths.MergedBundleContent, which Tools/WccLite.PackBundle packs *wholesale*
		// (no filtering) - a leftover ".conflict" text file there would get embedded as
		// bogus content into the shipped blob0.bundle on any later successful pack of
		// that same bundle. Relocating under Paths.DiffPlexConflictsDirectory avoids
		// both - and deliberately does NOT nest under Paths.TempBundleContent either,
		// despite both being "scratch-ish" locations conceptually: an earlier version
		// of this fix did nest there, and end-to-end testing against the real CLI
		// caught a real regression - FileMerger.CleanUpTempFiles() deletes the entire
		// TempBundleContent tree wholesale at the end of every headless merge run (to
		// clear QuickBMS-unpacked bundle scratch content), which silently deleted every
		// sidecar moments after this method wrote it, before a user could ever see it.
		// See Paths.DiffPlexConflictsDirectory's own comment for the full story. The
		// XxHash32 of the full absolute outputPath (Core already depends on
		// System.IO.Hashing for Tools/Hasher.cs) keeps the result collision-free without
		// needing to know which of those two root trees outputPath came from, and
		// without the unbounded path length a naive "flatten the whole absolute path
		// into one filename" scheme would risk for a deeply-nested bundle-content path.
		// string.GetHashCode() was deliberately not used here - .NET randomizes string
		// hash codes per process by default, so it isn't stable across runs, unlike
		// XxHash32. This is still a computable, not merely a discoverable-by-browsing,
		// location: a later unit wiring up "open in editor" can call this same method.
		public static string GetConflictMarkerPath(string outputPath)
		{
			var pathHash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(outputPath), 0);
			var fileName = Path.GetFileName(outputPath) + "." + pathHash.ToString("X8") + ".conflict";
			return Path.Combine(Paths.DiffPlexConflictsDirectory, fileName);
		}

		// Swallows every exception (locked file, permission denial, etc.) rather than
		// surfacing a failed delete - deliberate, not an oversight: this only ever
		// removes a stale sidecar right before writing a fresh, correct output to
		// outputPath, which happens regardless of whether this cleanup succeeds. The
		// only consequence of a failed delete is a stale ".conflict" file left sitting
		// next to a now-correct output - mildly confusing if someone stumbles on it, but
		// never incorrect or data-lossy, so it isn't worth a user-facing notification for
		// what's already a low-probability failure on a best-effort cleanup step.
		// Flagged in code review, see CLAUDE.md.
		static void DeleteIfExists(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}

		#region Merge algorithm

		// The actual 3-way merge, factored out as a public static method (independent of
		// any FileMerger/MergeSource/disk I/O) specifically so it's directly unit
		// testable. Mirrors DiffPlex's own ThreeWayDiffer.CreateMerge loop (see its
		// source for the shape this follows) but adds two things CreateMerge doesn't do:
		//  - Purely-whitespace-only conflicts auto-resolve instead of producing markers,
		//    mirroring KDiff3's --cs "WhiteSpace3FileMergeDefault=2" (verified against the
		//    KDiff3 source: value 2 means "always pick input B", which is oldText/oldLabel
		//    here, matching KDiff3.BuildArgs' own file order of vanilla/source1/source2 -
		//    see CLAUDE.md's KDiff3 compatibility notes).
		//  - Genuine conflicts are rendered as git/diff3-style conflict markers labeled
		//    with the actual mod names, not DiffPlex's generic "old"/"base"/"new".
		// Uses LineEndingsPreservingChunker (not DiffPlex's default LineChunker) so
		// unchanged/single-side-changed content round-trips through unmodified, keeping
		// each such line's original line-ending byte-for-byte - only synthetic content
		// this method itself adds (conflict marker lines) uses an explicit "\r\n" to
		// match vanilla .ws files' own DOS line endings (KDiff3.BuildArgs' own
		// --cs "LineEndStyle=1" - confirmed against the KDiff3 source, value 1 is DOS).
		public static MergeTextResult BuildMerge(string baseText, string oldText, string newText, string oldLabel, string newLabel)
		{
			// ThreeWayDiffer.CreateDiffs throws its own ArgumentNullException for a null
			// baseText/oldText/newText, but from inside DiffPlex rather than at this
			// method's own boundary - guard here instead so a caller gets a clear
			// exception pointing at this public entry point.
			if (baseText == null) throw new ArgumentNullException(nameof(baseText));
			if (oldText == null) throw new ArgumentNullException(nameof(oldText));
			if (newText == null) throw new ArgumentNullException(nameof(newText));

			var chunker = LineEndingsPreservingChunker.Instance;
			var diffResult = ThreeWayDiffer.Instance.CreateDiffs(
				baseText, oldText, newText, ignoreWhiteSpace: false, ignoreCase: false, chunker);

			var merged = new StringBuilder();
			var hasConflicts = false;

			var baseIndex = 0;
			var oldIndex = 0;
			var newIndex = 0;

			// CONFIRMED UPSTREAM BUG (DiffPlex 1.9.0), not a defect in this loop's own
			// bookkeeping: this loop is a faithful port of DiffPlex's own
			// ThreeWayDiffer.CreateMerge (same index-chasing shape), and DiffPlex's own
			// CreateMerge was verified - via a throwaway scratch console app per this
			// repo's testing convention, calling DiffPlex's ThreeWayDiffer.CreateMerge
			// directly - to exhibit the exact same two failure modes on the exact same
			// inputs, with both LineChunker (DiffPlex's own default/only-tested chunker
			// for 3-way diffs - its own Facts.DiffPlex/ThreeWayDifferFacts.cs never
			// exercises any other chunker) and LineEndingsPreservingChunker: when old-side
			// and new-side edits interleave/overlap relative to base in certain ways,
			// CreateThreeWayDiffBlocks can produce a block list whose OldCount/NewCount
			// don't actually correspond to the real PiecesOld/PiecesNew arrays. This
			// surfaces two ways: (1) an outright ArgumentOutOfRangeException from the
			// direct indexer accesses below, or (2) - confirmed via a minimal repro
			// (base "a();/b();/c();", one side inserts a line, the other independently
			// changes "b()" to "B()") - no exception at all, but content is silently
			// lost or duplicated, because the running oldIndex/newIndex end up not
			// matching PiecesOld.Count/PiecesNew.Count even though no single block's own
			// bookkeeping ever looked wrong in isolation. A large randomized stress test
			// (varying edit density and file length) measured combined failure rates from
			// ~0.35% (one independent single-line edit per side, 50-200 line files - the
			// closest analogue to a typical two-mod .ws conflict) up to double digits for
			// denser multi-edit-per-side cases - see CLAUDE.md's Compatibility
			// constraints for the full numbers. Given real, measured, non-negligible
			// rates of both failure modes, this is caught here (an exception) and
			// verified for (the silent case, via the post-loop count check below) rather
			// than trusted - MergeHeadless treats either as "needs manual resolution"
			// rather than ever risking corrupted merge output. This is also a primary
			// reason DiffPlexMergeEngine isn't the default engine yet (see Program.cs).
			try
			{
				foreach (var block in diffResult.DiffBlocks)
				{
					while (baseIndex < block.BaseStart)
					{
						merged.Append(diffResult.PiecesBase[baseIndex]);
						++baseIndex;
						++oldIndex;
						++newIndex;
					}

					switch (block.ChangeType)
					{
						case ThreeWayChangeType.Unchanged:
							for (var i = 0; i < block.BaseCount; ++i)
								merged.Append(diffResult.PiecesBase[baseIndex + i]);
							break;

						case ThreeWayChangeType.OldOnly:
							for (var i = 0; i < block.OldCount; ++i)
								merged.Append(diffResult.PiecesOld[oldIndex + i]);
							break;

						case ThreeWayChangeType.NewOnly:
							for (var i = 0; i < block.NewCount; ++i)
								merged.Append(diffResult.PiecesNew[newIndex + i]);
							break;

						case ThreeWayChangeType.BothSame:
							// Both sides made the same change - take either (old, matching
							// DiffPlex's own CreateMerge convention).
							for (var i = 0; i < block.OldCount; ++i)
								merged.Append(diffResult.PiecesOld[oldIndex + i]);
							break;

						case ThreeWayChangeType.Conflict:
							var oldPieces = diffResult.PiecesOld.Skip(oldIndex).Take(block.OldCount).ToList();
							var newPieces = diffResult.PiecesNew.Skip(newIndex).Take(block.NewCount).ToList();

							if (IsWhitespaceOnlyDifference(oldPieces, newPieces))
							{
								foreach (var piece in oldPieces)
									merged.Append(piece);
							}
							else
							{
								hasConflicts = true;
								var basePieces = diffResult.PiecesBase.Skip(baseIndex).Take(block.BaseCount).ToList();
								AppendConflictMarkers(merged, oldLabel, oldPieces, basePieces, newLabel, newPieces);
							}
							break;
					}

					baseIndex += block.BaseCount;
					oldIndex += block.OldCount;
					newIndex += block.NewCount;
				}

				while (baseIndex < diffResult.PiecesBase.Count)
				{
					merged.Append(diffResult.PiecesBase[baseIndex]);
					++baseIndex;
					++oldIndex;
					++newIndex;
				}
			}
			catch (ArgumentOutOfRangeException ex)
			{
				throw new DiffAlgorithmException(
					"DiffPlex's ThreeWayDiffer produced diff-block metadata that doesn't match " +
					"its own piece arrays for this file (" + ex.Message + ").");
			}

			// Even when nothing threw, the same underlying inconsistency can silently
			// produce WRONG merged content instead - confirmed via the minimal repro
			// described above, where oldIndex/newIndex end up one past
			// PiecesOld.Count/PiecesNew.Count with no exception anywhere. Verifying the
			// running counters actually landed on the true totals (rather than trusting
			// that "no exception" means "correct") is what catches that case.
			if (oldIndex != diffResult.PiecesOld.Count || newIndex != diffResult.PiecesNew.Count)
			{
				throw new DiffAlgorithmException(
					"DiffPlex's ThreeWayDiffer produced diff-block metadata that doesn't fully " +
					"(or doubly) account for this file's content, without throwing an exception.");
			}

			return new MergeTextResult(merged.ToString(), hasConflicts);
		}

		static void AppendConflictMarkers(
			StringBuilder merged,
			string oldLabel,
			List<string> oldPieces,
			List<string> basePieces,
			string newLabel,
			List<string> newPieces)
		{
			EnsureLineBreakBeforeMarker(merged);

			merged.Append("<<<<<<< ").Append(oldLabel).Append("\r\n");
			foreach (var piece in oldPieces)
				merged.Append(piece);
			EnsureLineBreakBeforeMarker(merged);

			merged.Append("||||||| Vanilla\r\n");
			foreach (var piece in basePieces)
				merged.Append(piece);
			EnsureLineBreakBeforeMarker(merged);

			merged.Append("=======\r\n");
			foreach (var piece in newPieces)
				merged.Append(piece);
			EnsureLineBreakBeforeMarker(merged);

			merged.Append(">>>>>>> ").Append(newLabel).Append("\r\n");
		}

		// Pieces from LineEndingsPreservingChunker only carry a line ending when the
		// original text had one at that point - a file (or a conflicting region right at
		// EOF) that doesn't end in a newline would otherwise glue a marker line onto the
		// preceding content instead of starting a new line. Checks for a trailing '\r'
		// as well as '\n': the original check only excluded '\n', so a region ending in
		// a lone '\r' (old Mac-style line ending, or a genuinely incomplete line) would
		// still get "\r\n" appended, producing a stray "\r\r\n" right before the marker
		// - flagged in code review, see CLAUDE.md.
		static void EnsureLineBreakBeforeMarker(StringBuilder sb)
		{
			if (sb.Length > 0 && sb[sb.Length - 1] != '\n' && sb[sb.Length - 1] != '\r')
				sb.Append("\r\n");
		}

		// KDiff3's WhiteSpace3FileMergeDefault only auto-resolves a conflict that's
		// "purely whitespace" - i.e. once whitespace differences are ignored entirely,
		// both sides agree. Comparing the whole joined-and-collapsed region (rather than
		// piece-by-piece) is deliberate: two sides can disagree on how many lines a
		// change spans (e.g. one side also adds a blank line) while still being
		// whitespace-equivalent overall - confirmed against DiffPlex's actual block
		// output in this change's verification scratch app, where such a case produces a
		// single Conflict block with different OldCount/NewCount. A stricter
		// element-wise comparison would misclassify that as a genuine conflict.
		static bool IsWhitespaceOnlyDifference(IReadOnlyList<string> oldPieces, IReadOnlyList<string> newPieces)
		{
			// A genuine deletion (one side has zero pieces in this region) must never be
			// treated as "whitespace-only", even if the surviving side's content happens
			// to collapse to "" once whitespace runs are stripped - confirmed via a
			// synthetic case: base has a whitespace-only separator line, one mod merely
			// trims its trailing spaces (still present, still blank), the other mod
			// deletes the line outright as part of a real edit. Both sides normalize to
			// "", which would otherwise misclassify a genuine content-vs-deletion
			// conflict as auto-resolvable and silently discard the deletion. If both
			// sides happen to have zero pieces (e.g. both independently deleted the same
			// region), this correctly falls through to producing empty conflict markers
			// rather than assuming anything about whether DiffPlex would even classify
			// that case as Conflict in the first place - see BuildMerge's comment on why
			// this library's block metadata isn't assumed trustworthy without checking.
			// Flagged in code review, see CLAUDE.md.
			if (oldPieces.Count == 0 || newPieces.Count == 0)
				return false;

			return NormalizeWhitespace(oldPieces) == NormalizeWhitespace(newPieces);
		}

		static string NormalizeWhitespace(IEnumerable<string> pieces)
		{
			return WhitespaceRun.Replace(string.Concat(pieces), " ").Trim();
		}

		#endregion
	}
}

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
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools
{
	// Return type for DiffPlexMergeEngine.Merge/MergeHeadless. Used to live in a deleted
	// IMergeEngine interface alongside a since-retired KDiff3MergeEngine (see
	// docs/decisions/kdiff3-retirement.md) - kept as its own small enum, rather than
	// deleted along with the interface, since FileMerger and this class's own test suite
	// both consume it as a stable return-type vocabulary independent of any interface.
	public enum MergeEngineResult
	{
		AutoSolved,
		NeedsManualResolution,
		Failed,
	}

	// The sole text-merge engine - in-process, external-binary-free, built on DiffPlex
	// (MIT-licensed NuGet package)'s ThreeWayDiffer. KDiff3 (formerly the default, and
	// before that the only, text-merge engine) was retired; see
	// docs/decisions/kdiff3-retirement.md for the full rationale and for the empirical
	// KDiff3 process-behavior findings preserved there now that KDiff3.cs itself is gone.
	//
	// FileMerger (Core) constructs and calls this class directly - there's no more
	// IMergeEngine interface indirection. That interface existed only so Core could reach
	// a text-merge engine without referencing Tools/KDiff3.cs's Win32 P/Invoke, which had
	// to stay in the host project; now that KDiff3MergeEngine is gone, DiffPlexMergeEngine
	// (already Core-side, same as FileMerger) is the only implementation there will ever
	// be, so the interface no longer bridges anything - keeping it would be premature
	// abstraction (an interface with exactly one implementation for its whole remaining
	// life). This isn't a documented repo-wide rule - a prior version of this comment
	// claimed CONTRIBUTING.md said as much, which code review caught as false (grep
	// confirms CONTRIBUTING.md never mentions abstraction, interfaces, or "premature" at
	// all) - it's this deletion's own reasoning, consistent with IMergeEngine.cs's own
	// former doc comment, which called itself "scaffolding... NOT meant as a permanent
	// pluggable-engine abstraction" and predicted its own removal once KDiff3 was gone.
	//
	// There's no UI here at all, so "interactive" and "headless" collapse to the same
	// underlying logic: Merge() just runs MergeHeadless() and maps NeedsManualResolution
	// to Failed, since Merge's contract forbids ever returning NeedsManualResolution
	// (that's a headless-only concept). This also means a genuine conflict's
	// conflict-marker sidecar gets written and opened (see MergeHeadless below) on the
	// interactive path too, since it's the exact same code path underneath.
	public class DiffPlexMergeEngine
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

		// Same exact character set as WhitespaceRun above, as a char[] rather than a
		// regex - used by NormalizeWhitespace's own Trim() call. A real bug, caught in
		// code review: NormalizeWhitespace used to call the parameterless string.Trim(),
		// which trims by char.IsWhiteSpace - the full Unicode whitespace category,
		// including NBSP - silently undoing this class's whole stated reason for using a
		// narrow ASCII-only regex in the first place, but only at the leading/trailing
		// edges of the joined, collapsed text (WhitespaceRun itself was always correctly
		// ASCII-only for internal runs). Concretely: oldPieces=["Hello\u00A0"] (a literal
		// U+00A0 NBSP, written out explicitly here since it's visually indistinguishable
		// from a plain space in most editors/diffs - the same reason the regression test
		// for this uses the same \u00A0 escape rather than a literal character),
		// newPieces=["Hello"] collapse to the same "Hello" after Trim() strips the edge
		// NBSP, silently auto-resolving as whitespace-only a case that should stay a
		// genuine conflict - exactly the NBSP-vs-space content-loss scenario the comment
		// above already warned about, just missed for the edges specifically.
		static readonly char[] WhitespaceChars = { ' ', '\t', '\r', '\n', '\f', '\v' };

		#endregion

		// Set (or reset to empty) at the start of every MergeHeadless call - see that
		// method's own comment. A single instance of this class is reused for a whole
		// headless run (FileMerger constructs one field), so this must never be left
		// holding a previous file's decisions when the current file never reaches the
		// function-level engine at all.
		public IReadOnlyList<string> LastFunctionLevelDecisions { get; private set; } = Array.Empty<string>();

		public MergeEngineResult Merge(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath,
			string oldDescription = null,
			string newDescription = null)
		{
			var result = MergeHeadless(source1, source2, vanillaFile, outputPath, oldDescription: oldDescription, newDescription: newDescription);
			return result == MergeEngineResult.NeedsManualResolution ? MergeEngineResult.Failed : result;
		}

		// openConflictMarkers defaults to true (matching Merge()'s interactive-path
		// behavior, and every pre-existing headless caller) - FileMerger.MergeTextHeadless
		// is the one caller that passes false, for a dry run (see MergeConflictsHeadless's
		// dryRun parameter): a dry run's whole contract is "preview only, no side effects
		// a user didn't ask for" (the MCP merge_conflicts tool's own dryRun description
		// promises no merged output, bundle repack, or MergeInventory.xml write), and
		// FileOpener.Open launching a real process is exactly that kind of surprise side
		// effect for an operation whose entire point is to be inspectable without
		// consequence - whatever the OS resolves that launch to, an editor if ".conflict"
		// has an association or its own "how do you want to open this?" picker if not
		// (confirmed both are possible - see MergeHeadless's own comment below). The
		// conflict-marker sidecar itself is still written either way (pre-existing
		// behavior, not something this parameter changes) - only the auto-open is
		// conditional, since that's the specific side effect that turns a preview into
		// something visibly disruptive (a window popping up per conflict for a mods
		// folder with many of them).
		public MergeEngineResult MergeHeadless(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath,
			bool openConflictMarkers = true,
			string oldDescription = null,
			string newDescription = null)
		{
			LastFunctionLevelDecisions = Array.Empty<string>();

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
			// defaults, is to refuse rather than guess. The now-retired KDiff3 engine had
			// no equivalent guard - it always attempted a real (if degraded, vanilla-less)
			// 2-way --auto merge in this situation instead of refusing outright, because
			// KDiff3 itself had a coherent notion of a 2-file diff/merge that DiffPlex's
			// ThreeWayDiffer, as used here, does not, so there was no equally meaningful
			// fallback to attempt (see docs/decisions/kdiff3-retirement.md). Not fixed by
			// building a parallel 2-way DiffPlex merge path here since that's new scope
			// beyond what this engine set out to replicate.
			if (!hasVanillaVersion)
			{
				AppState.Notifier.ShowMessage(
					$"Skipped {oldDescription ?? source1.Name} + {newDescription ?? source2.Name}: no vanilla version of this file could be found, " +
					"so a 3-way merge isn't possible.",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			// The now-retired KDiff3.RunHeadless applied this same "merging an updated mod
			// file into an existing merge chain" guard (see docs/decisions/kdiff3-retirement.md
			// for its reasoning, preserved there since the code that motivated it is gone).
			// Kept here rather than hoisted into FileMerger since that's shared
			// orchestration code outside this class's scope - a future change collapsing
			// this further could consider moving it there instead. Since Merge() (the
			// interactive path) just delegates straight to MergeHeadless() (see this
			// class's header comment - there's no UI to fall back to), this outdated-hash
			// case comes back as Failed on the interactive path too, same as every other
			// kind of conflict on this engine's interactive path.
			if (source1.TextFile.FullName.EqualsIgnoreCase(outputPath)
				&& source2.Hash != null && source2.Hash.IsOutdated)
			{
				AppState.Notifier.ShowMessage(
					$"Skipped {oldDescription ?? source1.Name} + {newDescription ?? source2.Name}: merging an updated mod file into a merge " +
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
				// Before giving up on the whole file: the function-level engine catches
				// this same exception per-function (see FunctionLevelMergeEngine) and
				// falls back to a whole-function tiebreak instead, so a file can still
				// merge even when the whole-file 3-way diff hits this bug. Only
				// attempted for .ws files - the extractor is WitcherScript-specific and
				// has no notion of XML structure.
				if (TryFunctionLevelRescue(baseText, oldText, newText, source1, source2, outputPath, oldDescription, newDescription, openConflictMarkers))
					return MergeEngineResult.AutoSolved;

				// DiffPlex's own diff algorithm produced output it isn't safe to trust
				// (see BuildMerge's comment) - don't write anything, including a sidecar:
				// the "conflict marker" content itself would have been built from the
				// same inconsistent piece indices, so it can't be trusted either. This is
				// the one case where DiffPlexMergeEngine can't offer a conflict-marker
				// starting point at all - genuinely needs the source files opened side by
				// side and compared by hand.
				AppState.Notifier.ShowMessage(
					$"Skipped {oldDescription ?? source1.Name} + {newDescription ?? source2.Name}: the automatic 3-way merge algorithm hit " +
					$"an internal inconsistency it couldn't safely recover from ({ex.Message}) - a known " +
					"limitation of the underlying DiffPlex library for certain multi-edit conflicts, see " +
					"CLAUDE.md. Needs manual resolution - open the source mod files directly to compare " +
					"and resolve.",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			if (!result.HasConflicts)
			{
				// DiffPlex's ThreeWayDiffer bug has a SILENT variant: a "clean" merge
				// whose output actually lost or duplicated content, with no exception
				// and no conflict block (see BuildMerge's comment - confirmed via
				// minimal repro, and observed live: a real 12-mod r4Player.ws chain had
				// mid-chain "clean" steps silently duplicate three functions, which
				// then made a later step's function-level rescue decline on the
				// ambiguity). Every clean output is therefore validated before being
				// trusted: structural sanity plus per-unit no-loss/no-duplication
				// invariants against the three inputs. A violation is treated exactly
				// like HasConflicts - try the function-level rescue (which re-merges
				// from the ORIGINAL inputs, sidestepping the corrupted output
				// entirely), then fall through to the conflict-marker sidecar.
				if (FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(
					baseText, oldText, newText, result.MergedText, outputPath, out var invariantViolation))
				{
					// A prior attempt at this same conflict may have left a sidecar
					// marker file behind (see below) - if this attempt now auto-solves
					// (e.g. the mod files were updated to no longer conflict), remove
					// it so it doesn't sit next to the fresh output indefinitely,
					// stale and misleading.
					DeleteIfExists(GetConflictMarkerPath(outputPath));

					FileEncoding.WriteUtf16Atomic(outputPath, result.MergedText);
					return MergeEngineResult.AutoSolved;
				}

				AppState.Notifier.ShowMessage(
					$"The whole-file merge of {oldDescription ?? source1.Name} + {newDescription ?? source2.Name} produced output that " +
					$"fails a content invariant ({invariantViolation}) - the known silent variant of the " +
					"DiffPlex ThreeWayDiffer bug (see CLAUDE.md). Falling back to the function-level merge.",
					"Merge Output Rejected", NotifyButtons.OK, DialogIcon.Warning);

				if (TryFunctionLevelRescue(baseText, oldText, newText, source1, source2, outputPath, oldDescription, newDescription, openConflictMarkers))
					return MergeEngineResult.AutoSolved;

				// Same policy as the DiffAlgorithmException branch above, for the same
				// reason: the "clean" output is corrupt, so writing it anywhere - as
				// the merged file OR as conflict-marker content - would ship the
				// corruption. Nothing is written.
				AppState.Notifier.ShowMessage(
					$"Skipped {oldDescription ?? source1.Name} + {newDescription ?? source2.Name}: the whole-file merge silently corrupted " +
					$"content ({invariantViolation}) and the function-level fallback declined. Needs manual " +
					"resolution - open the source mod files directly to compare and resolve.",
					"Skipped", NotifyButtons.OK, DialogIcon.Warning);
				return MergeEngineResult.NeedsManualResolution;
			}

			// Before falling back to conflict markers: same function-level rescue
			// attempt as the DiffAlgorithmException catch above, for the "produced
			// output, but with a real conflict block" case. See
			// FunctionLevelMergeEngine's own comment for why this is a fallback that
			// only ever activates where the whole-file merge has already failed, never
			// a parallel code path for merges that would have succeeded anyway.
			if (TryFunctionLevelRescue(baseText, oldText, newText, source1, source2, outputPath, oldDescription, newDescription, openConflictMarkers))
				return MergeEngineResult.AutoSolved;

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
			// location this method opens in the user's default editor below.
			var sidecarPath = GetConflictMarkerPath(outputPath);
			FileEncoding.WriteUtf16(sidecarPath, result.MergedText);

			// Path.GetFullPath here (message text only - the FileOpener.Open call below
			// still passes sidecarPath as-is) since Paths.DiffPlexConflictsDirectory is
			// relative (resolved against Environment.CurrentDirectory - see this class's
			// header comment / CLAUDE.md's "Interactive vs. headless split" section for
			// when that's pinned vs. not). A relative path in a user-facing message that
			// might be the only record of where to find a conflict is close to useless if
			// they read it later from a different working directory than the one that
			// wrote it - the absolute form is unambiguous regardless of when/where it's read.
			var sidecarFullPath = Path.GetFullPath(sidecarPath);

			// Opened BEFORE the notifier message, deliberately reordered from an earlier
			// version of this method (code review caught the problem with the original
			// order): the message needs FileOpener.Open's own bool return to pick its
			// wording, which requires calling it first. That bool distinguishes only
			// "Process.Start succeeded" from "Process.Start threw" (e.g.
			// ERROR_NO_ASSOCIATION, swallowed by FileOpener.TryOpen's own catch, returning
			// false) - it is NOT a guarantee an editor actually came up. Confirmed
			// empirically during this feature's own end-to-end verification: on the
			// machine used for testing, ".conflict" had no registered file association, so
			// Process.Start succeeded by launching OpenWith.exe (the OS's own "how do you
			// want to open this file?" picker) - opened came back true, and the message
			// below says "opened it for review" by its own definition of "opened" (the
			// call didn't fail), even though what the user actually sees is a picker
			// dialog, not directly an editor. Still strictly more honest than an
			// unconditional "attempting to open it now" regardless of outcome, which is
			// what this message used to say. Best-effort either way: a failed open doesn't
			// change the result below, the sidecar is on disk regardless.
			// Skipped entirely for a dry run (openConflictMarkers = false - see this
			// method's parameter comment), which also means dryRun's message always uses
			// the "open it manually" wording, never claims an open that was never
			// attempted. One consequence of this ordering worth stating plainly: on the
			// GUI's interactive path, AppState.Notifier.ShowMessage is a real blocking
			// modal, so the editor (if any) now opens BEHIND that modal instead of after
			// it - arguably better (the file's already up by the time the user dismisses
			// the dialog) but a deliberate change from this method's original "acknowledge
			// the skip, then the editor opens" sequencing, not an accident.
			var opened = openConflictMarkers && FileOpener.Open(sidecarPath);
			var openSuffix = !openConflictMarkers
				? " - open it manually to review (dry run preview)."
				: opened
					? " - opened it for review."
					: " - open it manually to review.";
			AppState.Notifier.ShowMessage(
				$"Skipped {oldDescription ?? source1.Name} + {newDescription ?? source2.Name}: genuine conflict, needs manual resolution. " +
				$"Conflict markers were written to {sidecarFullPath}{openSuffix}",
				"Skipped", NotifyButtons.OK, DialogIcon.Warning);

			return MergeEngineResult.NeedsManualResolution;
		}

		// No external executable - DiffPlex is an in-process managed library, so there's
		// nothing to validate a path for. No longer called from
		// Paths.ValidateDependencyPaths() (that call site was removed along with
		// IMergeEngine - QuickBMS/wcc_lite are still checked there directly, and remain
		// required regardless of the text-merge engine); kept here since it's directly
		// unit-tested and a trivially-true predicate costs nothing to keep around for any
		// future caller.
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
		// location: MergeHeadless calls this same method right before opening the file
		// (via FileOpener) for the exact path it just wrote.
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

		// The function-level fallback (see FunctionLevelMergeEngine's own header
		// comment). Called from both of MergeHeadless's give-up points, never from
		// anywhere else - this method's whole job is "try to do better than the
		// failure that's already about to happen," so every early return here means
		// "the caller proceeds exactly as if this method didn't exist," never a worse
		// outcome than today's baseline.
		//
		// oldDescription/newDescription default to source1.Name/source2.Name (the
		// pre-existing marker-label convention) when not supplied - accurate for a
		// merge chain's first pairwise step, but source1.Name resolves to the
		// accumulated-merge output's own mod name (e.g. the configured merged-mod
		// folder) from the second step onward, since FileMerger.MergeFlatConflictHeadless
		// reassigns source1 to the prior step's output file. FileMerger passes a
		// richer "accumulated merge (modA, modB)" description once it has more than
		// one real mod recorded for this chain (see MergeTextHeadless/
		// MergeTextInteractive) so a Decisions[] note reads sensibly past the first
		// step; this method doesn't know or care which case it's in.
		bool TryFunctionLevelRescue(
			string baseText, string oldText, string newText,
			FileMerger.MergeSource source1, FileMerger.MergeSource source2, string outputPath,
			string oldDescription, string newDescription, bool openConflictMarkers)
		{
			// ModFile.IsScript, not a locally reinvented extension check - the same
			// vocabulary every other file-category dispatch in Core uses for this exact
			// question. The extractor is WitcherScript-specific and has no notion of
			// XML structure, so .xml conflicts never reach it.
			if (!ModFile.IsScript(outputPath))
				return false;

			FunctionLevelMergeResult result;
			try
			{
				result = FunctionLevelMergeEngine.TryMerge(
					baseText, oldText, newText,
					source1.Name, source2.Name,
					oldDescription ?? source1.Name, newDescription ?? source2.Name);
			}
			catch (ScriptUnitExtractor.ExtractionException)
			{
				// The one expected, anticipated decline case (FunctionLevelMergeEngine.
				// TryMerge itself already narrows to this same exception type) -
				// genuinely just means this input doesn't parse cleanly, not a bug.
				return false;
			}
			catch (Exception ex)
			{
				// Anything else is a genuine defect in the new engine, not an
				// anticipated decline - still can't be allowed to regress this method
				// below its pre-existing behavior (the caller falls through to
				// whatever it was already about to do), but silently swallowing it
				// with zero trace would make such a bug permanently unmeasurable from
				// field reports alone. DialogIcon.Warning (not Information) so this
				// routes to stderr under HeadlessMergeNotifier, never stdout - stdout
				// carries MCP JSON-RPC frames only when running under the mcp verb,
				// and writing arbitrary text there would corrupt the protocol stream.
				AppState.Notifier.ShowMessage(
					$"Function-level merge rescue hit an unexpected error for {oldDescription ?? source1.Name} + {newDescription ?? source2.Name} " +
					$"({ex.GetType().Name}: {ex.Message}) - falling back to the whole-file result.",
					"Function-level rescue error", NotifyButtons.OK, DialogIcon.Warning);
				return false;
			}

			if (!result.Applied)
				return false;

			// The rescue's own output gets the same content-invariant validation as a
			// whole-file "clean" merge (duplication/loss/structure - see
			// ValidateWholeFileMergeOutput): an emission bug in the function-level
			// engine must fail loudly here, not ship. Declining is always safe - the
			// caller falls through to its existing sidecar/skip behavior.
			if (!FunctionLevelMergeEngine.ValidateWholeFileMergeOutput(
				baseText, oldText, newText, result.MergedText, outputPath, out var rescueViolation))
			{
				AppState.Notifier.ShowMessage(
					$"Function-level merge of {oldDescription ?? source1.Name} + {newDescription ?? source2.Name} produced output failing a " +
					$"content invariant ({rescueViolation}) - declining it rather than writing it.",
					"Function-Level Merge", NotifyButtons.OK, DialogIcon.Warning);
				return false;
			}

			DeleteIfExists(GetConflictMarkerPath(outputPath));
			FileEncoding.WriteUtf16Atomic(outputPath, result.MergedText);
			LastFunctionLevelDecisions = result.Decisions;

			if (result.Decisions.Count > 0)
			{
				// DialogIcon.Warning, not Information - see the unexpected-exception
				// branch above for why Information (which HeadlessMergeNotifier routes
				// to stdout) isn't safe here either; this message fires on every
				// successful rescue with decisions to report, including during a dry
				// run (openConflictMarkers is false only for the dry-run caller), so
				// it's reachable far more often than the exception-logging branch.
				var previewSuffix = openConflictMarkers ? "" : " (dry run preview - nothing was actually written)";
				// oldDescription/newDescription, not source1.Name/source2.Name: past a
				// merge chain's first step, source1 is the previous step's accumulated
				// temp file, whose MergeSource.Name resolves from its path (observed
				// mislabeling a dry run's flat-file chain as "Merged Bundle Content" -
				// a temp-root folder name - during live regression). The decision lines
				// inside this same message already used the caller-threaded
				// descriptions; the header just never caught up.
				AppState.Notifier.ShowMessage(
					$"Merged {oldDescription ?? source1.Name} + {newDescription ?? source2.Name} at the function level after the whole-file merge " +
					$"couldn't auto-solve it{previewSuffix}:\n\n" + string.Join("\n", result.Decisions),
					"Merged (function-level)", NotifyButtons.OK, DialogIcon.Warning);
			}

			return true;
		}

		#region Merge algorithm

		// The actual 3-way merge, factored out as a public static method (independent of
		// any FileMerger/MergeSource/disk I/O) specifically so it's directly unit
		// testable. Mirrors DiffPlex's own ThreeWayDiffer.CreateMerge loop (see its
		// source for the shape this follows) but adds two things CreateMerge doesn't do:
		//  - Purely-whitespace-only conflicts auto-resolve instead of producing markers,
		//    mirroring KDiff3's --cs "WhiteSpace3FileMergeDefault=2" (verified against the
		//    KDiff3 source: value 2 means "always pick input B", which is oldText/oldLabel
		//    here, matching the now-retired KDiff3 engine's own file order of
		//    vanilla/source1/source2 - see docs/decisions/kdiff3-retirement.md).
		//  - Genuine conflicts are rendered as git/diff3-style conflict markers labeled
		//    with the actual mod names, not DiffPlex's generic "old"/"base"/"new".
		// Uses LineEndingsPreservingChunker (not DiffPlex's default LineChunker) so
		// unchanged/single-side-changed content round-trips through unmodified, keeping
		// each such line's original line-ending byte-for-byte - only synthetic content
		// this method itself adds (conflict marker lines) uses an explicit "\r\n" to
		// match vanilla .ws files' own DOS line endings (the now-retired KDiff3 engine's
		// own --cs "LineEndStyle=1" - confirmed against the KDiff3 source, value 1 is DOS;
		// see docs/decisions/kdiff3-retirement.md).
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
			// rather than ever risking corrupted merge output. This measured, non-zero
			// failure rate is the primary reason retiring KDiff3 in favor of this engine
			// was a deliberate tradeoff, not a strict improvement - see
			// docs/decisions/kdiff3-retirement.md. (There used to be a separate
			// engine-selection switch in Program.cs that kept this engine non-default
			// specifically because of this gap - that switch and KDiff3 itself are both
			// gone now; this engine is the sole engine, gap and all.)
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

		// Internal, not private: FunctionLevelMergeEngine.NormalizeGap reuses this
		// directly (a single-element pieces array) rather than keeping its own second
		// copy of the same regex+trim logic - see that method's own comment for the
		// real NBSP-related regression duplicating it once already caused.
		internal static string NormalizeWhitespace(IEnumerable<string> pieces)
		{
			// Trim(WhitespaceChars), not the parameterless Trim() - see WhitespaceChars'
			// own comment for the real NBSP-related bug this guards against.
			return WhitespaceRun.Replace(string.Concat(pieces), " ").Trim(WhitespaceChars);
		}

		#endregion
	}
}

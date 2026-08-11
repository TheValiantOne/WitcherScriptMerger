using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Inventory
{
	// Core/host project split: this class used to mix TreeNode/BackgroundWorker-driven
	// interactive methods with headless ones (MergeConflictsHeadless etc.) and
	// constructed WinForms report forms (MergeReportForm/PackReportForm) directly.
	// Neither TreeNode/BackgroundWorker nor Forms.* can appear here anymore, since this
	// class now lives in the WinForms-free Core project:
	//  - The interactive orchestration (MergeFilesInteractive et al.) takes plain
	//    InteractiveMergeRequest/MergeSource data instead of TreeNode[]. The host
	//    project's InteractiveMergeRunner (Inventory/InteractiveMergeRunner.cs) extracts
	//    that data from TreeNodes, owns the BackgroundWorker, and is the thing MainForm
	//    actually talks to - its public API shape deliberately mirrors what this class
	//    used to expose (MergeByTreeNodesAsync/RepackBundleAsync), so MainForm's call
	//    sites barely changed.
	//  - Report-form popups and completion sounds (System.Media.SystemSounds, also not
	//    something Core should depend on) are host-only concerns now: this class calls
	//    OnMergeReport/OnPackReport after a successful interactive merge/pack, and
	//    InteractiveMergeRunner supplies callbacks that build the real forms, call
	//    MainForm.ShowModal, and play the sound - all exactly where the old inline
	//    `using (var reportForm = ...) { ShowModal }` blocks used to run.
	//  - Text-merge invocation goes through Tools/DiffPlexMergeEngine.cs directly - KDiff3
	//    (and the IMergeEngine interface that used to sit between this class and it) was
	//    retired; see docs/decisions/kdiff3-retirement.md.
	public class FileMerger
	{
		#region Types

		public struct MergeSource
		{
			public FileInfo TextFile;
			public FileInfo Bundle;
			public FileHash Hash;
			public string Name;

			public static MergeSource FromFlatFile(FileInfo file, FileHash hash)
				=> Create(file, hash, false);

			public static MergeSource FromBundle(FileInfo file, FileHash hash)
				=> Create(file, hash, true);

			static MergeSource Create(FileInfo file, FileHash hash, bool isBundle)
				=> new MergeSource
				{
					TextFile = isBundle ? null : file,
					Bundle = isBundle ? file : null,
					Hash = hash,
					Name = ModFile.GetModNameFromPath(file.FullName)
				};
		}

		public class HeadlessMergeSummary
		{
			public List<string> Merged { get; } = new List<string>();
			public List<string> Skipped { get; } = new List<string>();
			// Audit notes from FunctionLevelMergeEngine (see DiffPlexMergeEngine.
			// TryFunctionLevelRescue), each prefixed with the file it belongs to -
			// empty whenever no conflict in this run needed the function-level
			// fallback at all, which is the common case.
			public List<string> FunctionLevelDecisions { get; } = new List<string>();
		}

		// One file's interactive merge request, extracted by the host project's
		// InteractiveMergeRunner from checked TreeNodes so this class never sees a
		// TreeNode. OrderedSources[i].Name is only read (via the ConfirmRemainingConflict
		// gate below) before any merging starts for this file, while every element is
		// still an original per-mod source - MergeFlatFileInteractive/
		// MergeBundleFileInteractive only ever reassign a local loop variable to an
		// intermediate merge result, never an element of this array, so
		// ModFile.GetModNameFromPath's Paths.MergedBundleContent fallback (for a source
		// that isn't an original per-mod file) never applies at that read site.
		public class InteractiveMergeRequest
		{
			public string RelativePath;
			public bool IsBundle;
			public string VanillaFilePath;  // null for bundle-category files
			public MergeSource[] OrderedSources;
		}

		// Handed to OnMergeReport after each successful interactive pairwise text
		// merge, so the host project can build a MergeReportForm - Core has no Forms.*
		// types to build one itself.
		public class MergeReportData
		{
			public int MergeNum;
			public int TotalMergeCount;
			public string Source1Path;
			public string Source2Path;
			public string OutputPath;
			public string Source1Name;
			public string Source2Name;
		}

		#endregion

		#region Members

		public MergeProgressInfo ProgressInfo { get; private set; }

		// Invoked after a successful interactive merge/bundle pack. Only ever set (and
		// only ever invoked) on the interactive path - MergeConflictsHeadless never
		// touches these. See InteractiveMergeRunner.cs for what the host project's
		// callbacks actually do (report forms, completion sounds).
		public Action<MergeReportData> OnMergeReport { get; set; }
		public Action<string> OnPackReport { get; set; }

		MergeInventory _inventory;
		FileInfo _vanillaFile;
		string _mergedModName;
		string _outputPath;

		// The sole text-merge engine (see DiffPlexMergeEngine.cs's own header comment for
		// why this is a direct field rather than an injected IMergeEngine - that interface
		// was deleted along with KDiff3MergeEngine, its only other implementation). Not
		// exposed as a public property: nothing outside this class has ever needed to read
		// or replace it, unlike when this was an injected dependency selected once at
		// startup (Program.Main used to choose between two implementations here).
		DiffPlexMergeEngine _mergeEngine = new DiffPlexMergeEngine();

		bool _bundleChanged;
		List<Merge> _pendingBundleMerges = new List<Merge>();

		// Keyed by relativePath (case-insensitive, matching merge.RelativePath's own
		// comparison convention elsewhere in this class), drained into
		// HeadlessMergeSummary.FunctionLevelDecisions at the end of
		// MergeConflictsHeadless - but ONLY for paths that end up in summary.Merged,
		// not summary.Skipped. Appended to (not overwritten) right after every
		// MergeTextHeadless call, since _mergeEngine.LastFunctionLevelDecisions only
		// reflects the single most recent pairwise MergeHeadless call - a multi-mod
		// chain can trigger the function-level rescue at more than one step, and each
		// one's decisions would otherwise be lost the moment the next chain step's
		// MergeHeadless call resets that property back to empty.
		//
		// Keyed rather than a flat list (an earlier version of this field was a flat
		// List<string>, unconditionally drained in full) because a chain can record
		// real decisions for an EARLIER successful step and then fail at a LATER step
		// (or, for a bundle, succeed at the text-merge level but fail its later
		// blob0.bundle repack) - in either case the file ends up in summary.Skipped,
		// and a flat, always-drained list would still report those decisions for a
		// file that was never actually merged, contradicting the skipped/merged split
		// a caller (e.g. the Vortex extension's merge panel) relies on. Keying by
		// relativePath lets the drain step at the end of MergeConflictsHeadless include
		// only the entries for paths that actually made it into summary.Merged.
		Dictionary<string, List<string>> _functionLevelDecisionsByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

		// Anchored at BOTH ends ("^...$") - see IsVanillaDlcBundleFolder's own comment
		// below for why this matters: it's matched against just the extracted folder-name
		// segment, not the full path, so a full-string match is required, not merely a
		// suffix. Code review on the AdditionalVanillaDlcFolderNames addition caught that
		// an earlier, end-anchor-only version of this pattern ("(DLC[0-9]*|ep[0-9]|bob)$",
		// matched against the full path with no start anchor) would satisfy .NET Regex's
		// "match anywhere in the string" default for ANY folder name merely ending in one
		// of those substrings - confirmed to incorrectly match e.g. "ImmersiveDLC" (ends
		// in "DLC") or "Step1" (ends in "ep1") - exactly the kind of arbitrary,
		// attacker/mod-author-chosen folder name this allowlist's own doc comment warns
		// about (a Vortex "witcher3dlc"-deployed mod folder with such a name would have
		// silently qualified as a vanilla merge baseline).
		static readonly Regex VanillaDlcBundleFolderPattern =
			new Regex(@"^(DLC[0-9]*|ep[0-9]|bob)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Matches a DLC-folder name that has its own "bundles" subfolder to search for a
		// vanilla bundle: base-game expansions (DLC1, DLC2, ...), the "ep1"/"ep2" xpac
		// folders, and "bob" - Blood & Wine's internal folder codename (see this
		// project's CLAUDE.md, "Vortex-fork parity fixes" section, for the fork
		// comparison this was found against). IgnoreCase because real on-disk folder
		// names vary in casing across different game/mod installs - e.g. a repacked or
		// differently-sourced "Bob"/"BOB" folder - and .NET's Regex is case-sensitive by
		// default regardless of the underlying filesystem, so the prior case-sensitive
		// match could silently miss a real vanilla DLC folder on any platform, not just
		// a case-sensitive one. Public and static (no instance state involved)
		// specifically so it's directly unit testable, matching
		// DiffPlexMergeEngine.BuildMerge's own reasoning for the same shape. Forwards to
		// the two-arg overload below with an empty extra-names list, so every existing
		// caller/test keeps this regex-only behavior unchanged.
		public static bool IsVanillaDlcBundleFolder(string path) =>
			IsVanillaDlcBundleFolder(path, Array.Empty<string>());

		// Extends the regex match above with an exact (case-insensitive) match of the
		// trailing path segment against a caller-supplied allowlist, read from the
		// "AdditionalVanillaDlcFolderNames" App.config setting (see GetUnpackedFiles'
		// call site below) - the escape hatch for a future DLC/expansion whose folder
		// codename isn't known yet (e.g. CD Projekt Red's "Songs of the Past", announced
		// in 2026 with no folder name public as of this writing) without needing a code
		// change here every time it happens. Deliberately stays an exact-match
		// ALLOWLIST, never existence-based auto-discovery: Vortex's own "witcher3dlc" mod
		// type deploys ordinary user mods into "<GameDir>\DLC\<modname>\content\..." -
		// the identical on-disk shape as real vanilla DLC content - so treating "any
		// folder under DLC" as a vanilla baseline would risk silently merging against a
		// mod's own bundle instead of vanilla's, producing a silently wrong 3-way merge.
		// Deliberately takes the extra names as a plain parameter rather than reading
		// AppState.Settings itself, so this stays a pure, static, directly
		// unit-testable function with no config/AppState dependency - see this project's
		// CLAUDE.md and WitcherScriptMerger.Tests/CLAUDE.md for why touching
		// AppState.Settings from code a test exercises is a real hazard (AppSettings'
		// constructor calls Environment.Exit(1) when no config file is found next to the
		// entry assembly, which is fatal to the whole `dotnet test` process, not just one
		// test).
		//
		// The path is reduced to just its trailing folder-name segment ONCE, up front,
		// and both the regex check and the allowlist check run against that same
		// normalized value - deliberately not "regex against the raw path, allowlist
		// against the trimmed segment" (an earlier version of this method did exactly
		// that, an inconsistency code review also caught: a path with a trailing
		// separator would normalize differently for each branch).
		public static bool IsVanillaDlcBundleFolder(string path, IEnumerable<string> additionalFolderNames)
		{
			var folderName = Path.GetFileName(
				path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

			if (VanillaDlcBundleFolderPattern.IsMatch(folderName))
				return true;

			if (additionalFolderNames == null)
				return false;

			return additionalFolderNames.Any(name => !string.IsNullOrEmpty(name) && name.EqualsIgnoreCase(folderName));
		}

		#endregion

		public FileMerger(MergeInventory inventory)
		{
			_inventory = inventory;
			ProgressInfo = new MergeProgressInfo();
		}

		#region Interactive

		public void MergeFilesInteractive(IReadOnlyList<InteractiveMergeRequest> filesToMerge, string mergedModName)
		{
			_mergedModName = mergedModName;

			ProgressInfo.TotalMergeCount = filesToMerge.Sum(f => f.OrderedSources.Length - 1);
			ProgressInfo.TotalFileCount = filesToMerge.Count;

			for (int i = 0; i < filesToMerge.Count; ++i)
			{
				var file = filesToMerge[i];

				ProgressInfo.CurrentFileName = Path.GetFileName(file.RelativePath);
				ProgressInfo.CurrentFileNum = i + 1;
				ProgressInfo.CurrentAction = "Starting merge";

				if (file.OrderedSources.Any(source => (new LoadOrderComparer()).Compare(source.Name, _mergedModName) < 0) &&
					!ConfirmRemainingConflict(_mergedModName))
					continue;

				var isNew = false;
				var merge = _inventory.Merges.FirstOrDefault(m => m.RelativePath.EqualsIgnoreCase(file.RelativePath));
				if (merge == null)
				{
					isNew = true;
					merge = new Merge
					{
						RelativePath = file.RelativePath,
						MergedModName = _mergedModName
					};
				}

				if (file.IsBundle)
				{
					merge.BundleName = Path.GetFileName(Paths.RetrieveMergedBundlePath());
					MergeBundleFileInteractive(file, merge, isNew);
				}
				else
					MergeFlatFileInteractive(file, merge, isNew);
			}
			if (_bundleChanged)
			{
				var newBundlePath = PackNewBundle(Paths.RetrieveMergedBundlePath());
				if (newBundlePath != null)
				{
					ProgressInfo.CurrentAction = "Adding bundle merge to inventory";
					foreach (var bundleMerge in _pendingBundleMerges)
						_inventory.Merges.Add(bundleMerge);

					OnPackReport?.Invoke(newBundlePath);
				}
			}
			CleanUpTempFiles();
			CleanUpEmptyDirectories();
		}

		void MergeFlatFileInteractive(InteractiveMergeRequest file, Merge merge, bool isNew)
		{
			var source1 = file.OrderedSources[0];

			var relPath = Paths.GetRelativePath(
				source1.TextFile.FullName,
				Path.Combine(Paths.ModsDirectory, source1.Name));

			_outputPath = Path.Combine(Paths.ModsDirectory, _mergedModName, relPath);

			if (File.Exists(_outputPath) && !ConfirmOutputOverwrite(_outputPath))
				return;

			_vanillaFile = new FileInfo(file.VanillaFilePath);

			for (int i = 1; i < file.OrderedSources.Length; ++i)
			{
				++ProgressInfo.CurrentMergeNum;

				var source2 = file.OrderedSources[i];

				var oldDescription = DescribeAccumulated(file.OrderedSources.Take(i).Select(s => s.Name));
				var mergedFile = MergeTextInteractive(merge, source1, source2, oldDescription, source2.Name);
				if (mergedFile != null)
				{
					source1 = MergeSource.FromFlatFile(mergedFile, null);
				}
				else if (!ConfirmContinueAfterSkippedMerge(file.OrderedSources.Length - i - 1, merge))
					break;
			}

			if (isNew && merge.Mods.Count > 1)
			{
				ProgressInfo.CurrentAction = "Adding script merge to inventory";
				_inventory.Merges.Add(merge);
			}
		}

		void MergeBundleFileInteractive(InteractiveMergeRequest file, Merge merge, bool isNew)
		{
			_outputPath = Path.Combine(Paths.MergedBundleContent, file.RelativePath);

			if (File.Exists(_outputPath) && !ConfirmOutputOverwrite(_outputPath))
				return;

			_vanillaFile = null;

			var source1 = file.OrderedSources[0];

			for (int i = 1; i < file.OrderedSources.Length; ++i)
			{
				++ProgressInfo.CurrentMergeNum;

				var source2 = file.OrderedSources[i];

				if (!GetUnpackedFiles(file.RelativePath, ref source1, ref source2))
				{
					if (ConfirmContinueAfterSkippedMerge(file.OrderedSources.Length - i - 1, merge))
						continue;
					break;
				}

				var oldDescription = DescribeAccumulated(file.OrderedSources.Take(i).Select(s => s.Name));
				var mergedFile = MergeTextInteractive(merge, source1, source2, oldDescription, file.OrderedSources[i].Name);
				if (mergedFile != null)
				{
					source1 = MergeSource.FromFlatFile(mergedFile, null);
				}
				else if (!ConfirmContinueAfterSkippedMerge(file.OrderedSources.Length - i - 1, merge))
					break;
			}

			if (merge.BundleName != null && isNew && merge.Mods.Count > 1)
			{
				_bundleChanged = true;
				_pendingBundleMerges.Add(merge);
			}
		}

		FileInfo MergeTextInteractive(Merge merge, MergeSource source1, MergeSource source2, string oldDescription = null, string newDescription = null)
		{
			// Deliberately engine-neutral wording rather than naming KDiff3 explicitly
			// ("waiting for KDiff3 to close") - no external process or window is involved
			// at all with DiffPlexMergeEngine.
			ProgressInfo.CurrentAction = $"Merging {source1.Name} && {source2.Name}";

			var result = _mergeEngine.Merge(source1, source2, _vanillaFile, _outputPath, oldDescription, newDescription);

			if (result != MergeEngineResult.AutoSolved)
				return null;

			RecordMergedSources(merge, source1, source2);

			OnMergeReport?.Invoke(new MergeReportData
			{
				MergeNum = ProgressInfo.CurrentMergeNum,
				TotalMergeCount = ProgressInfo.TotalMergeCount,
				Source1Path = source1.TextFile.FullName,
				Source2Path = source2.TextFile.FullName,
				OutputPath = _outputPath,
				Source1Name = source1.Name,
				Source2Name = source2.Name,
			});

			return new FileInfo(_outputPath);
		}

		// Synchronous - the host project's InteractiveMergeRunner runs this on its own
		// BackgroundWorker, same as it did when this logic lived directly in
		// RepackBundleAsync.
		public string RepackBundle(string bundlePath)
		{
			var newBundlePath = PackNewBundle(bundlePath, isRepack: true);
			if (newBundlePath != null)
				OnPackReport?.Invoke(newBundlePath);
			return newBundlePath;
		}

		bool ConfirmRemainingConflict(string mergedModName)
		{
			return (NotifyResult.Yes == AppState.Notifier.ShowMessage(
				"There will still be a conflict if you use the merged mod name " + mergedModName + ".\n\n" +
					"The Witcher 3 loads mods in case-insensitive ASCII order, " +
					"so this merged mod name will load after one of the original mods, " +
					"and the merged file will be ignored.\n\n" +
					"Use this name anyway?",
				"Merged Mod Name Conflict",
				NotifyButtons.YesNo,
				DialogIcon.Exclamation));
		}

		// Returns false when the caller should stop trying further merges for this
		// file (user declined to continue past a skipped/failed merge).
		//
		// Named/worded around "skipped", not "canceled", after code review caught a real
		// mislabeling: this fires whenever MergeTextInteractive returns null, which used
		// to mean "the user canceled out of KDiff3's GUI" (a true cancellation, since
		// KDiff3's interactive path really did hand control to the user) but now, with
		// DiffPlexMergeEngine, means "the engine automatically refused this pairing"
		// (genuine conflict, missing vanilla file, outdated-hash guard, or a caught
		// DiffAlgorithmException) - DiffPlexMergeEngine.Merge() has no UI at all, so there
		// is no longer any user action for "canceled" to describe here. The engine
		// already showed its own explanatory modal (via AppState.Notifier) before
		// returning, so this second prompt only needs to ask whether to continue with any
		// remaining merges for the file - describing what already happened as "skipped"
		// keeps that prompt accurate instead of misattributing an automatic refusal to
		// the user.
		//
		// When remainingMergesForFile is 0, this shows a bare OK-only acknowledgment with
		// no real decision attached (there's nothing left to continue to) - back-to-back
		// with DiffPlexMergeEngine's own explanatory modal for a MergeTextInteractive
		// failure, that's a genuinely redundant second dialog. Deliberately not
		// special-cased away, though: this method has a second call site
		// (MergeBundleFileInteractive, on a GetUnpackedFiles failure) where nothing else
		// shows any explanatory message first - GetUnpackedFiles itself is silent on
		// failure - so this modal is the ONLY acknowledgment the user gets in that case.
		// Suppressing it whenever remainingMergesForFile is 0 would fix the redundant
		// case but silently drop the only feedback in the other one; distinguishing them
		// would need this method to know which failure path it's covering, which isn't
		// worth the extra plumbing just to save one OK click in the already-explained
		// case.
		bool ConfirmContinueAfterSkippedMerge(int remainingMergesForFile, Merge merge)
		{
			var msg = $"Merge {ProgressInfo.CurrentMergeNum} of {ProgressInfo.TotalMergeCount} was skipped.";
			var buttons = NotifyButtons.OK;
			if (remainingMergesForFile > 0)
			{
				var fileName = Path.GetFileName(merge.RelativePath);
				msg += $"\n\nContinue with {remainingMergesForFile} remaining merge{remainingMergesForFile.GetPluralS()} for file {fileName}?";
				buttons = NotifyButtons.YesNo;
			}
			var result = AppState.Notifier.ShowMessage(msg, "Skipped Merge", buttons, DialogIcon.Information);
			if (result == NotifyResult.No)
			{
				ProgressInfo.CurrentMergeNum += remainingMergesForFile;
				return false;
			}
			return true;
		}

		#endregion

		#region Headless

		// Headless equivalent of MergeFlatFileInteractive/MergeBundleFileInteractive,
		// driven by plain ModFile/FileHash data (FileIndex/ModFileIndex.Conflicts)
		// instead of InteractiveMergeRequest - those already carry everything needed
		// (relative path, category, per-mod name and hash), so no TreeNode is ever
		// involved on this path either.
		public HeadlessMergeSummary MergeConflictsHeadless(
			IEnumerable<ModFile> conflicts,
			string mergedModName,
			IReadOnlyDictionary<string, string[]> orderOverrides,
			bool dryRun = false,
			bool overwrite = false)
		{
			var summary = new HeadlessMergeSummary();

			foreach (var conflict in conflicts.Where(c =>
				c.Category == Categories.Script || c.Category == Categories.Xml || c.Category == Categories.BundleText))
			{
				var orderedNames = ResolveMergeOrder(conflict, mergedModName, orderOverrides);
				if (orderedNames == null)
				{
					summary.Skipped.Add(conflict.RelativePath);
					continue;
				}

				// A dry run always merges into a throwaway record instead of one pulled
				// from _inventory.Merges - that way nothing this pass does to `merge`
				// (BundleName, recorded source hashes via RecordMergedSources) can ever
				// mutate a live object still referenced by the loaded inventory, even if
				// some other code path calls Save() later. isNew is irrelevant for a dry
				// run since the block below that would add it to the inventory is itself
				// skipped for dry runs.
				var isNew = false;
				Merge merge = null;
				if (!dryRun)
					merge = _inventory.Merges.FirstOrDefault(m => m.RelativePath.EqualsIgnoreCase(conflict.RelativePath));
				if (merge == null)
				{
					isNew = true;
					merge = new Merge
					{
						RelativePath = conflict.RelativePath,
						MergedModName = mergedModName
					};
				}

				var isBundle = conflict.Category == Categories.BundleText;
				var fullyMerged = isBundle
					? MergeBundleConflictHeadless(conflict, merge, orderedNames, dryRun, overwrite)
					: MergeFlatConflictHeadless(conflict, merge, mergedModName, orderedNames, dryRun, overwrite);

				if (!fullyMerged)
				{
					summary.Skipped.Add(conflict.RelativePath);
					continue;
				}

				// Dry run never adds to the inventory or flags the bundle as needing a
				// repack - it only reports what *would* happen, so PackNewBundle (which
				// overwrites the real blob0.bundle) never runs for one either.
				if (!dryRun && isNew && merge.Mods.Count > 1)
				{
					if (isBundle)
					{
						_bundleChanged = true;
						_pendingBundleMerges.Add(merge);
					}
					else
						_inventory.Merges.Add(merge);
				}
				summary.Merged.Add(conflict.RelativePath);
			}

			if (_bundleChanged)
			{
				var newBundlePath = PackNewBundle(Paths.RetrieveMergedBundlePath());
				if (newBundlePath != null)
				{
					foreach (var bundleMerge in _pendingBundleMerges)
						_inventory.Merges.Add(bundleMerge);
				}
				else
				{
					// Content merged fine, but repacking blob0.bundle failed - those
					// conflicts didn't actually make it into a usable merge.
					foreach (var bundleMerge in _pendingBundleMerges)
					{
						summary.Merged.Remove(bundleMerge.RelativePath);
						summary.Skipped.Add(bundleMerge.RelativePath);
					}
				}
			}

			// Only for paths that survived to summary.Merged - see
			// _functionLevelDecisionsByPath's own comment for why a flat, unconditional
			// drain here would misattribute decisions to a file that ultimately failed
			// (a later chain step, or a bundle repack, both handled above this point).
			// Uses summary.Merged's own casing for the output prefix, not
			// merge.RelativePath's - `merge` can be an existing record pulled from
			// _inventory.Merges via a case-insensitive match (see the isNew branch
			// above), whose stored RelativePath could differ in casing from the
			// freshly-scanned conflict.RelativePath that summary.Merged actually holds;
			// the dictionary lookup itself is case-insensitive either way.
			foreach (var relativePath in summary.Merged)
			{
				if (_functionLevelDecisionsByPath.TryGetValue(relativePath, out var decisionsForThisPath))
					foreach (var decision in decisionsForThisPath)
						summary.FunctionLevelDecisions.Add(relativePath + ": " + decision);
			}

			CleanUpTempFiles();
			CleanUpEmptyDirectories();

			return summary;
		}

		bool MergeFlatConflictHeadless(ModFile conflict, Merge merge, string mergedModName, string[] orderedNames, bool dryRun, bool overwrite)
		{
			var firstHash = conflict.Mods.First(h => h.Name.EqualsIgnoreCase(orderedNames[0]));
			var source1 = MergeSource.FromFlatFile(new FileInfo(conflict.GetModFile(orderedNames[0])), firstHash);

			var relPath = Paths.GetRelativePath(
				source1.TextFile.FullName,
				Path.Combine(Paths.ModsDirectory, source1.Name));
			var realOutputPath = Path.Combine(Paths.ModsDirectory, mergedModName, relPath);

			// Checked against the real would-be output path regardless of dryRun, so a
			// preview predicts the same outcome the real run it previews would hit.
			// Without overwrite, an existing output is a clearly-reported skip (never a
			// YesNo prompt: headless runs have no one to answer it, and
			// HeadlessMergeNotifier's fixed non-destructive default made the old prompt
			// an unconditional, near-silent "no" - the skip reason lived only in stderr
			// prompt text, and headless/MCP callers could never refresh an existing
			// merge at all; see docs/bugs/function-level-merge-gap-handling.md's
			// "Related, lesser finding"). With overwrite, the existing output is
			// refreshed - the semantic a mod manager re-merging after a mod update
			// actually needs.
			if (!overwrite && File.Exists(realOutputPath))
			{
				ReportOutputExistsSkip(conflict.RelativePath, realOutputPath);
				return false;
			}

			// KDiff3 always physically writes its -o target on a successful solve - there's
			// no "check without writing" mode - so a dry run still needs somewhere real to
			// land. Routing it under TempBundleContent instead of the real mod output path
			// means CleanUpTempFiles() at the end of MergeConflictsHeadless deletes it
			// afterward, so nothing from a dry run is meant to survive past this call (best
			// -effort, like the rest of this method's cleanup - see CleanUpTempFiles).
			_outputPath = dryRun
				? Path.Combine(Paths.TempBundleContent, "DryRun", conflict.RelativePath)
				: realOutputPath;

			_vanillaFile = new FileInfo(conflict.GetVanillaFile());

			for (int i = 1; i < orderedNames.Length; ++i)
			{
				var hash = conflict.Mods.First(h => h.Name.EqualsIgnoreCase(orderedNames[i]));
				var source2 = MergeSource.FromFlatFile(new FileInfo(conflict.GetModFile(orderedNames[i])), hash);

				var mergedFile = MergeTextHeadless(merge, source1, source2, dryRun, DescribeAccumulated(orderedNames.Take(i)), orderedNames[i]);
				if (mergedFile == null)
					return false;
				source1 = MergeSource.FromFlatFile(mergedFile, null);
			}
			return true;
		}

		// The real mod names folded into "source1" so far within a merge chain, for
		// FunctionLevelMergeEngine's Decisions[] audit text (see DiffPlexMergeEngine.
		// TryFunctionLevelRescue's own comment on why source1.Name alone is misleading
		// past a chain's first step - source1 becomes the prior step's accumulated
		// output, whose own MergeSource.Name resolves to the merged-mod folder, not a
		// real contributing mod). Deliberately takes namesSoFar fresh from the caller's
		// own already-authoritative ordered list (orderedNames.Take(i) / OrderedSources.
		// Take(i).Select(s => s.Name)) rather than a separately maintained list that
		// would just be redundantly re-deriving the same prefix.
		static string DescribeAccumulated(IEnumerable<string> namesSoFar)
		{
			var names = namesSoFar.ToList();
			return names.Count > 1 ? "accumulated merge (" + string.Join(", ", names) + ")" : names[0];
		}

		bool MergeBundleConflictHeadless(ModFile conflict, Merge merge, string[] orderedNames, bool dryRun, bool overwrite)
		{
			merge.BundleName = Path.GetFileName(Paths.RetrieveMergedBundlePath());

			var realOutputPath = Path.Combine(Paths.MergedBundleContent, conflict.RelativePath);

			// See the matching comment in MergeFlatConflictHeadless - same reasoning and
			// same overwrite semantics.
			if (!overwrite && File.Exists(realOutputPath))
			{
				ReportOutputExistsSkip(conflict.RelativePath, realOutputPath);
				return false;
			}

			// Rooted under TempBundleContent instead of MergedBundleContent for a dry run so
			// its intermediate merge text can never linger there either - see the matching
			// comment in MergeFlatConflictHeadless.
			_outputPath = dryRun
				? Path.Combine(Paths.TempBundleContent, "DryRun", conflict.RelativePath)
				: realOutputPath;

			_vanillaFile = null;

			var firstHash = conflict.Mods.First(h => h.Name.EqualsIgnoreCase(orderedNames[0]));
			var source1 = MergeSource.FromBundle(new FileInfo(conflict.GetModFile(orderedNames[0])), firstHash);

			for (int i = 1; i < orderedNames.Length; ++i)
			{
				var hash = conflict.Mods.First(h => h.Name.EqualsIgnoreCase(orderedNames[i]));
				var source2 = MergeSource.FromBundle(new FileInfo(conflict.GetModFile(orderedNames[i])), hash);

				if (!GetUnpackedFiles(conflict.RelativePath, ref source1, ref source2))
					return false;

				var mergedFile = MergeTextHeadless(merge, source1, source2, dryRun, DescribeAccumulated(orderedNames.Take(i)), orderedNames[i]);
				if (mergedFile == null)
					return false;
				source1 = MergeSource.FromFlatFile(mergedFile, null);
			}
			return true;
		}

		// Explicit order-file entries win; conflicts not listed fall back to the same
		// LoadOrderComparer ordering ConflictTree's default sort already uses
		// (Controls/SMTreeSorter.cs), so headless behavior matches the GUI's default
		// without needing every conflict spelled out.
		string[] ResolveMergeOrder(ModFile conflict, string mergedModName, IReadOnlyDictionary<string, string[]> orderOverrides)
		{
			if (orderOverrides != null && orderOverrides.TryGetValue(conflict.RelativePath, out var explicitOrder))
			{
				if (explicitOrder == null)
				{
					AppState.Notifier.ShowError($"Order file's mod list for {conflict.RelativePath} is null.");
					return null;
				}

				var unknown = explicitOrder.Where(name => !conflict.ContainsMod(name)).ToArray();
				if (unknown.Any())
				{
					AppState.Notifier.ShowError(
						$"Order file lists unknown mod(s) for {conflict.RelativePath}: " +
						string.Join(", ", unknown.Select(n => n ?? "(null)")));
					return null;
				}

				var distinctCount = explicitOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count();
				if (distinctCount != explicitOrder.Length)
				{
					AppState.Notifier.ShowError(
						$"Order file's mod list for {conflict.RelativePath} names the same mod more than once.");
					return null;
				}

				// A merge needs at least a pair to actually do anything - without this,
				// a conflict left with only one *real* source after excluding
				// mergedModName (e.g. every other source mod's file was since removed,
				// leaving only the previous merge output and one real mod) could pass an
				// override naming just that one real mod: nothing "missing", no
				// duplicate, but MergeFlatConflictHeadless's chain loop (starting at
				// index 1) would never run, silently reporting the file as fully merged
				// having merged nothing and written nothing.
				if (explicitOrder.Length < 2)
				{
					AppState.Notifier.ShowError(
						$"Order file's mod list for {conflict.RelativePath} must name at least two mods to merge.");
					return null;
				}

				// Every *real* source mod must be covered - omitting one would otherwise
				// merge an incomplete chain and still report the file as fully merged.
				// mergedModName itself doesn't count as a required source: once a file has
				// already been merged once, its own merged-mod folder re-enters
				// conflict.Mods as if it were a source (scan_conflicts's own description
				// warns clients about this), and the documented way to re-merge after a
				// source mod's file changes is to list just the real mods again - not to
				// also re-list the previous merge output.
				var requiredNames = conflict.Mods
					.Select(m => m.Name)
					.Where(name => !name.EqualsIgnoreCase(mergedModName))
					.ToArray();
				var missing = requiredNames.Where(name => !explicitOrder.Any(n => n.EqualsIgnoreCase(name))).ToArray();
				if (missing.Any())
				{
					AppState.Notifier.ShowError(
						$"Order file's mod list for {conflict.RelativePath} is missing conflicting mod(s): " +
						string.Join(", ", missing));
					return null;
				}

				return explicitOrder;
			}

			return conflict.Mods
				.Select(h => h.Name)
				.OrderBy(name => name, new LoadOrderComparer())
				.ToArray();
		}

		FileInfo MergeTextHeadless(Merge merge, MergeSource source1, MergeSource source2, bool dryRun, string oldDescription = null, string newDescription = null)
		{
			ProgressInfo.CurrentAction = $"Merging {source1.Name} && {source2.Name}";

			// openConflictMarkers: false for a dry run - a genuine conflict still writes
			// its conflict-marker sidecar (pre-existing behavior; see
			// DiffPlexMergeEngine.MergeHeadless's own comment), but must not launch a real
			// editor/process for what's supposed to be a side-effect-free preview. Without
			// this, MergeConflictsHeadless(dryRun: true) against a mods folder with N
			// genuine conflicts would pop open N editor windows - a real bug caught in
			// review before it shipped (see docs/decisions/kdiff3-retirement.md).
			var result = _mergeEngine.MergeHeadless(
				source1, source2, _vanillaFile, _outputPath, openConflictMarkers: !dryRun,
				oldDescription: oldDescription, newDescription: newDescription);

			if (_mergeEngine.LastFunctionLevelDecisions.Count > 0)
			{
				if (!_functionLevelDecisionsByPath.TryGetValue(merge.RelativePath, out var decisionsForThisPath))
					_functionLevelDecisionsByPath[merge.RelativePath] = decisionsForThisPath = new List<string>();
				decisionsForThisPath.AddRange(_mergeEngine.LastFunctionLevelDecisions);
			}

			if (result != MergeEngineResult.AutoSolved)
				return null;

			// Dry run only needs the auto-solve verdict above. `merge` is always a
			// throwaway object for a dry run (see MergeConflictsHeadless), so skipping
			// this is a second, independent guard rather than the only thing preventing a
			// recorded-hash mutation from reaching the loaded inventory.
			if (!dryRun)
				RecordMergedSources(merge, source1, source2);

			return new FileInfo(_outputPath);
		}

		#endregion

		#region Shared

		// Shared by MergeTextInteractive/MergeTextHeadless after a successful merge:
		// records each source's hash into the merge record, unless that source IS the
		// output file itself (the accumulated merge target from a previous pairwise
		// merge in the same multi-mod chain, not a distinct mod source) or lives under
		// MergedBundleContent (an intermediate bundle-merge byproduct, same reasoning).
		// MergeInventory.xml's hashes are load-bearing (see CLAUDE.md's Compatibility
		// constraints) - kept in one place so a future fix to this guard has one call
		// site to touch, not two that can silently drift apart.
		void RecordMergedSources(Merge merge, MergeSource source1, MergeSource source2)
		{
			if (!source1.TextFile.FullName.EqualsIgnoreCase(_outputPath)
				&& !source1.TextFile.FullName.StartsWithIgnoreCase(Paths.MergedBundleContentAbsolute))
			{
				_inventory.AddModToMerge(source1, merge);
			}

			if (!source2.TextFile.FullName.EqualsIgnoreCase(_outputPath)
				&& !source2.TextFile.FullName.StartsWithIgnoreCase(Paths.MergedBundleContentAbsolute))
			{
				_inventory.AddModToMerge(source2, merge);
			}
		}

		// Interactive-path prompt (MergeFlatFileInteractive/MergeBundleFileInteractive)
		// - a GUI user is present to actually answer it. The headless paths use
		// ReportOutputExistsSkip below instead: under HeadlessMergeNotifier this
		// prompt's fixed non-destructive default silently declined every overwrite,
		// which meant headless/MCP merge_conflicts could never refresh an existing
		// merge, with the reason buried in stderr prompt text (see docs/bugs/
		// function-level-merge-gap-handling.md's "Related, lesser finding").
		bool ConfirmOutputOverwrite(string outputPath)
		{
			return (NotifyResult.Yes == AppState.Notifier.ShowMessage(
				"The output file below already exists! Overwrite?\n\n" + outputPath,
				"Overwrite?",
				NotifyButtons.YesNo,
				DialogIcon.Exclamation));
		}

		// Headless-path counterpart to ConfirmOutputOverwrite: no prompt (there's no
		// one to answer it) - states the skip and the way out. The overwrite
		// parameter on MergeConflictsHeadless is what actually authorizes a refresh.
		static void ReportOutputExistsSkip(string relativePath, string outputPath)
		{
			AppState.Notifier.ShowMessage(
				"Skipped " + relativePath + ": merged output already exists.\n" + outputPath +
				"\nPass --overwrite (CLI) or overwrite: true (MCP merge_conflicts) to refresh it.",
				"Already Merged",
				NotifyButtons.OK,
				DialogIcon.Warning);
		}

		bool GetUnpackedFiles(string contentRelativePath, ref MergeSource source1, ref MergeSource source2)
		{
			if (_vanillaFile == null)
			{
				ProgressInfo.CurrentAction = "Searching for corresponding vanilla bundle";

				// Directory.GetDirectories throws DirectoryNotFoundException on a missing
				// root - guarded here (rather than assuming GameDirectory always has real
				// "content"/"DLC" subfolders) so a scratch/incomplete game tree degrades to
				// "no vanilla bundle found" (handled below, and ultimately by each
				// IMergeEngine as a graceful "needs manual resolution" skip - see
				// DiffPlexMergeEngine.MergeHeadless's hasVanillaVersion guard) instead of an
				// unhandled exception. Previously unreachable on the WinForms host, which
				// always gates bundle-category scanning behind Paths.ValidateDependencyPaths()
				// (and therefore a real game install) first - but WitcherScriptMerger.Headless
				// deliberately doesn't require QuickBMS/wcc_lite to attempt flat-file merges, so
				// a bundle conflict can now reach this code without one. Flagged in code review,
				// see CLAUDE.md.
				// Read once per search, here rather than inside IsVanillaDlcBundleFolder
				// itself, so that function stays a pure, static, AppState-free function
				// safely callable from tests - see its own comment above. Comma-separated
				// exact folder names (not regex fragments - see App.config's own
				// description of this key), split/trimmed/filtered the same way
				// ModFileIndex.GetIgnoredModNames already parses the pre-existing
				// "IgnoreModNames" setting - plus an extra TrimEnd of stray directory
				// separators a user might paste into the setting (e.g. "SongsOfThePast\"),
				// since IsVanillaDlcBundleFolder compares against an already
				// separator-trimmed folder name and would otherwise never match such an
				// entry.
				var additionalDlcFolderNames = AppState.Settings.Get("AdditionalVanillaDlcFolderNames")
					.Split(',')
					.Where(name => !string.IsNullOrWhiteSpace(name))
					.Select(name => name.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
					.ToArray();

				var bundleDirs =
					(Directory.Exists(Paths.BundlesDirectory)
						? Directory.GetDirectories(Paths.BundlesDirectory).Select(path => Path.Combine(path, "bundles"))
						: Enumerable.Empty<string>())
						.Concat(
							Directory.Exists(Paths.DlcDirectory)
								? Directory.GetDirectories(Paths.DlcDirectory)
									.Where(path => IsVanillaDlcBundleFolder(path, additionalDlcFolderNames))
									.Select(path => Path.Combine(path, Paths.BundleBase, "bundles"))
								: Enumerable.Empty<string>()
						)
						.Where(path => Directory.Exists(path))
						.OrderBy(path => path, new LoadOrderComparer())
						.ToArray();

				for (int i = bundleDirs.Length - 1; i >= 0; --i)  // Search vanilla directories in reverse
				{                                                 // order, as patches & DLC override content.
					var bundleFiles = Directory.GetFiles(bundleDirs[i], "*.bundle");
					foreach (var bundle in bundleFiles)
					{
						var contentPaths = QuickBms.GetBundleContentPaths(bundle);
						if (contentPaths.Any(path => path.EqualsIgnoreCase(contentRelativePath)))
						{
							_vanillaFile = new FileInfo(bundle);
							break;
						}
					}
					if (_vanillaFile != null)
						break;
				}
				if (_vanillaFile != null)
				{
					ProgressInfo.CurrentAction = "Unpacking vanilla bundle content file";
					var vanillaContentPath = UnpackFile(_vanillaFile.FullName, contentRelativePath, "Vanilla");
					_vanillaFile = new FileInfo(vanillaContentPath);
				}
			}

			if (source1.TextFile == null)
			{
				ProgressInfo.CurrentAction = $"Unpacking bundle content file for {source1.Name}";
				var modContentFile1 = UnpackFile(source1.Bundle.FullName, contentRelativePath, "Mod 1");
				if (modContentFile1 == null)
					return false;
				source1.TextFile = new FileInfo(modContentFile1);
			}
			ProgressInfo.CurrentAction = $"Unpacking bundle content file for {source2.Name}";
			var modContentFile2 = UnpackFile(source2.Bundle.FullName, contentRelativePath, "Mod 2");
			if (modContentFile2 == null)
				return false;
			source2.TextFile = new FileInfo(modContentFile2);
			return true;
		}

		string UnpackFile(string bundlePath, string contentRelativePath, string outputDirName)
		{
			var outputDir = Path.Combine(Paths.TempBundleContent, outputDirName);

			var exitCode = QuickBms.UnpackFile(bundlePath, contentRelativePath, outputDir);

			return exitCode == 0
				? Path.Combine(outputDir, contentRelativePath)
				: null;
		}

		string PackNewBundle(string bundlePath, bool isRepack = false)
		{
			ProgressInfo.CurrentPhase = (!isRepack ? "Packing Bundle" : "Repacking Bundle");
			ProgressInfo.CurrentAction = "Packing merged bundle content into new blob0.bundle";

			var outputDir = Path.GetDirectoryName(bundlePath);

			var exitCode = WccLite.PackBundle(Paths.MergedBundleContentAbsolute, outputDir);
			if (exitCode != 0)
				return null;

			ProgressInfo.CurrentAction = "Generating metadata.store for new blob0.bundle";

			exitCode = WccLite.GenerateMetadata(outputDir);
			if (exitCode != 0)
				return null;

			return bundlePath;
		}

		void CleanUpTempFiles()
		{
			if (!Directory.Exists(Paths.TempBundleContent))
				return;

			try
			{
				ProgressInfo.CurrentAction = "Deleting temporary unpacked bundle content";
				DeleteDirectory(Paths.TempBundleContent);
			}
			catch (Exception ex)
			{
				AppState.Notifier.ShowMessage(
					"Non-critical error: Failed to delete temporary unpacked bundle content.\n\n" + ex.Message,
					"Error",
					NotifyButtons.OK,
					DialogIcon.Warning);
			}
		}

		void CleanUpEmptyDirectories()
		{
			if (!Directory.Exists(Paths.MergedBundleContent))
				return;

			try
			{
				ProgressInfo.CurrentAction = "Deleting empty Merged Bundle Content directories";
				DeleteEmptyDirectories(Paths.MergedBundleContent);
			}
			catch (Exception ex)
			{
				AppState.Notifier.ShowMessage(
					"Non-critical error: Failed to delete empty Merged Bundle Content directories.\n\n" + ex.Message,
					"Error",
					NotifyButtons.OK,
					DialogIcon.Warning);
			}
		}

		/// <summary>
		/// Depth-first recursive delete, with handling for descendant
		/// directories open in Windows Explorer.
		/// </summary>
		void DeleteDirectory(string path)
		{
			foreach (var subdirPath in Directory.GetDirectories(path))
			{
				System.Threading.Thread.Sleep(1);
				DeleteDirectory(subdirPath);
			}

			try
			{
				System.Threading.Thread.Sleep(1);
				Directory.Delete(path, true);
			}
			catch (IOException)
			{
				System.Threading.Thread.Sleep(1);
				Directory.Delete(path, true);
			}
			catch (UnauthorizedAccessException)
			{
				System.Threading.Thread.Sleep(1);
				Directory.Delete(path, true);
			}
			catch (Exception)
			{
				throw;
			}
		}

		/// <summary>
		/// Deletes any subdirectories of the root that are empty, AS WELL AS the root itself, if it's empty.
		/// </summary>
		void DeleteEmptyDirectories(string rootPath)
		{
			foreach (string directory in Directory.GetDirectories(rootPath))
			{
				System.Threading.Thread.Sleep(1);
				DeleteEmptyDirectories(directory);
			}

			if (Directory.GetFiles(rootPath).Any() || Directory.GetDirectories(rootPath).Any())
				return;

			try
			{
				System.Threading.Thread.Sleep(1);
				DeleteDirectory(rootPath);
			}
			catch (Exception)
			{
				throw;
			}
		}

		#endregion
	}
}

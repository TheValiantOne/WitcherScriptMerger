using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Inventory
{
	public enum StaleMergeReason
	{
		// The <Merge> record's own output file isn't on disk. Until this was checked,
		// MergeInventory.HasResolvedConflict answered "resolved" for such a record
		// forever, so the conflict was never re-merged and the game silently loaded
		// whichever single mod won load order.
		MergedFileMissing,

		// A mod the record lists no longer has the file (uninstalled, or the file was
		// dropped by a mod update).
		SourceModFileMissing,

		// A mod the record lists is explicitly disabled in mods.settings, so the game
		// never loads it - its contribution to the merged output is dead weight.
		SourceModDisabled,
	}

	public sealed class StaleMergeRecord
	{
		public Merge Merge { get; }
		public StaleMergeReason Reason { get; }

		// The mod that triggered the finding; null for MergedFileMissing, which is about
		// the record's output rather than any one source.
		public string ModName { get; }

		public StaleMergeRecord(Merge merge, StaleMergeReason reason, string modName = null)
		{
			Merge = merge;
			Reason = reason;
			ModName = modName;
		}

		public string Describe()
		{
			switch (Reason)
			{
				case StaleMergeReason.MergedFileMissing:
					return $"{Merge.RelativePath}: recorded as merged, but the merged file is missing " +
						$"({SafeMergedPath()}) - the conflict is unresolved despite the inventory record.";
				case StaleMergeReason.SourceModFileMissing:
					return $"{Merge.RelativePath}: recorded source mod '{ModName}' no longer has this file.";
				case StaleMergeReason.SourceModDisabled:
					return $"{Merge.RelativePath}: recorded source mod '{ModName}' is disabled in mods.settings, " +
						"so its changes are merged in but the game would never have loaded them.";
				default:
					return $"{Merge.RelativePath}: stale ({Reason}).";
			}
		}

		string SafeMergedPath()
		{
			try { return Merge.GetMergedFile(); }
			catch (Exception) { return "path unavailable"; }
		}

		public override string ToString() => Describe();
	}

	// The inventory-hygiene rules the WinForms GUI has always applied in
	// MainForm.RefreshMergeTree() - "merged file missing", "source mod file missing",
	// "source mod disabled" - lifted out as pure, promptless predicates so the headless
	// CLI, the MCP tools and the Vortex extension can see the same staleness the GUI
	// does.
	//
	// Before this existed those three rules were GUI-only, which had a concrete
	// consequence on a real install: a <Merge> record for
	// game\vehicles\horse\states\exploration.ws outlived its output file, and because
	// HasResolvedConflict never checked that the output existed, every headless scan kept
	// answering alreadyResolved:true for a conflict that had no merged file at all.
	//
	// Note these are findings, not actions. Deciding what to do about a stale record is
	// the caller's - the GUI asks the user (ConfirmPruneMissingMergeFile and friends);
	// headless callers report it. That split is deliberate: the GUI's prompts pass no
	// defaultResult, so HeadlessMergeNotifier would answer its generic YesNo => No to
	// every one of them and prune nothing, silently.
	public static class MergeInventoryHygiene
	{
		// Whether a record's merged output is on disk.
		//
		// Bundle-content records are always reported as present. Their GetMergedFile()
		// resolves under Paths.MergedBundleContent, which is relative to the current
		// working directory and is scratch space between runs - absence there says
		// nothing about whether the merge is live, since the real artifact is the packed
		// bundle. Only flat files (.ws/.xml) have a stable, absolute output path under
		// the mods directory that "missing" is meaningful for, and flat files are the
		// only category either headless host supports at all.
		public static bool HasMergedFile(Merge merge)
		{
			if (merge == null)
				return true;
			if (merge.Category != Categories.Script && merge.Category != Categories.Xml)
				return true;

			try
			{
				return File.Exists(merge.GetMergedFile());
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
			{
				// Can't prove it's missing - don't claim it is.
				return true;
			}
		}

		// Every stale record in the inventory. A single record can yield more than one
		// finding (e.g. a missing output whose source mod is also disabled); callers that
		// only want one line per record can group by Merge.
		//
		// loadOrder may be null - the SourceModDisabled rule is simply not applied then,
		// which is the right behavior when mods.settings can't be read (a fresh install,
		// or a Linux host with no Documents\The Witcher 3 at all). CustomLoadOrder
		// already treats a mod absent from mods.settings as enabled, so a mod the game
		// would auto-add on next launch is never reported as disabled.
		public static IReadOnlyList<StaleMergeRecord> FindStale(MergeInventory inventory, CustomLoadOrder loadOrder = null)
		{
			var stale = new List<StaleMergeRecord>();
			if (inventory?.Merges == null)
				return stale;

			foreach (var merge in inventory.Merges)
			{
				if (merge == null)
					continue;

				if (!HasMergedFile(merge))
					stale.Add(new StaleMergeRecord(merge, StaleMergeReason.MergedFileMissing));

				foreach (var mod in merge.Mods ?? Enumerable.Empty<FileHash>())
				{
					if (mod?.Name == null)
						continue;

					try
					{
						if (!File.Exists(merge.GetModFile(mod.Name)))
							stale.Add(new StaleMergeRecord(merge, StaleMergeReason.SourceModFileMissing, mod.Name));
					}
					catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotImplementedException)
					{
						// Same reasoning as HasMergedFile: an unresolvable path isn't proof of absence.
					}

					if (loadOrder != null && loadOrder.IsModDisabledByName(mod.Name))
						stale.Add(new StaleMergeRecord(merge, StaleMergeReason.SourceModDisabled, mod.Name));
				}
			}

			return stale;
		}
	}
}

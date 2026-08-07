using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using ModelContextProtocol.Server;
using WitcherScriptMerger.Cli;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Mcp
{
	[McpServerToolType]
	public static class WsmMcpTools
	{
		// scan_conflicts and merge_conflicts both load-then-mutate the shared AppState.Inventory
		// (MergeInventory.xml is the load-bearing, string-hash-compared merge record - see
		// CLAUDE.md's Compatibility constraints). An MCP client can issue tool calls concurrently;
		// without serializing these two, one call's Load() can clobber another's in-flight
		// instance and Save() can write whatever happened to land in the field. Not a concern for
		// get_status/list_merges - neither touches AppState.Inventory.
		static readonly object _inventoryLock = new object();

		[McpServerTool(Name = "scan_conflicts"), Description(
			"Scans the configured mods folder and returns every detected file conflict: relative " +
			"path, category, each mod's file hash, the default merge order, and whether it's " +
			"already resolved by a recorded merge. Note: if a file was already merged, the merged-mod " +
			"folder itself can appear in mods/defaultOrder as if it were a source - don't echo " +
			"defaultOrder back verbatim as orderOverrides without checking alreadyResolved first.")]
		public static object ScanConflicts()
		{
			RequireDependenciesAndModsDirectory();

			lock (_inventoryLock)
			{
				AppState.Inventory = MergeInventory.Load(Paths.Inventory);
				var modIndex = MergeOperations.ScanConflicts();

				return modIndex.Conflicts.Select(c => new
				{
					relativePath = c.RelativePath,
					category = c.Category.DisplayName,
					mods = c.Mods.Select(h => new { name = h.Name, hash = h.Hash, isOutdated = h.IsOutdated }).ToArray(),
					defaultOrder = c.Mods.Select(h => h.Name).OrderBy(n => n, new LoadOrderComparer()).ToArray(),
					alreadyResolved = AppState.Inventory.HasResolvedConflict(c),
				}).ToArray();
			}
		}

		[McpServerTool(Name = "merge_conflicts"), Description(
			"Merges detected conflicts headlessly - never opens KDiff3's GUI; conflicts that can't " +
			"be auto-solved are skipped and reported, not merged. Restrict to specific files with " +
			"relativePaths (default: every detected conflict); override a file's mod merge order " +
			"with orderOverrides (default merge order otherwise matches the game's own load order).")]
		public static object MergeConflicts(
			[Description("Relative paths to merge; omit to merge every detected conflict.")] string[] relativePaths = null,
			[Description("Map of relative path to an explicit, ordered list of mod names for that file.")] Dictionary<string, string[]> orderOverrides = null)
		{
			RequireDependenciesAndModsDirectory();

			var mergedModName = Paths.RetrieveMergedModName();
			if (string.IsNullOrWhiteSpace(mergedModName))
				throw new InvalidOperationException("MergedModName isn't configured in App.config.");

			lock (_inventoryLock)
			{
				AppState.Inventory = MergeInventory.Load(Paths.Inventory);
				var modIndex = MergeOperations.ScanConflicts();

				var conflicts = relativePaths == null
					? modIndex.Conflicts
					: modIndex.Conflicts.Where(c => relativePaths.Any(p => p.EqualsIgnoreCase(c.RelativePath)));

				var summary = MergeOperations.RunMerge(AppState.Inventory, conflicts, mergedModName, orderOverrides);
				AppState.Inventory.Save();

				return new { merged = summary.Merged, skipped = summary.Skipped };
			}
		}

		[McpServerTool(Name = "get_status"), Description(
			"Reports WSM's current configuration and dependency status: resolved game/mods " +
			"directories, whether KDiff3/QuickBMS/wcc_lite are all found, the configured " +
			"merged-mod name, and the current conflict count.")]
		public static object GetStatus()
		{
			var dependenciesValid = Paths.ValidateDependencyPaths();
			var modsDirectoryExists = Directory.Exists(Paths.ModsDirectory);

			var conflictCount = 0;
			if (dependenciesValid && modsDirectoryExists)
				conflictCount = MergeOperations.ScanConflicts().Conflicts.Count();

			return new
			{
				gameDirectory = Paths.GameDirectory,
				modsDirectory = Paths.ModsDirectory,
				dependenciesValid,
				modsDirectoryExists,
				mergedModName = AppState.Settings.Get("MergedModName"),
				conflictCount,
			};
		}

		[McpServerTool(Name = "list_merges"), Description(
			"Lists every merge already recorded in MergeInventory.xml: relative path, which mod " +
			"folder holds the merged result, and each source mod's recorded hash.")]
		public static object ListMerges()
		{
			var inventory = MergeInventory.Load(Paths.Inventory);

			return inventory.Merges.Select(m => new
			{
				relativePath = m.RelativePath,
				mergedModName = m.MergedModName,
				mods = m.Mods.Select(h => new { name = h.Name, hash = h.Hash }).ToArray(),
			}).ToArray();
		}

		static void RequireDependenciesAndModsDirectory()
		{
			if (!Paths.ValidateDependencyPaths())
				throw new InvalidOperationException(
					"A required dependency (KDiff3, QuickBMS, or wcc_lite) is missing. Configure its path in App.config.");

			if (!Directory.Exists(Paths.ModsDirectory))
				throw new InvalidOperationException("Mods directory not found - check GameDirectory/ModsDirectory in App.config.");
		}
	}
}

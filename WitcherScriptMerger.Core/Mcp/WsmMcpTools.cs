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
			"Merges detected conflicts headlessly; conflicts that can't be auto-solved are skipped " +
			"and reported, not merged - a conflict-marker sidecar file is written and opened in the " +
			"default editor for manual review instead. Restrict to specific files with " +
			"relativePaths (default: every detected conflict); override a file's mod merge order " +
			"with orderOverrides (default merge order otherwise matches the game's own load order). " +
			"Set dryRun to preview which conflicts would auto-solve without writing any merged " +
			"output, repacking any bundle, or modifying MergeInventory.xml.")]
		public static object MergeConflicts(
			[Description("Relative paths to merge; omit to merge every detected conflict. Each must " +
				"resolve inside the configured mods directory - absolute paths, UNC paths, and " +
				"\"..\\\" segments that would escape it are rejected.")] string[] relativePaths = null,
			[Description("Map of relative path to an explicit, ordered list of mod names for that " +
				"file - at least two, no duplicates, and every one of that file's real source " +
				"mods must appear (the configured merged-mod name itself doesn't need to be " +
				"listed, even if it shows up as a source because the file was already merged " +
				"once).")] Dictionary<string, string[]> orderOverrides = null,
			[Description("If true, evaluates which conflicts would auto-solve without writing any " +
				"merged output, repacking any bundle, or modifying MergeInventory.xml.")] bool dryRun = false)
		{
			// Validated before touching the mods folder or dependency state: this is pure
			// input validation the caller controls, so it should fail fast and
			// independently of whatever else is or isn't configured.
			EnsureInScope(relativePaths, orderOverrides);

			RequireDependenciesAndModsDirectory();

			var mergedModName = Paths.RetrieveMergedModName();
			if (string.IsNullOrWhiteSpace(mergedModName))
				throw new InvalidOperationException("MergedModName isn't configured in App.config.");

			// ModFile.RelativePath always uses '\' (built via Path.Combine/GetRelativePath
			// on Windows). A client-supplied relativePaths entry using '/' already passes
			// IsWithinModsDirectory's scope check (Path.GetFullPath normalizes separators),
			// but a raw EqualsIgnoreCase against RelativePath below would not - normalize
			// here so an in-scope path in a different, still-valid separator style doesn't
			// silently fail to match its own conflict and land in `unmatched` looking like
			// it was never a conflict at all.
			var normalizedRelativePaths = relativePaths?.Select(p => p.Replace('/', '\\')).ToArray();

			lock (_inventoryLock)
			{
				// allowSave: !dryRun - MergeInventory.Load() can itself write to
				// MergeInventory.xml (AddMissingHashes backfilling an old record's null
				// hash) before dryRun is ever otherwise consulted; without this, a dry run
				// against a legacy inventory file could still touch disk despite the
				// tool's own "without... modifying MergeInventory.xml" description.
				AppState.Inventory = MergeInventory.Load(Paths.Inventory, allowSave: !dryRun);
				var modIndex = MergeOperations.ScanConflicts();

				var conflicts = (normalizedRelativePaths == null
					? modIndex.Conflicts
					: modIndex.Conflicts.Where(c => normalizedRelativePaths.Any(p => p.EqualsIgnoreCase(c.RelativePath))))
					.ToArray();

				// In-scope but no longer a detected conflict (e.g. resolved or removed
				// between scan and this call) is reported back, not silently dropped -
				// only an out-of-scope path (rejected above) is a hard error. Derived from
				// `conflicts` (already the relativePaths-matching subset) rather than
				// re-scanning the full modIndex.Conflicts a second time.
				var unmatched = normalizedRelativePaths == null
					? Array.Empty<string>()
					: normalizedRelativePaths.Where(p => !conflicts.Any(c => c.RelativePath.EqualsIgnoreCase(p))).ToArray();

				// orderOverrides keys are matched against conflict.RelativePath elsewhere
				// (FileMerger.ResolveMergeOrder) via a plain Dictionary lookup, which - built
				// from JSON with no comparer specified - is ordinal case-sensitive by
				// default and wouldn't tolerate a '/'-separated key either. Rebuilding it
				// here (case-insensitive comparer, '\' separators) keeps that lookup
				// consistent with every other path/name comparison in this codebase, so a
				// differently-cased or differently-separated but otherwise-correct key
				// isn't silently ignored.
				var normalizedOrderOverrides = orderOverrides == null
					? null
					: orderOverrides.ToDictionary(
						kv => kv.Key.Replace('/', '\\'),
						kv => kv.Value,
						StringComparer.OrdinalIgnoreCase);

				var summary = MergeOperations.RunMerge(AppState.Inventory, conflicts, mergedModName, normalizedOrderOverrides, dryRun);

				// FileMerger guarantees a dry run never adds or updates records in the
				// loaded inventory (see MergeConflictsHeadless), but skipping the disk
				// write too is a second, independent guarantee that a preview call can
				// never leave MergeInventory.xml touched, even if that were ever weakened.
				if (!dryRun)
					AppState.Inventory.Save();

				return new { merged = summary.Merged, skipped = summary.Skipped, unmatched, dryRun };
			}
		}

		[McpServerTool(Name = "get_status"), Description(
			"Reports WSM's current configuration and dependency status: resolved game/mods " +
			"directories, whether QuickBMS/wcc_lite are all found, the configured " +
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
					"A required dependency (QuickBMS or wcc_lite) is missing. Configure its path in App.config.");

			if (!Directory.Exists(Paths.ModsDirectory))
				throw new InvalidOperationException("Mods directory not found - check GameDirectory/ModsDirectory in App.config.");
		}

		// Directory allow-listing for merge_conflicts's two path-shaped inputs. Neither is
		// currently joined into a filesystem path by itself - relativePaths is only ever
		// compared for equality against already-scanned ModFile.RelativePath values
		// (WsmMcpTools.MergeConflicts, above), and orderOverrides keys are only ever
		// looked up the same way (FileMerger.ResolveMergeOrder) - so there's no live
		// traversal vector through either today. This exists as defense-in-depth against
		// that changing later, and so a malicious-looking value is rejected with a clear
		// error up front instead of just silently matching nothing.
		static void EnsureInScope(string[] relativePaths, Dictionary<string, string[]> orderOverrides)
		{
			var offenders = new List<string>();

			if (relativePaths != null)
				offenders.AddRange(relativePaths.Where(p => !IsWithinModsDirectory(p)).Select(p => p ?? "(null)"));

			if (orderOverrides != null)
				offenders.AddRange(orderOverrides.Keys.Where(k => !IsWithinModsDirectory(k)));

			if (offenders.Any())
				throw new ArgumentException(
					"The following path(s) are outside the configured mods directory and were rejected: " +
					string.Join(", ", offenders.Distinct()));
		}

		// A relative path is in scope only if resolving it against the configured mods
		// directory still lands inside that directory. Path.IsPathRooted rejects both
		// absolute paths and UNC paths outright (Path.Combine silently discards its first
		// argument when the second is rooted, which is exactly the bypass that check
		// closes). Path.GetFullPath then normalizes any "..\" segments before the
		// comparison, which is a proper prefix check against the fully-qualified root
		// (with a trailing separator, so "ModsDirectory" can't be spoofed by a sibling
		// directory like "ModsDirectoryEvil") rather than a naive string StartsWith.
		// This does NOT resolve symlinks - Path.GetFullPath doesn't either - but that's
		// fine here: this value is only ever compared against already-scanned
		// ModFile.RelativePath strings or used as a dictionary key, never opened
		// directly, so a symlink planted inside the mods directory can't be exploited
		// through this check.
		static bool IsWithinModsDirectory(string relativePath)
		{
			if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
				return false;

			var root = Paths.ModsDirectory;
			if (string.IsNullOrWhiteSpace(root))
				return false;

			string fullRoot, candidate;
			try
			{
				fullRoot = Path.GetFullPath(root);
				candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
			{
				return false;
			}

			var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
				? fullRoot
				: fullRoot + Path.DirectorySeparatorChar;

			return candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
				|| candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
		}
	}
}

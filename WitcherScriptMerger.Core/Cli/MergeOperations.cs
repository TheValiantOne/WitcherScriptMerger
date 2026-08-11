using System.Collections.Generic;
using System.Threading;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Cli
{
	// Shared scan/merge orchestration behind both the `merge` CLI verb (Program.cs) and the
	// MCP tools (Mcp/WsmMcpTools.cs) - see CLAUDE.md's CLI mode / MCP mode sections.
	public static class MergeOperations
	{
		public static ModFileIndex ScanConflicts()
		{
			var modIndex = new ModFileIndex();
			using (var scanComplete = new ManualResetEventSlim(false))
			{
				modIndex.BuildAsync(
					AppState.Settings.Get<bool>("CheckScripts"),
					AppState.Settings.Get<bool>("CheckXmlFiles"),
					AppState.Settings.Get<bool>("CheckBundleContents"),
					(s, e) => { },
					(s, e) => scanComplete.Set());
				scanComplete.Wait();
			}
			return modIndex;
		}

		public static FileMerger.HeadlessMergeSummary RunMerge(
			MergeInventory inventory,
			IEnumerable<ModFile> conflicts,
			string mergedModName,
			IReadOnlyDictionary<string, string[]> orderOverrides,
			bool dryRun = false,
			bool overwrite = false)
		{
			var merger = new FileMerger(inventory);
			return merger.MergeConflictsHeadless(conflicts, mergedModName, orderOverrides, dryRun, overwrite);
		}
	}
}

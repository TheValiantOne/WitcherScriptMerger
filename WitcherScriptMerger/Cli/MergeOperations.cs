using System.Collections.Generic;
using System.Threading;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Cli
{
	// Shared scan/merge orchestration behind both the `merge` CLI verb (Program.cs) and the
	// MCP tools (Mcp/WsmMcpTools.cs) - see CLAUDE.md's CLI mode / MCP mode sections.
	static class MergeOperations
	{
		public static ModFileIndex ScanConflicts()
		{
			var modIndex = new ModFileIndex();
			using (var scanComplete = new ManualResetEventSlim(false))
			{
				modIndex.BuildAsync(
					Program.Settings.Get<bool>("CheckScripts"),
					Program.Settings.Get<bool>("CheckXmlFiles"),
					Program.Settings.Get<bool>("CheckBundleContents"),
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
			IReadOnlyDictionary<string, string[]> orderOverrides)
		{
			var merger = new FileMerger(inventory, (s, e) => { }, (s, e) => { });
			return merger.MergeConflictsHeadless(conflicts, mergedModName, orderOverrides);
		}
	}
}

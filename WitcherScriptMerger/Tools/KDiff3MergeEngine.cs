using System.IO;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools
{
	// The one real IMergeEngine implementation - see Core's Tools/IMergeEngine.cs for
	// why this scaffolding exists. Just wraps the existing KDiff3.Run/RunHeadless
	// calls FileMerger (now in Core) used to make directly; all the real logic
	// (encoding normalization, window-persistence detection, focus restoration) stays
	// in KDiff3.cs unchanged.
	class KDiff3MergeEngine : IMergeEngine
	{
		public MergeEngineResult Merge(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath)
		{
			var exitCode = KDiff3.Run(source1, source2, vanillaFile, outputPath);
			return exitCode == 0 ? MergeEngineResult.AutoSolved : MergeEngineResult.Failed;
		}

		public MergeEngineResult MergeHeadless(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath)
		{
			return KDiff3.RunHeadless(source1, source2, vanillaFile, outputPath) switch
			{
				KDiff3.HeadlessResult.AutoSolved => MergeEngineResult.AutoSolved,
				KDiff3.HeadlessResult.NeedsManualResolution => MergeEngineResult.NeedsManualResolution,
				_ => MergeEngineResult.Failed,
			};
		}

		public bool ValidateExePath() => File.Exists(KDiff3.ExePath);
	}
}

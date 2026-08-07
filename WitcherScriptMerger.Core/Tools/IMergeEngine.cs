using System.IO;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools
{
	public enum MergeEngineResult
	{
		AutoSolved,
		NeedsManualResolution,
		Failed,
	}

	// Scaffolding introduced by the Core/host project split - NOT meant as a
	// permanent pluggable-engine abstraction. It exists only so FileMerger (now in
	// Core) can call a 3-way text merge without Core referencing Tools/KDiff3.cs's
	// Win32 P/Invoke, which has to stay in the host project for now. The host
	// project supplies the one real implementation (KDiff3MergeEngine) at startup
	// via AppState.MergeEngine. A later unit that removes KDiff3 entirely will
	// likely delete this interface and inline its replacement directly into
	// FileMerger, unless a test project ends up depending on it as a seam.
	public interface IMergeEngine
	{
		// Interactive: may open the merge tool's own UI and block until the user
		// finishes or cancels. Returns AutoSolved on any successful save (whether
		// auto-solved or manually resolved by the user), Failed on cancel/error.
		// Never returns NeedsManualResolution - that's a headless-only concept.
		MergeEngineResult Merge(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath);

		// Headless: never blocks on user interaction. Detects an unresolved
		// conflict itself and reports NeedsManualResolution instead of leaving a
		// process hanging or a window open with nobody watching it.
		MergeEngineResult MergeHeadless(
			FileMerger.MergeSource source1,
			FileMerger.MergeSource source2,
			FileInfo vanillaFile,
			string outputPath);

		// Whether the underlying merge tool's executable can actually be found.
		// Exists so Paths.ValidateDependencyPaths() (Core) can validate the merge
		// engine's dependency alongside QuickBMS/wcc_lite without Core referencing
		// Tools/KDiff3.cs directly - that class stays in the host project for its
		// Win32 P/Invoke, so Core can only reach it through this interface.
		bool ValidateExePath();
	}
}

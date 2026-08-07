using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger
{
	// Shared mutable application state, previously held directly by the host
	// project's Program class. It moved out to Core during the Core/host project
	// split because domain code that now lives in Core (Paths, AppSettings,
	// ModFileIndex, FileMerger, CustomLoadOrder, Cli/MergeOperations,
	// Mcp/WsmMcpTools, ...) needs to read/write it, and Core can never reference
	// the host assembly (that's the whole point of the split - the dependency only
	// flows host -> Core). The host project's Program class re-exposes these as
	// pass-through Notifier/Settings/LoadOrder/Inventory properties so none of its
	// own call sites had to change.
	//
	// An explicit static constructor suppresses `beforefieldinit`, so this class's
	// field initializers run at a precise, well-defined point (first member access)
	// rather than at some unspecified point the CLR chooses - load-bearing here
	// because Program.MaybeAttachConsole() must run before Settings' constructor
	// can report a missing-config error to the invoking terminal (see CLAUDE.md's
	// Startup flow), and because Paths' own static field initializers read
	// Settings.Get(...), transitively depending on this class being fully
	// initialized first.
	public static class AppState
	{
		// Defaults to the headless implementation so it's safe to use from the very
		// first line of Main() - the GUI path swaps it out for MainForm once
		// constructed. See CLAUDE.md's IMergeNotifier section.
		public static IMergeNotifier Notifier = new HeadlessMergeNotifier();
		public static AppSettings Settings = new AppSettings();
		public static CustomLoadOrder LoadOrder = null;
		public static MergeInventory Inventory = null;

		// Set once by the host project at startup (see Program.cs) to a
		// KDiff3MergeEngine - see Tools/IMergeEngine.cs for why this exists.
		public static IMergeEngine MergeEngine = null;

		static AppState() { }
	}
}

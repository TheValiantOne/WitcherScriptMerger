using System.Threading;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

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
	// Startup flow). Paths.cs used to have the same beforefieldinit hazard one hop
	// further out (its own static field initializers read Settings.Get(...)
	// eagerly) - fixed by making those Paths properties compute on every access
	// instead of caching via a field initializer, so Settings' laziness isn't
	// undermined transitively; see Paths.cs.
	public static class AppState
	{
		// Defaults to the headless implementation so it's safe to use from the very
		// first line of Main() - the GUI path swaps it out for MainForm once
		// constructed. See CLAUDE.md's IMergeNotifier section.
		public static IMergeNotifier Notifier = new HeadlessMergeNotifier();

		// Lazy rather than a field initializer: AppSettings' constructor calls
		// Environment.Exit(1) if it can't find a config file next to the entry
		// assembly (see AppSettings.cs) - appropriate for the real GUI/CLI/MCP entry
		// points, where that's genuinely fatal, but not for WitcherScriptMerger.Tests,
		// whose test host has no matching .config. Since C# runs ALL of a type's
		// static field initializers together on first touch of ANY static member,
		// Settings being a plain field-with-initializer meant merely reading
		// AppState.Notifier (which Core code - e.g. DiffPlexMergeEngine's headless
		// skip/guard messages - legitimately does on its own, unprompted by test code)
		// silently also ran `new AppSettings()` and crashed the whole test process.
		// Making Settings lazy decouples the two: touching Notifier alone no longer
		// forces Settings to construct. Confirmed no call site assigns AppState.Settings
		// or Program.Settings, so keeping this settable (for symmetry with the other
		// fields here, and in case a future test wants to inject a stub) is a safe,
		// behavior-preserving change for every existing GUI/CLI/MCP call site: first
		// real access still runs the identical `new AppSettings()` and identical
		// crash-on-missing-config behavior, just deferred to that first access instead
		// of eagerly.
		//
		// LazyInitializer.EnsureInitialized (rather than the simpler
		// `_settings ?? (_settings = new AppSettings())`) makes this thread-safe: the
		// simpler form is a classic non-atomic check-then-act race that could, under
		// concurrent first access, construct AppSettings() more than once (each with
		// its own real side effects, including a possible Environment.Exit(1)).
		// Currently unreachable from any shipped entry point or the test suite (all
		// single-threaded at this point in startup) - flagged in code review as a
		// latent risk anyway, since other Core statics (e.g. QuickBms.cs/WccLite.cs)
		// also read AppState.Settings.Get(...) from their own static field
		// initializers, and nothing prevents a future concurrent caller.
		static AppSettings _settings;
		public static AppSettings Settings
		{
			get => LazyInitializer.EnsureInitialized(ref _settings, () => new AppSettings());
			set => _settings = value;
		}

		public static CustomLoadOrder LoadOrder = null;
		public static MergeInventory Inventory = null;

		static AppState() { }
	}
}

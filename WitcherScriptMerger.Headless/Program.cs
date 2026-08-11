using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WitcherScriptMerger.Cli;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Mcp;

namespace WitcherScriptMerger.Headless
{
	// Entry point for the Linux-capable, CLI/MCP-only host - see CLAUDE.md's "Headless
	// host (WitcherScriptMerger.Headless)" section. Deliberately a much smaller mirror of
	// WitcherScriptMerger/Program.cs: only the "merge" and "mcp" verbs exist here, there's
	// no GUI branch at all (no System.Windows.Forms reference in this project, so there's
	// nothing that *could* launch one), and nothing Windows-specific (no [STAThread], no
	// AttachConsole P/Invoke - see MaybeAttachConsole's comment on the WinForms host for
	// why that one is Windows-only) appears here. RunCli/RunMcp's actual orchestration
	// (scan/merge sequencing, the MCP tool implementations) already lives in
	// WitcherScriptMerger.Core's Cli/MergeOperations.cs and Mcp/WsmMcpTools.cs, shared with
	// the WinForms host - this class only replicates the thin routing/argument-parsing
	// glue around those, which was small enough not to warrant extracting into Core too.
	static class Program
	{
		static int Main(string[] args)
		{
			// Checked before anything else, including the CurrentDirectory reassignment
			// below - mirrors WitcherScriptMerger/Program.cs's RunCli doing the same
			// ahead of its own config-file check. AppState.Settings's construction (first
			// touched inside RunMerge/RunMcp) calls Environment.Exit(1) when it can't find
			// a config file (see Core's CLAUDE.md), so a freshly-extracted publish dir
			// with no App.config/<AssemblyName>.dll.config copied beside the exe yet must
			// never reach that path just to answer "--version".
			if (args.Length > 0 && args[0] == "--version")
			{
				Console.WriteLine(VersionInfo.GetVersion(typeof(Program).Assembly));
				return 0;
			}

			// Several Core paths are relative to Environment.CurrentDirectory
			// (Paths.MergedBundleContentAbsolute's field initializer, Paths.Inventory,
			// Paths.DiffPlexConflictsDirectory, Paths.TempBundleContent) - must be set
			// before anything touches Paths or AppState.Settings. Mirrors
			// WitcherScriptMerger/Program.cs's RunCli doing the same as its first
			// statement; this host has no no-args-launches-GUI branch to worry about
			// leaving unreset, so it's safe to do this unconditionally as the very first
			// thing, before even inspecting args.
			Environment.CurrentDirectory = AppContext.BaseDirectory;

			// No engine setup needed here (or in WitcherScriptMerger/Program.cs, the
			// WinForms host) - FileMerger builds its own DiffPlexMergeEngine directly now
			// that KDiff3 and the IMergeEngine interface that used to sit in front of it
			// are gone (see docs/decisions/kdiff3-retirement.md and CLAUDE.md's
			// "Interactive vs. headless split" section) - there's no more engine-selection
			// step for either host to perform at startup.

			if (args.Length == 0)
			{
				PrintUsage();
				return 1;
			}

			if (args[0] == "mcp")
				return RunMcp();

			if (args[0] == "merge")
				return RunMerge(args);

			Console.Error.WriteLine($"Unknown command '{args[0]}'. Supported commands: merge, mcp, --version");
			PrintUsage();
			return 1;
		}

		static void PrintUsage()
		{
			Console.Error.WriteLine("WitcherScriptMerger.Headless - CLI/MCP-only host (no GUI).");
			Console.Error.WriteLine();
			Console.Error.WriteLine("Usage:");
			Console.Error.WriteLine("  WitcherScriptMerger.Headless merge [--order-file <path.json>] [--overwrite]");
			Console.Error.WriteLine("  WitcherScriptMerger.Headless mcp");
			Console.Error.WriteLine("  WitcherScriptMerger.Headless --version");
			Console.Error.WriteLine();
			Console.Error.WriteLine("Supports flat-file (.ws/.xml) conflicts only - bundle-content conflicts");
			Console.Error.WriteLine("require QuickBMS/wcc_lite, which this host doesn't bundle. See CLAUDE.md.");
		}

		// Mirrors WitcherScriptMerger/Program.cs's RunCli's "merge" branch. Exit codes
		// match that host's: 0 = every conflict merged, 1 = couldn't even start (bad
		// args/config/deps), 2 = ran, but one or more conflicts were skipped.
		static int RunMerge(string[] args)
		{
			if (!AppState.Settings.HasConfigFile)
			{
				Console.Error.WriteLine("Config file is missing.");
				return 1;
			}

			// Only the text-merge engine (DiffPlexMergeEngine, always) is required to
			// start a merge run here - not QuickBMS/wcc_lite too. This host has no
			// QuickBMS/wcc_lite bundled at all (see CLAUDE.md and
			// docs/decisions/bundle-format-replacement-spike.md), so requiring the full
			// Paths.ValidateDependencyPaths() check (as the WinForms host's CLI verb
			// does) would mean this host could never merge even its supported flat-file
			// (.ws/.xml) conflicts. Bundle-category conflicts still fail gracefully,
			// per-conflict, when actually attempted without QuickBMS/wcc_lite configured
			// - see ModFileIndex.BuildAsync and FileMerger.GetUnpackedFiles (Core).
			if (!Paths.ValidateTextMergeDependencies())
			{
				AppState.Notifier.ShowError(
					"The configured text-merge engine is missing or misconfigured. This shouldn't " +
					"happen with the built-in DiffPlex engine - check for a corrupted install.");
				return 1;
			}

			string orderFilePath = null;
			var overwrite = false;
			for (int i = 1; i < args.Length; ++i)
			{
				if (args[i] == "--order-file" && i + 1 < args.Length)
					orderFilePath = args[++i];
				else if (args[i] == "--overwrite")
					overwrite = true;
				else
				{
					Console.Error.WriteLine($"Unknown argument: {args[i]}");
					return 1;
				}
			}

			IReadOnlyDictionary<string, string[]> orderOverrides = null;
			if (orderFilePath != null && !TryLoadOrderFile(orderFilePath, out orderOverrides))
				return 1;

			if (!Paths.ValidateModsDirectory())
				return 1;

			var mergedModName = Paths.RetrieveMergedModName();
			if (string.IsNullOrWhiteSpace(mergedModName))
				return 1;

			AppState.LoadOrder = new CustomLoadOrder();
			AppState.Inventory = MergeInventory.Load(Paths.Inventory);

			var modIndex = MergeOperations.ScanConflicts();

			if (!modIndex.HasConflict)
			{
				Console.WriteLine("No conflicts found.");
				return 0;
			}

			var summary = MergeOperations.RunMerge(AppState.Inventory, modIndex.Conflicts, mergedModName, orderOverrides, dryRun: false, overwrite: overwrite);

			AppState.Inventory.Save();

			Console.WriteLine($"Merged {summary.Merged.Count} file(s), skipped {summary.Skipped.Count}.");
			foreach (var path in summary.Skipped)
				Console.WriteLine($"  skipped: {path}");
			foreach (var decision in summary.FunctionLevelDecisions)
				Console.WriteLine($"  function-level: {decision}");

			return summary.Skipped.Count == 0 ? 0 : 2;
		}

		static bool TryLoadOrderFile(string path, out IReadOnlyDictionary<string, string[]> orderOverrides)
		{
			orderOverrides = null;
			try
			{
				var json = File.ReadAllText(path);
				orderOverrides = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
				return true;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Failed to read order file '{path}': {ex.Message}");
				return false;
			}
		}

		// Runs an MCP server over stdio - mirrors WitcherScriptMerger/Program.cs's RunMcp
		// exactly (same tool assembly, same stdout/stderr split). See CLAUDE.md's MCP
		// mode section. Only requires the text-merge engine, not QuickBMS/wcc_lite - see
		// RunMerge's comment above and WsmMcpTools.RequireDependenciesAndModsDirectory
		// (Core), which applies the identical relaxation to scan_conflicts/
		// merge_conflicts.
		static int RunMcp()
		{
			if (!AppState.Settings.HasConfigFile)
			{
				Console.Error.WriteLine("Config file is missing.");
				return 1;
			}

			if (!Paths.ValidateTextMergeDependencies())
			{
				Console.Error.WriteLine(
					"The configured text-merge engine is missing or misconfigured. This shouldn't " +
					"happen with the built-in DiffPlex engine - check for a corrupted install.");
				return 1;
			}

			var builder = Host.CreateApplicationBuilder();

			// stdout is reserved for MCP protocol frames - all logging must go to stderr.
			builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

			// WsmMcpTools lives in WitcherScriptMerger.Core, not this (entry/calling)
			// assembly - the parameterless WithToolsFromAssembly() overload only scans the
			// calling assembly, which would silently register zero tools (server starts,
			// `initialize` succeeds, `tools/list` returns an empty array) if left as-is.
			// Pass the Core assembly explicitly - same fix WitcherScriptMerger/Program.cs
			// needed for the identical reason.
			//
			// ServerInfo is the SDK's standard mechanism for identifying this server (name
			// + version) to a connecting client during the initialize handshake - not a
			// custom side-channel. "WitcherScriptMerger.Headless" distinguishes this host
			// from the WinForms host's own MCP server in a client's logs.
			builder.Services
				.AddMcpServer(options => options.ServerInfo = new Implementation
				{
					Name = "WitcherScriptMerger.Headless",
					Version = VersionInfo.GetVersion(typeof(Program).Assembly),
				})
				.WithStdioServerTransport()
				.WithToolsFromAssembly(typeof(WsmMcpTools).Assembly);

			builder.Build().RunAsync().GetAwaiter().GetResult();
			return 0;
		}
	}
}

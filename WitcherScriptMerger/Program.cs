using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WitcherScriptMerger.Cli;
using WitcherScriptMerger.Forms;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Mcp;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger
{
	static class Program
	{
		// Must run before anything else in this class's static init, so any
		// early startup error (e.g. missing App.config) is visible in the
		// invoking terminal instead of written to an unattached console.
		static readonly bool _consoleAttached = MaybeAttachConsole();

		// Notifier/Settings/LoadOrder/Inventory live in Core's AppState now, not here -
		// domain code that moved to Core (Paths, FileMerger, Cli/MergeOperations,
		// Mcp/WsmMcpTools, ...) needs them, and Core can never reference this host
		// assembly (see AppState.cs). These pass-through properties keep every
		// existing Program.X call site in this project unchanged.
		public static IMergeNotifier Notifier
		{
			get => AppState.Notifier;
			set => AppState.Notifier = value;
		}
		public static AppSettings Settings => AppState.Settings;
		public static CustomLoadOrder LoadOrder
		{
			get => AppState.LoadOrder;
			set => AppState.LoadOrder = value;
		}
		public static MergeInventory Inventory
		{
			get => AppState.Inventory;
			set => AppState.Inventory = value;
		}
		public static MainForm MainForm;

		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main(string[] args)
		{
			// The one real IMergeEngine implementation, supplied here since it needs
			// Tools/KDiff3.cs's Win32 P/Invoke (host-only) - see Tools/IMergeEngine.cs.
			// Must be set before anything calls Paths.ValidateDependencyPaths() or
			// constructs a FileMerger, in any of the GUI/CLI/MCP paths below.
			AppState.MergeEngine = new KDiff3MergeEngine();

			if (args.Length > 0)
			{
				Environment.ExitCode = RunCli(args);
				return;
			}

			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

			if (!Settings.HasConfigFile)
			{
				ShowLaunchFailure("Config file is missing.");
				return;
			}
			if (!Paths.ValidateDependencyPaths())
			{
				using (var dependencyForm = new DependencyForm())
				{
					if (dependencyForm.ShowDialog() != DialogResult.OK)
					{
						ShowLaunchFailure("A dependency is missing.");
						return;
					}
				}
			}

			MainForm = new MainForm();
			Notifier = MainForm;
			Application.Run(MainForm);
		}

		static void ShowLaunchFailure(string message)
		{
			Notifier.ShowError($"Launch failure: {message}", "Script Merger Error");
		}

		public static bool TryOpenFile(string path)
		{
			if (!File.Exists(path))
			{
				MainForm.ShowMessage("Can't find file: " + path);
				return false;
			}

			if (path.EndsWithIgnoreCase(".exe"))  // EXEs need working dir to be specified
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = path,
					WorkingDirectory = Path.GetDirectoryName(path)
				};
				Process.Start(startInfo);
			}
			else
				try { Process.Start(path); }
				catch (Exception) { }

			return true;
		}

		public static bool TryOpenFileLocation(string filePath)
		{
			return TryOpenDirectory(Path.GetDirectoryName(filePath));
		}

		public static bool TryOpenDirectory(string dirPath)
		{
			if (!Directory.Exists(dirPath))
			{
				MainForm.ShowMessage("Can't find directory: " + dirPath);
				return false;
			}
			Process.Start(dirPath);
			return true;
		}

		#region CLI

		// "merge" and "mcp" are the only commands for now. Exit codes (merge): 0 = every
		// conflict merged, 1 = couldn't even start (bad args/config/deps), 2 = ran, but
		// one or more conflicts were skipped.
		static int RunCli(string[] args)
		{
			Environment.CurrentDirectory = AppContext.BaseDirectory;

			if (!Settings.HasConfigFile)
			{
				ShowLaunchFailure("Config file is missing.");
				return 1;
			}

			if (args[0] == "mcp")
				return RunMcp();

			if (args[0] != "merge")
			{
				Console.Error.WriteLine($"Unknown command '{args[0]}'. Supported commands: merge, mcp");
				return 1;
			}

			if (!Paths.ValidateDependencyPaths())
			{
				Notifier.ShowError(
					"A required dependency (KDiff3, QuickBMS, or wcc_lite) is missing. Configure its path " +
					"in App.config, or run without arguments once to use the GUI's dependency setup.");
				return 1;
			}

			string orderFilePath = null;
			for (int i = 1; i < args.Length; ++i)
			{
				if (args[i] == "--order-file" && i + 1 < args.Length)
					orderFilePath = args[++i];
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

			LoadOrder = new CustomLoadOrder();
			Inventory = MergeInventory.Load(Paths.Inventory);

			var modIndex = MergeOperations.ScanConflicts();

			if (!modIndex.HasConflict)
			{
				Console.WriteLine("No conflicts found.");
				return 0;
			}

			var summary = MergeOperations.RunMerge(Inventory, modIndex.Conflicts, mergedModName, orderOverrides);

			Inventory.Save();

			Console.WriteLine($"Merged {summary.Merged.Count} file(s), skipped {summary.Skipped.Count}.");
			foreach (var path in summary.Skipped)
				Console.WriteLine($"  skipped: {path}");

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

		// Runs an MCP server over stdio (see Mcp/WsmMcpTools.cs and CLAUDE.md's MCP mode
		// section). Never returns until the client disconnects/stdin closes.
		static int RunMcp()
		{
			if (!Paths.ValidateDependencyPaths())
			{
				Console.Error.WriteLine(
					"A required dependency (KDiff3, QuickBMS, or wcc_lite) is missing. Configure its path in App.config.");
				return 1;
			}

			var builder = Host.CreateApplicationBuilder();

			// stdout is reserved for MCP protocol frames - all logging must go to stderr.
			builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

			// WsmMcpTools now lives in WitcherScriptMerger.Core, not this (entry/calling)
			// assembly - the parameterless WithToolsFromAssembly() overload only scans
			// the calling assembly, which would silently register zero tools (server
			// starts, `initialize` succeeds, `tools/list` returns an empty array) if
			// left as-is. Pass the Core assembly explicitly.
			builder.Services
				.AddMcpServer()
				.WithStdioServerTransport()
				.WithToolsFromAssembly(typeof(WsmMcpTools).Assembly);

			builder.Build().RunAsync().GetAwaiter().GetResult();
			return 0;
		}

		static bool MaybeAttachConsole()
		{
			// GetCommandLineArgs()[0] is the exe path itself; more than that means CLI
			// arguments were passed. Skip for "mcp": stdin/stdout are reserved for the MCP
			// protocol there, and an MCP client spawns WSM with its own redirected pipes
			// rather than a console to attach to anyway.
			var cliArgs = Environment.GetCommandLineArgs();
			return cliArgs.Length > 1 && cliArgs[1] != "mcp" && AttachConsole(AttachParentProcess);
		}

		const int AttachParentProcess = -1;

		[DllImport("kernel32.dll")]
		static extern bool AttachConsole(int dwProcessId);

		#endregion
	}
}

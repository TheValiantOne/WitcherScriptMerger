using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Forms;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger
{
    static class Program
    {
        // Must run before anything else in this class's static init, so any
        // early startup error (e.g. missing App.config) is visible in the
        // invoking terminal instead of written to an unattached console.
        static readonly bool _consoleAttached = MaybeAttachConsole();

        // Defaults to the headless implementation so it's safe to use from the
        // very first line of Main() - the GUI path swaps it out for MainForm
        // once constructed. See CLAUDE.md's IMergeNotifier section.
        public static IMergeNotifier Notifier = new HeadlessMergeNotifier();
        public static AppSettings Settings = new AppSettings();
        public static CustomLoadOrder LoadOrder = null;
        public static MergeInventory Inventory = null;
        public static MainForm MainForm;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
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

        // "merge" is the only command for now. Exit codes: 0 = every conflict
        // merged, 1 = couldn't even start (bad args/config/deps), 2 = ran, but
        // one or more conflicts were skipped.
        static int RunCli(string[] args)
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;

            if (!Settings.HasConfigFile)
            {
                ShowLaunchFailure("Config file is missing.");
                return 1;
            }

            if (args[0] != "merge")
            {
                Console.Error.WriteLine($"Unknown command '{args[0]}'. Supported commands: merge");
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

            var modIndex = new ModFileIndex();
            using (var scanComplete = new ManualResetEventSlim(false))
            {
                modIndex.BuildAsync(
                    Settings.Get<bool>("CheckScripts"),
                    Settings.Get<bool>("CheckXmlFiles"),
                    Settings.Get<bool>("CheckBundleContents"),
                    (s, e) => { },
                    (s, e) => scanComplete.Set());
                scanComplete.Wait();
            }

            if (!modIndex.HasConflict)
            {
                Console.WriteLine("No conflicts found.");
                return 0;
            }

            var merger = new FileMerger(Inventory, (s, e) => { }, (s, e) => { });
            var summary = merger.MergeConflictsHeadless(modIndex.Conflicts, mergedModName, orderOverrides);

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

        static bool MaybeAttachConsole()
        {
            // GetCommandLineArgs()[0] is the exe path itself; more than that
            // means CLI arguments were passed.
            return Environment.GetCommandLineArgs().Length > 1 && AttachConsole(AttachParentProcess);
        }

        const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        #endregion
    }
}

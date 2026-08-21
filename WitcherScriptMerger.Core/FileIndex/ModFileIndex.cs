using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.FileIndex
{
	public class ModFileIndex
	{
		public List<ModFile> Files;

		public IEnumerable<ModFile> Conflicts => Files.Where(f => f.HasConflict);

		public bool HasConflict => Files.Any(f => f.HasConflict);

		public int ModCount { get; private set; }

		public int ScriptCount { get; private set; }

		public int XmlCount { get; private set; }

		public int BundleCount { get; private set; }

		public ModFileIndex()
		{
			Files = new List<ModFile>();
		}

		public void BuildAsync(
			bool checkScripts, bool checkXml, bool checkBundles,
			ProgressChangedEventHandler progressHandler,
			RunWorkerCompletedEventHandler completedHandler)
		{
			var ignoredModNames = GetIgnoredModNames();
			var modDirPaths = Directory.GetDirectories(Paths.ModsDirectory, "mod*", SearchOption.TopDirectoryOnly)
				.Where(path => !ignoredModNames.Any(name => name.EqualsIgnoreCase(new DirectoryInfo(path).Name)))
				.ToList();
			ModCount = modDirPaths.Count;
			if (ModCount == 0)
			{
				AppState.Notifier.ShowMessage("Can't find any mods in the Mods directory.");
			}

			// Checked once up front, not per bundle: QuickBms.GetBundleContentPaths already
			// reports (and now tolerates - see its own comment) a missing QuickBMS/wcc_lite
			// per bundle it's asked about, but that's needlessly noisy across a whole scan,
			// and WitcherScriptMerger.Headless (the Linux-capable CLI/MCP-only host, no
			// QuickBMS/wcc_lite bundled at all - see its CLAUDE.md section) deliberately
			// doesn't gate scanning on Paths.ValidateDependencyPaths() first, so this is the
			// first point in a scan where that host's missing bundle tooling surfaces. One
			// clear message beats one per bundle. BundleCount (below) still counts every
			// *.bundle file found regardless of whether checking could proceed - unchanged
			// from before this gate, and consistent with ScriptCount/XmlCount, which also
			// count regardless of checkScripts/checkXml - only the actual per-file
			// conflict-scanning loop is skipped here.
			var canCheckBundles = checkBundles && QuickBms.IsAvailable;
			if (checkBundles && !canCheckBundles)
			{
				AppState.Notifier.ShowMessage(
					"Bundle-content conflicts aren't supported without QuickBMS and wcc_lite configured - skipping bundle-content checking for this scan.",
					"Bundle Checking Unavailable",
					NotifyButtons.OK,
					DialogIcon.Warning);
			}

			var bgWorker = new BackgroundWorker
			{
				WorkerReportsProgress = true
			};
			bgWorker.DoWork += (sender, e) =>
			{
				var i = 0;
				ScriptCount = XmlCount = BundleCount = 0;
				foreach (var modDirPath in modDirPaths)
				{
					var modName = Path.GetFileName(modDirPath);
					var filePaths = Directory.GetFiles(modDirPath, "*", SearchOption.AllDirectories);
					var scriptPaths = filePaths.Where(path => ModFile.IsScript(path));
					var xmlPaths = filePaths.Where(path => ModFile.IsXml(path));
					var bundlePaths = filePaths.Where(path => ModFile.IsBundle(path));

					ScriptCount += scriptPaths.Count();
					XmlCount += xmlPaths.Count();
					BundleCount += bundlePaths.Count();

					if (checkScripts)
					{
						Files.AddRange(GetModFilesFromPaths(scriptPaths, Categories.Script, modName));
					}
					if (checkXml)
					{
						Files.AddRange(GetModFilesFromPaths(xmlPaths, Categories.Xml, modName));
					}
					if (canCheckBundles)
					{
						foreach (var bundlePath in bundlePaths)
						{
							var contentPaths = QuickBms.GetBundleContentPaths(bundlePath);
							Files.AddRange(GetModFilesFromPaths(contentPaths, Categories.BundleText, modName, bundlePath));
						}
					}
					var progressPct = (int)((float)++i / modDirPaths.Count * 100f);
					bgWorker.ReportProgress(progressPct, modName as object);
				}
				if (canCheckBundles)
					System.Threading.Thread.Sleep(500);  // Wait for progress bar to fill completely
			};
			bgWorker.RunWorkerCompleted += completedHandler;
			bgWorker.ProgressChanged += progressHandler;
			bgWorker.RunWorkerAsync();
		}

		private List<ModFile> GetModFilesFromPaths(
			IEnumerable<string> filePaths,
			ModFileCategory category,
			string modName, string bundlePath = null)
		{
			var fileList = new List<ModFile>();
			foreach (var filePath in filePaths)
			{
				string relPath = null;
				if (category == Categories.Script)
					relPath = Paths.GetRelativePath(filePath, Paths.ModScriptBase);
				else if (category == Categories.Xml)
					relPath = Paths.GetRelativePath(filePath, modName);
				else if (category == Categories.BundleText)
					relPath = filePath;
				else
					throw new NotImplementedException();

				var existingFile = Files.FirstOrDefault(file =>
					file.RelativePath.EqualsIgnoreCase(relPath));
				if (existingFile == null)
				{
					var newFile = (bundlePath != null
						? new ModFile(relPath, bundlePath)
						: new ModFile(relPath));
					newFile.Mods.Add(new FileHash { Name = modName });
					fileList.Add(newFile);
				}
				else
					existingFile.Mods.Add(new FileHash { Name = modName });
			}
			return fileList;
		}

		private IEnumerable<string> GetIgnoredModNames()
		{
			return BuildIgnoredModNames(
				AppState.Settings.Get("IgnoreModNames"),
				AppState.Settings.Get("MergedModName"));
		}

		// Split out from GetIgnoredModNames (above) as a pure function over the two raw
		// setting values so it's unit-testable without touching AppState.Settings - see
		// WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety constraints".
		//
		// The merged mod is always excluded, on top of whatever the user configured in
		// IgnoreModNames. It is this tool's own *output*, not a source mod, but its
		// directory name starts with "mod" and so matches BuildAsync's "mod*" glob like any
		// other. Left in the scan it becomes a merge input alongside the very mods it was
		// built from, and each subsequent merge re-applies those mods' edits on top of an
		// already-merged file - inserted blocks accumulate a fresh copy per run (observed
		// live: a single modBloodAndSteel insertion present 6 times in actor.ws, and a
		// modCriSlowMoCR one 6 times in damageManagerProcessor.ws, after repeated merges),
		// and a losing tiebreak can additionally revert an edit a previous run had kept.
		// Excluding it by name is what makes a re-merge idempotent instead of cumulative.
		public static List<string> BuildIgnoredModNames(string ignoreModNamesSetting, string mergedModNameSetting)
		{
			var ignoredNames = (ignoreModNamesSetting ?? string.Empty).Split(',')
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Select(name => name.Trim())
				.ToList();

			var mergedModName = Paths.NormalizeMergedModName(mergedModNameSetting);
			if (mergedModName != null &&
				!ignoredNames.Any(name => name.EqualsIgnoreCase(mergedModName)))
			{
				ignoredNames.Add(mergedModName);
			}
			return ignoredNames;
		}
	}
}

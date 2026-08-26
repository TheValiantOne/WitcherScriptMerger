using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
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

			modDirPaths = ExcludeDisabledMods(modDirPaths);

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

		// Drops mod folders the game will never load because mods.settings marks them
		// Enabled=0.
		//
		// A scan is a filesystem glob over Paths.ModsDirectory, so a mod that is deployed
		// but disabled looked exactly like an active one, and its files were counted as
		// full conflict participants. That is not a cosmetic over-count: a disabled mod
		// can make a conflict genuinely unmergeable and keep it that way forever. Observed
		// live - a disabled modFearlessRoach ships a pre-next-gen whole-file copy of
		// game\vehicles\horse\states\exploration.ws (missing CheckVector / DoHorseKick /
		// OnHorseKick), which trips FunctionLevelMergeEngine's vanilla-declaration
		// invariant, so every run reported "needs manual resolution" for a file the game
		// was never going to load that mod's version of anyway.
		//
		// Only an EXPLICIT Enabled=0 excludes a mod. CustomLoadOrder.IsModDisabledByName
		// returns false for a mod that isn't in mods.settings at all, which is the correct
		// reading: the game appends unknown mod folders on next launch, enabled. A missing
		// or unreadable mods.settings likewise disables no one (CustomLoadOrder.Refresh
		// leaves Mods empty), so this can never make a scan miss conflicts on a fresh
		// install, or on a Linux host with no Documents\The Witcher 3 at all.
		//
		// Set MergeDisabledMods=true to opt out and go back to scanning every deployed mod
		// - the useful case being pre-merging a mod that's staged but not switched on yet.
		// The setting is named for the opt-OUT so that its absence from an older
		// App.config (AppSettings.Get<bool> yields false for a key that isn't there) means
		// the new, wanted behavior rather than silently keeping the old one.
		private List<string> ExcludeDisabledMods(List<string> modDirPaths)
		{
			if (AppState.Settings.Get<bool>("MergeDisabledMods"))
				return modDirPaths;

			// Built here rather than taken from AppState.LoadOrder because the MCP tools
			// never populate that field (only the CLI verb and the GUI do), and a scan
			// must not depend on which entry point it was reached from.
			CustomLoadOrder loadOrder;
			try
			{
				loadOrder = AppState.LoadOrder ?? new CustomLoadOrder();
			}
			catch (Exception)
			{
				// mods.settings unreadable - exclude nothing, exactly as if every mod were enabled.
				return modDirPaths;
			}

			var kept = ExcludeDisabledModPaths(modDirPaths, loadOrder.IsModDisabledByName, out var skipped);
			DisabledModsSkipped = skipped;

			// Reported, never silent: a conflict vanishing from the scan because a mod is
			// switched off should be visible in the run's output, not something the user
			// has to infer from a changed conflict count.
			if (DisabledModsSkipped.Count > 0)
			{
				AppState.Notifier.ShowMessage(
					$"Skipped {DisabledModsSkipped.Count} disabled mod folder(s) - the game won't load them, so they can't " +
					$"conflict: {string.Join(", ", DisabledModsSkipped)}. Set MergeDisabledMods=true to include them anyway.",
					"Disabled Mods Skipped");
			}

			return kept;
		}

		// Split out from ExcludeDisabledMods (above) as a pure function over a
		// path list and a name predicate, so it's unit-testable without touching
		// AppState.Settings or reading a real mods.settings - the same shape, and the same
		// reason, as BuildIgnoredModNames just above. See
		// WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety constraints".
		//
		// The predicate is asked about the mod's FOLDER NAME, not its full path: that's the
		// name mods.settings keys on and the name the game itself loads by.
		public static List<string> ExcludeDisabledModPaths(
			IEnumerable<string> modDirPaths, Func<string, bool> isModDisabled, out List<string> skippedModNames)
		{
			skippedModNames = new List<string>();
			var kept = new List<string>();
			if (modDirPaths == null)
				return kept;

			foreach (var path in modDirPaths)
			{
				var name = Path.GetFileName(path);
				if (isModDisabled != null && isModDisabled(name))
					skippedModNames.Add(name);
				else
					kept.Add(path);
			}

			skippedModNames.Sort(StringComparer.OrdinalIgnoreCase);
			return kept;
		}

		// Mod folder names left out of the most recent scan by ExcludeDisabledMods.
		// Never null; empty when nothing was skipped.
		public IReadOnlyList<string> DisabledModsSkipped { get; private set; } = new List<string>();

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

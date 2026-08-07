using System;
using System.IO;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger
{
	public static class Paths
	{
		public const string TempBundleContent = "tempbundlecontent";
		public static string MergedBundleContent = "Merged Bundle Content";
		public static string MergedBundleContentAbsolute = Path.Combine(Environment.CurrentDirectory, MergedBundleContent);

		// A dedicated top-level directory for DiffPlexMergeEngine's conflict-marker
		// sidecar files (Tools/DiffPlexMergeEngine.cs::GetConflictMarkerPath) -
		// deliberately NOT a subdirectory of TempBundleContent, even though both are
		// "scratch-ish" locations conceptually: FileMerger.CleanUpTempFiles() deletes
		// the entire TempBundleContent tree wholesale at the end of every headless
		// merge run (to clear QuickBMS-unpacked bundle scratch content), which would
		// otherwise delete every sidecar moments after DiffPlexMergeEngine wrote it -
		// confirmed by direct observation running the real CLI end-to-end: the sidecar
		// briefly existed during the run (the "conflict markers written to..." message
		// printed a real path) but was gone by the time the process exited. A separate,
		// unrelated top-level name sidesteps that collision entirely while keeping the
		// same original benefits (out of the live Paths.ModsDirectory tree, out of
		// Paths.MergedBundleContent's wholesale-packed tree - see DiffPlexMergeEngine's
		// own comment on GetConflictMarkerPath for those two reasons).
		public const string DiffPlexConflictsDirectory = "DiffPlexConflicts";
		public const string Inventory = "MergeInventory.xml";
		public static string ModScriptBase = Path.Combine("content", "scripts");
		public static string VanillaScriptBase = Path.Combine("content", "content0", "scripts");
		public static string BundleBase = "content";

		public static string GameDirectory => AppState.Settings.Get("GameDirectory");

		public static string GameExe => Path.Combine(GameDirectory, "bin", "x64", "witcher3.exe");

		public static string BundlesDirectory => Path.Combine(GameDirectory, BundleBase);

		public static string DlcDirectory => Path.Combine(GameDirectory, "DLC");

		// Deliberately not cached in a static field (as these two used to be): a field
		// initializer here would run alongside every other static field initializer of
		// this type on first touch of ANY of them (C#'s beforefieldinit semantics),
		// which would eagerly call AppState.Settings.Get(...) - forcing
		// AppState.Settings to construct (see its own lazy-property comment in
		// AppState.cs) merely from touching an unrelated static member of Paths, e.g. a
		// plain string helper like GetRelativePath with no settings dependency at all.
		// That's exactly the crash-in-a-dotnet-test-host scenario AppState.Settings'
		// laziness exists to avoid, one hop removed - flagged in code review, see
		// CLAUDE.md. AppState.Settings.Get(...) already reads from AppSettings' own
		// cached ConfigurationManager state, so re-reading it on every call here (rather
		// than caching again at this layer) costs nothing meaningful.
		public static string ScriptsDirectory
		{
			get
			{
				var setting = AppState.Settings.Get("VanillaScriptsDirectory");
				return (!string.IsNullOrWhiteSpace(setting)
						? setting
						: Path.Combine(GameDirectory, VanillaScriptBase));
			}
		}

		public static string ModsDirectory
		{
			get
			{
				var setting = AppState.Settings.Get("ModsDirectory");
				return (!string.IsNullOrWhiteSpace(setting)
						? setting
						: Path.Combine(GameDirectory, "Mods"));
			}
		}

		public static bool IsScriptsDirectoryDerived => string.IsNullOrWhiteSpace(AppState.Settings.Get("VanillaScriptsDirectory"));

		public static bool IsModsDirectoryDerived => string.IsNullOrWhiteSpace(AppState.Settings.Get("ModsDirectory"));

		public static string GetRelativePath(string fullPath, string basePath)
		{
			var startIndex = fullPath.IndexOfIgnoreCase(basePath) + basePath.Length + 1;
			return fullPath.Substring(startIndex);
		}

		// KDiff3's own exe-path check goes through AppState.MergeEngine rather than a
		// direct reference to Tools/KDiff3.cs, which stays in the host project for
		// its Win32 P/Invoke and so can't be referenced from Core - see
		// Tools/IMergeEngine.cs. Like AppState.Notifier/Settings, this relies on the
		// host having set AppState.MergeEngine before calling in - true for the one
		// real entry point (Program.Main, first line) but not enforced by the type
		// system; a null MergeEngine here reads as "dependency missing" rather than
		// "not initialized yet", which could be a confusing message if that
		// invariant is ever broken by a future entry point.
		// Split out from ValidateDependencyPaths (below) so a host that only supports
		// flat-file (.ws/.xml) conflicts - WitcherScriptMerger.Headless, the Linux-capable
		// CLI/MCP-only host, which has no QuickBMS/wcc_lite bundled at all (see its
		// CLAUDE.md section and docs/decisions/bundle-format-replacement-spike.md) - can
		// gate merging on just the text-merge engine, without also requiring bundle
		// tooling it deliberately doesn't ship. Bundle-category conflicts still fail
		// gracefully per-conflict when attempted without QuickBMS/wcc_lite (see
		// QuickBms.IsAvailable's callers and FileMerger.GetUnpackedFiles) - this split
		// doesn't change that, it only changes what gates a *scan/merge run starting at
		// all*.
		public static bool ValidateTextMergeDependencies()
		{
			return AppState.MergeEngine != null && AppState.MergeEngine.ValidateExePath();
		}

		// See ValidateTextMergeDependencies above for why this is separate.
		public static bool ValidateBundleDependencies()
		{
			return File.Exists(QuickBms.ExePath) &&
					File.Exists(QuickBms.PluginPath) &&
					File.Exists(WccLite.ExePath);
		}

		public static bool ValidateDependencyPaths()
		{
			return ValidateTextMergeDependencies() && ValidateBundleDependencies();
		}

		public static bool ValidateModsDirectory()
		{
			if (!Directory.Exists(ModsDirectory))
			{
				AppState.Notifier.ShowMessage(
					(!IsModsDirectoryDerived
					 ? "Can't find the Mods directory specified in the config file."
					 : "Can't find Mods directory in the specified game directory."));
				return false;
			}
			return true;
		}

		public static bool ValidateScriptsDirectory()
		{
			if (!Directory.Exists(ScriptsDirectory))
			{
				AppState.Notifier.ShowMessage(
					(!IsScriptsDirectoryDerived
					 ? "Can't find the Scripts directory specified in the config file."
					 : "Can't find \\content\\content0\\scripts directory in the specified game directory.") +
					"\n\nIt was added in patch 1.08.1 and should contain the game's vanilla scripts.");
				return false;
			}
			return true;
		}

		public static bool ValidateBundlesDirectory()
		{
			if (!Directory.Exists(BundlesDirectory))
			{
				AppState.Notifier.ShowMessage("Can't find 'content' directory in the specified game directory.");
				return false;
			}
			return true;
		}

		public static string RetrieveMergedBundlePath()
		{
			var mergedModName = RetrieveMergedModName();
			if (mergedModName != null)
				return Path.Combine(ModsDirectory, mergedModName, BundleBase, "blob0.bundle");
			else
				return null;
		}

		public static string RetrieveMergedModName()
		{
			var mergedModName = AppState.Settings.Get("MergedModName");
			if (string.IsNullOrWhiteSpace(mergedModName))
			{
				AppState.Notifier.ShowMessage("The MergedModName setting isn't configured in the .config file.");
				return null;
			}
			if (mergedModName.Length > 64)
				mergedModName = mergedModName.Substring(0, 64);
			if (!mergedModName.IsAlphaNumeric() || !mergedModName.StartsWith("mod"))
			{
				if (!ConfirmInvalidModName(mergedModName))
					return null;
			}
			return mergedModName;
		}

		public static string RetrieveMergedModDir()
		{
			var modName = RetrieveMergedModName();
			return
				modName != null
				? Path.Combine(ModsDirectory, modName)
				: null;
		}

		static bool ConfirmInvalidModName(string mergedModName)
		{
			return (NotifyResult.Yes == AppState.Notifier.ShowMessage(
				"The Witcher 3 won't load the merged file if the mod name isn't \"mod\" followed by numbers, letters, or underscores."
				+ "\n\nUse this name anyway?\n" + mergedModName
				+ "\n\nTo change the name: Click No, then edit \"MergedModName\" in the .config file.",
				"Warning",
				NotifyButtons.YesNo,
				DialogIcon.Exclamation));
		}
	}
}

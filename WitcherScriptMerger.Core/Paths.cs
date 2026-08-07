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
		public const string Inventory = "MergeInventory.xml";
		public static string ModScriptBase = Path.Combine("content", "scripts");
		public static string VanillaScriptBase = Path.Combine("content", "content0", "scripts");
		public static string BundleBase = "content";

		public static string GameDirectory => AppState.Settings.Get("GameDirectory");

		public static string GameExe => Path.Combine(GameDirectory, "bin", "x64", "witcher3.exe");

		public static string BundlesDirectory => Path.Combine(GameDirectory, BundleBase);

		public static string DlcDirectory => Path.Combine(GameDirectory, "DLC");

		static string _scriptsDirSetting = AppState.Settings.Get("VanillaScriptsDirectory");
		public static string ScriptsDirectory
		{
			get
			{
				return (!string.IsNullOrWhiteSpace(_scriptsDirSetting)
						? _scriptsDirSetting
						: Path.Combine(GameDirectory, VanillaScriptBase));
			}
		}

		static string _modsDirSetting = AppState.Settings.Get("ModsDirectory");
		public static string ModsDirectory
		{
			get
			{
				return (!string.IsNullOrWhiteSpace(_modsDirSetting)
						? _modsDirSetting
						: Path.Combine(GameDirectory, "Mods"));
			}
		}

		public static bool IsScriptsDirectoryDerived => string.IsNullOrWhiteSpace(_scriptsDirSetting);

		public static bool IsModsDirectoryDerived => string.IsNullOrWhiteSpace(_modsDirSetting);

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
		public static bool ValidateDependencyPaths()
		{
			return (AppState.MergeEngine != null && AppState.MergeEngine.ValidateExePath() &&
					File.Exists(QuickBms.ExePath) &&
					File.Exists(QuickBms.PluginPath) &&
					File.Exists(WccLite.ExePath));
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

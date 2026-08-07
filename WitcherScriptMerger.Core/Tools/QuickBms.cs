using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WitcherScriptMerger.Tools
{
	public static class QuickBms
	{
		public static string ExePath = AppState.Settings.Get("QuickBmsPath");
		public static string PluginPath = AppState.Settings.Get("QuickBmsPluginPath");

		// Whether QuickBMS itself (exe + plugin) can be found at all, independent of any
		// specific bundle file - lets a caller that's about to scan many bundles (e.g.
		// ModFileIndex.BuildAsync) check once up front instead of hitting
		// ValidateResources' per-bundle "Can't find QuickBMS..." message once per bundle.
		// Added for WitcherScriptMerger.Headless, the Linux-capable CLI/MCP-only host,
		// which has no bundled QuickBMS/wcc_lite at all - see its CLAUDE.md section and
		// docs/decisions/bundle-format-replacement-spike.md.
		public static bool IsAvailable => File.Exists(ExePath) && File.Exists(PluginPath);

		public static int UnpackFile(string bundlePath, string contentRelativePath, string outputDir)
		{
			if (!ValidateResources(bundlePath))
				return 1;

			if (!Directory.Exists(outputDir))
				Directory.CreateDirectory(outputDir);

			var startInfo = BuildStartInfo($"-Y -f \"{contentRelativePath}\" \"{PluginPath}\" \"{bundlePath}\" \"{outputDir}\"");

			using (var bmsProc = new Process { StartInfo = startInfo })
			{
				bmsProc.Start();
				var output = bmsProc.StandardError.ReadToEnd();  // QuickBMS prints results to std error, even if successful

				if (output.Contains("- 0 files found"))
				{
					var errorMsg = "Error unpacking bundle content file using QuickBMS.\nIts output is below.";
					var outputStart = output.IndexOf("- filter string");
					if (outputStart != -1)
					{
						output = output.Substring(outputStart);
						errorMsg += "\n\n" + output;
					}
					AppState.Notifier.ShowError(errorMsg);
					return 1;
				}

				return 0;
			}
		}

		// Returns Array.Empty<string> (never null) when the bundle or QuickBMS itself
		// can't be found: callers (ModFileIndex.BuildAsync, FileMerger.GetUnpackedFiles)
		// enumerate the result directly, and a null here used to be a real NullReferenceException
		// hazard reachable as soon as a caller stopped gating scans behind
		// Paths.ValidateDependencyPaths() first - which WitcherScriptMerger.Headless does
		// deliberately, so flat-file-only merging still works without QuickBMS/wcc_lite
		// configured. ValidateResources already reports a clear error for why. Flagged in
		// code review, see CLAUDE.md.
		public static string[] GetBundleContentPaths(string bundlePath)
		{
			if (!ValidateResources(bundlePath))
				return Array.Empty<string>();

			var contentPaths = new List<string>();

			var startInfo = BuildStartInfo($"-l \"{PluginPath}\" \"{bundlePath}\"");

			using (var bmsProc = new Process { StartInfo = startInfo })
			{
				bmsProc.Start();
				var output = bmsProc.StandardOutput.ReadToEnd() + "\n\n" + bmsProc.StandardError.ReadToEnd();
				var footerPos = output.LastIndexOf("QuickBMS generic");
				var outputLines = output.Substring(0, footerPos).Split('\n');
				var paths = outputLines
					.Where(line => line.Length > 5)
					.Select(line => line.Substring(line.LastIndexOf(' ')).Trim());
				contentPaths.AddRange(paths);
			}
			return contentPaths.ToArray();
		}

		static bool ValidateResources(string bundlePath)
		{
			if (!File.Exists(bundlePath))
			{
				AppState.Notifier.ShowError("Can't find bundle file:\n\n" + bundlePath, "Missing Bundle");
				return false;
			}
			if (!File.Exists(ExePath))
			{
				AppState.Notifier.ShowError("Can't find QuickBMS at this location:\n\n" + ExePath, "Missing QuickBMS");
				return false;
			}
			if (!File.Exists(PluginPath))
			{
				AppState.Notifier.ShowError("Can't find QuickBMS plugin at this location:\n\n" + PluginPath, "Missing QuickBMS Plugin");
				return false;
			}
			return true;
		}

		static ProcessStartInfo BuildStartInfo(string arguments)
		{
			return new ProcessStartInfo
			{
				FileName = ExePath,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
		}
	}
}

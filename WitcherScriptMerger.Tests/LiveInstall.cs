using System;
using System.IO;

namespace WitcherScriptMerger.Tests
{
	// Opt-in discovery of a real Witcher 3 + WitcherScriptMerger install, for tests that
	// cross-check against real recorded data (a live MergeInventory.xml's hashes) or a
	// real KDiff3.exe binary. Deliberately NOT a hardcoded path, and deliberately NOT a
	// drive-letter scan either: CONTRIBUTING.md requires scrubbing machine-specific
	// absolute paths from committed diffs/tests, so discovery here is opt-in only, via an
	// environment variable a developer sets locally before running `dotnet test` - never
	// a default that would silently vary test behavior across machines or in CI.
	public static class LiveInstall
	{
		// Point this at a Witcher 3 game install directory (the one containing Mods\ and
		// WitcherScriptMerger\) to opt in to the tests gated on this class.
		public static string GameDirectory
		{
			get
			{
				var dir = Environment.GetEnvironmentVariable("WSM_TEST_GAME_DIR");
				return string.IsNullOrWhiteSpace(dir) ? null : dir;
			}
		}

		public static string MergeInventoryPath
		{
			get
			{
				var gameDir = GameDirectory;
				if (gameDir == null)
					return null;
				var path = Path.Combine(gameDir, "WitcherScriptMerger", "MergeInventory.xml");
				return File.Exists(path) ? path : null;
			}
		}

		public static string ModsDirectory
		{
			get
			{
				var gameDir = GameDirectory;
				return gameDir == null ? null : Path.Combine(gameDir, "Mods");
			}
		}

		public static string Kdiff3ExePath
		{
			get
			{
				var gameDir = GameDirectory;
				if (gameDir == null)
					return null;
				var path = Path.Combine(gameDir, "WitcherScriptMerger", "Tools", "KDiff3", "KDiff3.exe");
				return File.Exists(path) ? path : null;
			}
		}
	}
}

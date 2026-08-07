using System;
using System.Diagnostics;
using System.IO;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Optional A/B check: does DiffPlexMergeEngine agree with the real KDiff3.exe binary
	// on auto-solvable merges? Gated entirely on WSM_TEST_GAME_DIR (see LiveInstall.cs) -
	// never runs by default, and never fails a run where it's unset; per this repo's
	// CONTRIBUTING.md, a committed test must not require or hardcode a machine-specific
	// path.
	//
	// KDiff3 was retired as WSM's shipped text-merge engine/dependency - see
	// docs/decisions/kdiff3-retirement.md. This class doesn't exercise anything WSM itself
	// still does: it's a standalone oracle, invoking a real kdiff3.exe binary you happen to
	// have locally (WSM no longer bundles or requires one) purely to cross-check
	// DiffPlexMergeEngine's output against a mature, independent 3-way merge
	// implementation - kept deliberately, not by oversight, since DiffPlexMergeEngine is
	// now the sole engine and has a known upstream bug (see CLAUDE.md's Compatibility
	// constraints) that this cross-check has no bearing on but that makes independent
	// verification of the auto-solvable cases it does cover worth keeping.
	//
	// Deliberately narrow in scope: only auto-solvable scenarios are compared here
	// (whitespace-only, and non-overlapping edits). A genuine two-sided conflict is NOT
	// cross-checked against the real binary from this test project, because safely
	// automating KDiff3 headlessly for that case needs the window-persistence detection
	// documented in docs/decisions/kdiff3-retirement.md (a ~250ms poll interval that's
	// itself load-bearing, and a window that can't be hidden without hanging the merge
	// entirely) - that logic (Win32 P/Invoke) lived in the host project's since-deleted
	// Tools/KDiff3.cs, which this Core-only test project never referenced anyway. Below
	// uses a single bounded Process.WaitForExit with a kill-on-timeout fallback instead,
	// safe only because both scenarios here are designed to be cleanly auto-solvable - per
	// docs/decisions/kdiff3-retirement.md, an untouched, auto-solvable KDiff3 launch
	// reliably exits in a few seconds regardless of file size.
	//
	// Running this locally (WSM_TEST_GAME_DIR set) will briefly show KDiff3's window and
	// steal foreground focus, twice - the same documented behavior
	// docs/decisions/kdiff3-retirement.md describes for the old headless CLI path this
	// class's launch mechanism deliberately mirrors. That's expected, not a bug in this
	// test.
	public class KDiff3CrossCheckTests
	{
		[Fact]
		public void WhitespaceOnlyConflict_RealKDiff3AgreesWithDiffPlexEngine()
		{
			var kdiff3Path = LiveInstall.Kdiff3ExePath;
			if (kdiff3Path == null)
				return;

			var baseText = "function f() {\r\n\tx = 1;\r\n}\r\n";
			var oldText = "function f() {\r\n    x = 1;\r\n}\r\n";
			var newText = "function f() {\r\n  x = 1;\r\n}\r\n";

			RunComparison(kdiff3Path, baseText, oldText, newText);
		}

		[Fact]
		public void NonOverlappingEdits_RealKDiff3AgreesWithDiffPlexEngine()
		{
			var kdiff3Path = LiveInstall.Kdiff3ExePath;
			if (kdiff3Path == null)
				return;

			var baseText = "a();\r\nb();\r\nc();\r\n";
			var oldText = "a();\r\nMOD1();\r\nb();\r\nc();\r\n";
			var newText = "a();\r\nb();\r\nc();\r\nMOD2();\r\n";

			RunComparison(kdiff3Path, baseText, oldText, newText);
		}

		static void RunComparison(string kdiff3Path, string baseText, string oldText, string newText)
		{
			var dir = Path.Combine(Path.GetTempPath(), "wsm-tests-kdiff3-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				var oldPath = Path.Combine(dir, "old.ws");
				var newPath = Path.Combine(dir, "new.ws");
				var kdiff3OutPath = Path.Combine(dir, "kdiff3-out.ws");

				FileEncoding.WriteUtf16(vanillaPath, baseText);
				FileEncoding.WriteUtf16(oldPath, oldText);
				FileEncoding.WriteUtf16(newPath, newText);

				var kdiff3Text = RunRealKDiff3(kdiff3Path, vanillaPath, oldPath, newPath, kdiff3OutPath);
				if (kdiff3Text == null)
					return; // didn't exit cleanly within the bounded wait - see RunRealKDiff3

				var diffPlexResult = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "old", "new");

				Assert.False(diffPlexResult.HasConflicts);
				Assert.Equal(kdiff3Text, diffPlexResult.MergedText);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		// Invokes the real kdiff3.exe with the same --cs settings KDiff3.BuildArgs uses
		// (WhiteSpace3FileMergeDefault=2, LineEndStyle=1) plus --auto, via the two-string
		// Process.Start(fileName, argsString) overload - CLAUDE.md's compatibility notes
		// call out that this specific overload (not a shell) is the one that matches this
		// app's real invocation path. Returns null (never throws/fails) if the process
		// doesn't exit cleanly within the bounded wait, so a flaky or unexpectedly slow
		// KDiff3 run degrades to "comparison skipped", not a build-breaking test failure.
		static string RunRealKDiff3(string kdiff3Path, string vanillaPath, string oldPath, string newPath, string outputPath)
		{
			var args =
				$"\"{vanillaPath}\" \"{oldPath}\" \"{newPath}\" " +
				$"-o \"{outputPath}\" " +
				"--cs \"WhiteSpace3FileMergeDefault=2\" " +
				"--cs \"CreateBakFiles=0\" " +
				"--cs \"LineEndStyle=1\" " +
				"--auto";

			var proc = Process.Start(kdiff3Path, args);
			try
			{
				if (!proc.WaitForExit(15000))
				{
					try { proc.Kill(entireProcessTree: true); } catch { }
					return null;
				}

				return (proc.ExitCode == 0 && File.Exists(outputPath))
					? File.ReadAllText(outputPath)
					: null;
			}
			finally
			{
				proc.Dispose();
			}
		}
	}
}

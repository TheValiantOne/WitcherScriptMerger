using System;
using System.IO;
using System.Text;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Regression coverage for DiffPlexMergeEngine - the fixtures CLAUDE.md's history
	// calls out specifically: a purely-whitespace-only conflict auto-resolving (mirroring
	// KDiff3's --cs "WhiteSpace3FileMergeDefault=2"), a genuine conflict producing
	// well-formed conflict markers, and an encoding-mismatch case (UTF-8/no-BOM mod file
	// against a UTF-16LE+BOM vanilla file) normalizing correctly - the same class of
	// false conflict CLAUDE.md documents as the real baseEffect.ws case.
	//
	// Deliberately never constructs FileMerger.MergeSource via
	// MergeSource.FromFlatFile/FromBundle: those call ModFile.GetModNameFromPath, which
	// reads Paths.ModsDirectory, which reads AppState.Settings - and AppState.Settings's
	// constructor calls Environment.Exit(1) if it can't find a config file next to the
	// entry assembly (see AppSettings.cs), which in a test-host process would abort the
	// entire test run, not just fail one test. MergeSource's fields are all public, so
	// tests build it directly instead - this exercises DiffPlexMergeEngine exactly the
	// same way, since it only ever reads TextFile/Hash/Name off the struct.
	//
	// Any fixture that reaches a genuine-conflict outcome now also reaches
	// FileOpener.Open (see MergeHeadless's comment) - those fixtures swap it for a stub
	// in a try/finally around the real FileOpener.TryOpen, so no test run ever actually
	// launches a process. All fixtures in this class run sequentially (xunit's default:
	// one implicit collection per test class), so swapping this process-wide static field
	// per-test is safe without extra locking.
	public class DiffPlexMergeEngineTests
	{
		[Fact]
		public void BuildMerge_WhitespaceOnlyConflict_AutoResolvesToOldSideVerbatim()
		{
			var baseText = "function f() {\r\n\tx = 1;\r\n}\r\n";
			var oldText = "function f() {\r\n    x = 1;\r\n}\r\n";   // source1: 4-space indent
			var newText = "function f() {\r\n  x = 1;\r\n}\r\n";     // source2: 2-space indent

			var result = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB");

			Assert.False(result.HasConflicts);
			// WhiteSpace3FileMergeDefault=2 means "always pick input B" in KDiff3 terms,
			// which is oldText/source1 here (see KDiff3.BuildArgs' file order: vanilla,
			// source1, source2 map to A, B, C) - so the merge should take oldText's exact
			// whitespace, not some averaged/normalized form.
			Assert.Equal(oldText, result.MergedText);
			Assert.DoesNotContain("<<<<<<<", result.MergedText);
		}

		[Fact]
		public void BuildMerge_WhitespaceOnlyConflict_ToleratesDifferingLineCounts()
		{
			// One side's whitespace-only edit also happens to add a blank line - still
			// purely whitespace once collapsed, so this should still auto-resolve rather
			// than being misclassified as a genuine conflict just because the two sides'
			// piece counts differ (confirmed against DiffPlex's actual block output in
			// this change's verification scratch app before writing this fixture).
			var baseText = "a();\r\nx=1;\r\nb();\r\n";
			var oldText = "a();\r\n    x=1;\r\n\r\nb();\r\n";
			var newText = "a();\r\n\tx=1;\r\nb();\r\n";

			var result = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB");

			Assert.False(result.HasConflicts);
			Assert.DoesNotContain("<<<<<<<", result.MergedText);
		}

		[Fact]
		public void BuildMerge_GenuineConflict_ProducesGitStyleMarkersLabeledWithModNames()
		{
			var baseText = "x = 1;\r\n";
			var oldText = "x = 2;\r\n";
			var newText = "x = 3;\r\n";

			var result = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB");

			Assert.True(result.HasConflicts);
			Assert.Equal(
				"<<<<<<< modA\r\n" +
				"x = 2;\r\n" +
				"||||||| Vanilla\r\n" +
				"x = 1;\r\n" +
				"=======\r\n" +
				"x = 3;\r\n" +
				">>>>>>> modB\r\n",
				result.MergedText);
		}

		[Fact]
		public void BuildMerge_NonOverlappingEdits_MergeBothCleanlyWithoutConflict()
		{
			var baseText = "a();\r\nb();\r\nc();\r\n";
			var oldText = "a();\r\nMOD1();\r\nb();\r\nc();\r\n";
			var newText = "a();\r\nb();\r\nc();\r\nMOD2();\r\n";

			var result = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB");

			Assert.False(result.HasConflicts);
			Assert.Equal("a();\r\nMOD1();\r\nb();\r\nc();\r\nMOD2();\r\n", result.MergedText);
		}

		[Fact]
		public void BuildMerge_InterleavedIndependentEdits_ThrowsDiffAlgorithmExceptionRatherThanCorruptingOutput()
		{
			// Regression test for a confirmed upstream DiffPlex 1.9.0 bug in
			// ThreeWayDiffer.CreateThreeWayDiffBlocks (see BuildMerge's own comment for
			// the full writeup and CLAUDE.md for measured failure rates): one mod
			// inserts a line right after "a();", the other independently changes "b()"
			// to "B()". Before the try/catch + post-loop consistency check this fixture
			// guards, this exact input silently produced WRONG merged output (base's
			// "b();" escaped both the conflict markers and its correct position, while
			// "new"'s edit was captured against an empty base region) with no exception
			// at all - confirmed via a throwaway scratch console app directly against
			// both this engine's BuildMerge and DiffPlex's own official
			// ThreeWayDiffer.CreateMerge. This must now come back as a clearly-typed
			// failure instead of ever risking a corrupted merge.
			var baseText = "a();\r\nb();\r\nc();\r\n";
			var oldText = "a();\r\nnewline();\r\nb();\r\nc();\r\n";
			var newText = "a();\r\nB();\r\nc();\r\n";

			var ex = Assert.Throws<DiffPlexMergeEngine.DiffAlgorithmException>(
				() => DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB"));
			Assert.NotNull(ex.Message);
		}

		[Fact]
		public void MergeHeadless_InterleavedIndependentEdits_SkipsWithoutWritingAnythingIncludingSidecar()
		{
			// Same scenario as the BuildMerge-level fixture above, exercised through the
			// full MergeHeadless path: since the "conflict marker" content itself would
			// have been built from the same untrustworthy piece indices, MergeHeadless
			// must not write a sidecar here either - this is the one case where
			// DiffPlexMergeEngine can't even offer a conflict-marker starting point. No
			// sidecar also means FileOpener.Open must never fire here - stubbed with a
			// call counter (rather than the default real implementation) specifically to
			// verify that negative, not just to avoid a real process launch.
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			var openCallCount = 0;
			FileOpener.Open = _ => { ++openCallCount; return true; };
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "a();\r\nb();\r\nc();\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "a();\r\nnewline();\r\nb();\r\nc();\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "a();\r\nB();\r\nc();\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(source1, source2, new FileInfo(vanillaPath), outputPath);

				Assert.Equal(MergeEngineResult.NeedsManualResolution, result);
				Assert.False(File.Exists(outputPath));
				Assert.False(File.Exists(DiffPlexMergeEngine.GetConflictMarkerPath(outputPath)));
				Assert.Equal(0, openCallCount);
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void BuildMerge_DeletionVersusWhitespaceReformat_IsNotMisclassifiedAsWhitespaceOnly()
		{
			// A genuine content-vs-deletion conflict must never be silently auto-resolved
			// just because the surviving side's content happens to collapse to "" once
			// whitespace is stripped. Base has a whitespace-only separator line; mod1
			// merely trims its trailing spaces (still blank); mod2 deletes the line
			// outright as part of a real edit. Before the fix, both normalized to "" and
			// were treated as equal, silently discarding mod2's deletion.
			var baseText = "a();\r\n   \r\nb();\r\n";
			var oldText = "a();\r\n\r\nb();\r\n";      // mod1: trims trailing spaces, line stays blank
			var newText = "a();\r\nb();\r\n";           // mod2: deletes the blank line entirely

			var result = DiffPlexMergeEngine.BuildMerge(baseText, oldText, newText, "modA", "modB");

			Assert.True(result.HasConflicts);
			Assert.Contains("<<<<<<< modA", result.MergedText);
			Assert.Contains(">>>>>>> modB", result.MergedText);
		}

		[Fact]
		public void MergeHeadless_EncodingMismatch_NormalizesAndProducesUtf16LEWithBomOutput()
		{
			// Mirrors the real baseEffect.ws false-conflict case CLAUDE.md documents:
			// vanilla is UTF-16LE+BOM, one mod file is plain UTF-8 with no BOM. Reading
			// raw bytes with a fixed Encoding (rather than the auto-detecting
			// File.ReadAllText this engine actually uses) would leave a stray U+FEFF
			// glued to the vanilla file's first line, making it never equal the mod
			// file's first line and turning this into a spurious conflict - which is
			// exactly the failure mode this fixture guards against.
			var dir = CreateTempDir();
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "function f() {\r\n\tx = 1;\r\n}\r\n");

				var mod1Path = Path.Combine(dir, "mod1.ws");
				File.WriteAllText(mod1Path, "function f() {\r\n\tx = 1;\r\n\ty = 2;\r\n}\r\n", new UTF8Encoding(false));

				var mod2Path = Path.Combine(dir, "mod2.ws");
				File.WriteAllText(mod2Path, "function f() {\r\n\tx = 1;\r\n}\r\n", new UTF8Encoding(false));

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(source1, source2, new FileInfo(vanillaPath), outputPath);

				Assert.Equal(MergeEngineResult.AutoSolved, result);
				Assert.True(File.Exists(outputPath));
				Assert.True(FileEncoding.HasUtf16LeBom(outputPath));

				var outputBytes = File.ReadAllBytes(outputPath);
				Assert.Equal(0xFF, outputBytes[0]);
				Assert.Equal(0xFE, outputBytes[1]);

				var mergedText = File.ReadAllText(outputPath);
				// Assert.DoesNotContain(string, string) does a culture-aware substring
				// search (CompareInfo, not ordinal) - under which U+FEFF, a zero-width
				// Unicode format character, is collation-ignorable and reports a "match"
				// in any string, even one that doesn't contain it at all (confirmed
				// empirically: mergedText.Contains("\uFEFF") - ordinal - is false, while
				// Assert.DoesNotContain("\uFEFF", mergedText) still fails). The
				// char/IEnumerable<char> overload below does an exact ordinal element
				// comparison instead, which is what this assertion actually means.
				Assert.DoesNotContain('\uFEFF', mergedText);
				Assert.Equal("function f() {\r\n\tx = 1;\r\n\ty = 2;\r\n}\r\n", mergedText);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void MergeHeadless_GenuineConflict_WritesSidecarMarkerFileNotOutputPath()
		{
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			// A genuine conflict now also opens the sidecar via FileOpener (see
			// MergeHeadless_GenuineConflict_OpensSidecarViaFileOpener below) - stubbed out
			// here so this fixture, which only cares about the sidecar file itself, never
			// launches a real process during a test run.
			FileOpener.Open = _ => true;
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "x = 1;\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(source1, source2, new FileInfo(vanillaPath), outputPath);

				Assert.Equal(MergeEngineResult.NeedsManualResolution, result);

				// Never poison the real output path with conflict markers - see
				// DiffPlexMergeEngine.MergeHeadless's comment for why (it would
				// permanently block every future retry of this same conflict, since
				// FileMerger's headless callers treat any existing file at outputPath as
				// "already merged, don't overwrite").
				Assert.False(File.Exists(outputPath));

				var sidecarPath = DiffPlexMergeEngine.GetConflictMarkerPath(outputPath);
				Assert.True(File.Exists(sidecarPath));
				Assert.True(FileEncoding.HasUtf16LeBom(sidecarPath));

				var sidecarText = File.ReadAllText(sidecarPath);
				Assert.Contains("<<<<<<< modA", sidecarText);
				Assert.Contains(">>>>>>> modB", sidecarText);
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void MergeHeadless_GenuineConflict_OpensSidecarViaFileOpener()
		{
			// The headline new-user-experience change this unit adds: a genuine conflict's
			// sidecar isn't just written to disk, it's opened for the user via FileOpener -
			// the same call fires whether this was reached via the CLI/MCP headless path
			// directly or via the GUI's interactive Merge() (which just delegates to this
			// method). FileOpener.Open is swapped for a recording stub rather than letting
			// the real implementation run, so this test verifies the call - the exact path
			// passed - without actually launching a process during a test run.
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			string openedPath = null;
			var openCallCount = 0;
			FileOpener.Open = path =>
			{
				openedPath = path;
				++openCallCount;
				return true;
			};
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "x = 1;\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(source1, source2, new FileInfo(vanillaPath), outputPath);

				Assert.Equal(MergeEngineResult.NeedsManualResolution, result);
				Assert.Equal(1, openCallCount);
				Assert.Equal(DiffPlexMergeEngine.GetConflictMarkerPath(outputPath), openedPath);
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void MergeHeadless_OpenConflictMarkersFalse_WritesSidecarButNeverCallsFileOpener()
		{
			// Regression test for a real bug caught in code review before this shipped:
			// FileMerger.MergeTextHeadless passes openConflictMarkers: !dryRun, so a dry
			// run (MergeConflictsHeadless(dryRun: true), including the MCP merge_conflicts
			// tool's dryRun option) must never launch a real editor/process for a genuine
			// conflict - that's exactly the kind of surprise side effect a "preview only"
			// operation promises not to have. The sidecar file itself is still written
			// (pre-existing behavior, unchanged by this parameter) so a dry run's summary
			// can still point at well-formed conflict-marker content if a caller wants to
			// inspect it - only the auto-open is suppressed.
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			var openCallCount = 0;
			FileOpener.Open = _ => { ++openCallCount; return true; };
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "x = 1;\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(
					source1, source2, new FileInfo(vanillaPath), outputPath, openConflictMarkers: false);

				Assert.Equal(MergeEngineResult.NeedsManualResolution, result);
				Assert.True(File.Exists(DiffPlexMergeEngine.GetConflictMarkerPath(outputPath)));
				Assert.Equal(0, openCallCount);
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void MergeHeadless_RetryAfterConflictThatNowAutoSolves_RemovesStaleSidecar()
		{
			// A conflicting retry leaves a sidecar marker file behind (see the fixture
			// above). If a later retry against updated inputs auto-solves cleanly, the
			// stale sidecar from the earlier failed attempt must not be left sitting next
			// to the fresh output indefinitely - MergeHeadless deletes it on the
			// AutoSolved path specifically to avoid that.
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			FileOpener.Open = _ => true;  // see the fixture above for why this is stubbed
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "x = 1;\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");
				var sidecarPath = DiffPlexMergeEngine.GetConflictMarkerPath(outputPath);

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var engine = new DiffPlexMergeEngine();
				Assert.Equal(MergeEngineResult.NeedsManualResolution, engine.MergeHeadless(source1, source2, new FileInfo(vanillaPath), outputPath));
				Assert.True(File.Exists(sidecarPath));

				// Now "fix" mod2 so this pairing no longer conflicts, and retry.
				FileEncoding.WriteUtf16(mod2Path, "x = 2;\r\n");
				var retrySource2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				Assert.Equal(MergeEngineResult.AutoSolved, engine.MergeHeadless(source1, retrySource2, new FileInfo(vanillaPath), outputPath));
				Assert.True(File.Exists(outputPath));
				Assert.False(File.Exists(sidecarPath));
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void MergeHeadless_NoVanillaFile_SkipsWithoutWritingAnything()
		{
			// A 3-way merge is meaningless without a base - see MergeHeadless's comment
			// for the empty-base bug this guard exists to avoid (confirmed empirically:
			// feeding ThreeWayDiffer an empty base string produces zero diff blocks and a
			// "successful" empty merge, i.e. it would silently produce an empty output
			// file instead of refusing).
			var dir = CreateTempDir();
			try
			{
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");
				var missingVanilla = new FileInfo(Path.Combine(dir, "does-not-exist.ws"));

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().MergeHeadless(source1, source2, missingVanilla, outputPath);

				Assert.Equal(MergeEngineResult.NeedsManualResolution, result);
				Assert.False(File.Exists(outputPath));
				Assert.False(File.Exists(DiffPlexMergeEngine.GetConflictMarkerPath(outputPath)));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void Merge_Interactive_NeverReturnsNeedsManualResolution()
		{
			// Merge's contract explicitly forbids ever returning NeedsManualResolution
			// (that's a headless-only concept) - DiffPlexMergeEngine has no UI to resolve
			// a conflict interactively, so a genuine conflict must come back as Failed
			// instead. Merge() delegates straight to MergeHeadless(), which also means this
			// genuine conflict writes a sidecar and calls FileOpener.Open on the interactive
			// path too - stubbed here for the same reason as the headless fixtures above.
			var dir = CreateTempDir();
			var previousOpener = FileOpener.Open;
			FileOpener.Open = _ => true;
			try
			{
				var vanillaPath = Path.Combine(dir, "vanilla.ws");
				FileEncoding.WriteUtf16(vanillaPath, "x = 1;\r\n");
				var mod1Path = Path.Combine(dir, "mod1.ws");
				FileEncoding.WriteUtf16(mod1Path, "x = 2;\r\n");
				var mod2Path = Path.Combine(dir, "mod2.ws");
				FileEncoding.WriteUtf16(mod2Path, "x = 3;\r\n");

				var outputPath = Path.Combine(dir, "merged.ws");

				var source1 = new FileMerger.MergeSource { TextFile = new FileInfo(mod1Path), Name = "modA" };
				var source2 = new FileMerger.MergeSource { TextFile = new FileInfo(mod2Path), Name = "modB" };

				var result = new DiffPlexMergeEngine().Merge(source1, source2, new FileInfo(vanillaPath), outputPath);

				Assert.Equal(MergeEngineResult.Failed, result);
			}
			finally
			{
				FileOpener.Open = previousOpener;
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void ValidateExePath_AlwaysTrue_NoExternalBinaryToValidate()
		{
			Assert.True(new DiffPlexMergeEngine().ValidateExePath());
		}

		static string CreateTempDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			return dir;
		}
	}
}

using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Regression coverage for Tools/Hasher.cs. CLAUDE.md's Compatibility constraints call
	// this format load-bearing: MergeInventory.xml compares these hashes by plain string
	// equality to detect when a mod file has changed since it was last merged, so any
	// change to Hasher.ComputeHash - not just its numeric result, but its exact output
	// format - would silently make every already-recorded merge "go stale". Expected
	// values below were computed by actually running Hasher's exact algorithm against
	// synthetic inputs in a disposable scratch console app (this repo's own established
	// verification pattern - see CLAUDE.md's Tests section), not hand-derived, to avoid
	// transcription error.
	public class HasherTests
	{
		[Fact]
		public void ComputeHash_EmptyFile_MatchesKnownXxHash32Vector()
		{
			// xxHash32 of a zero-length input with seed 0 is a well-known published test
			// vector (0x02CC5D05) - this confirms Hasher's seed/algorithm choice hasn't
			// silently drifted, independent of the scratch-app cross-check this class
			// otherwise relies on.
			var path = WriteTempFile(Array.Empty<byte>());
			try
			{
				Assert.Equal("2CC5D05", Hasher.ComputeHash(path));
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Fact]
		public void ComputeHash_SmallAsciiContent_MatchesRecordedValue()
		{
			var path = WriteTempFile(Encoding.ASCII.GetBytes("abc"));
			try
			{
				Assert.Equal("32D153FF", Hasher.ComputeHash(path));
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Fact]
		public void ComputeHash_DoesNotZeroPadLeadingNibble()
		{
			// ComputeHash formats with "{0:X}", which never zero-pads - confirmed against
			// a real recorded hash in a live install's MergeInventory.xml, which contains
			// Hash="D830FD" (6 hex digits, i.e. unpadded from the usual 8). Reformatting
			// to a fixed-width "X8" would be exactly the kind of silent output-format
			// change CLAUDE.md warns would make every already-recorded merge hash
			// comparison fail.
			var path = WriteTempFile(Encoding.ASCII.GetBytes("candidate-41"));
			try
			{
				var hash = Hasher.ComputeHash(path);
				Assert.Equal("19AD22", hash);
				Assert.True(hash.Length < 8);
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Fact]
		public void ComputeHash_InputLargerThanReadBuffer_MatchesOneShotHash()
		{
			// ComputeHash streams through an 81920-byte buffer in a loop instead of
			// hashing the whole file in a single call - this independently verifies that
			// chunking via repeated XxHash32.Append calls produces the same result as
			// hashing the same bytes in one call, i.e. the loop's chunk-boundary handling
			// is correct. 100000 bytes deliberately crosses the 81920-byte boundary.
			var bytes = new byte[100000];
			for (var i = 0; i < bytes.Length; ++i)
				bytes[i] = (byte)(i % 251);

			var path = WriteTempFile(bytes);
			try
			{
				var expected = string.Format("{0:X}", XxHash32.HashToUInt32(bytes));
				Assert.Equal(expected, Hasher.ComputeHash(path));
			}
			finally
			{
				File.Delete(path);
			}
		}

		[Fact]
		public void ComputeHash_MissingFile_ThrowsFileNotFoundException()
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ws");
			Assert.Throws<FileNotFoundException>(() => Hasher.ComputeHash(path));
		}

		// Cross-checks a freshly computed hash against a real value recorded in a live
		// install's MergeInventory.xml, per this repo's Tests precedent (CLAUDE.md /
		// CONTRIBUTING.md). Gated entirely on WSM_TEST_GAME_DIR (see LiveInstall.cs) -
		// silently does nothing when unset, so it never fails a machine or CI run that
		// doesn't have a live install configured. Writes a one-line Console message
		// either way (visible via `dotnet test --logger "console;verbosity=detailed"`)
		// stating whether it actually cross-checked anything - a silent no-assertion
		// pass here would look identical to a real cross-check in every other way,
		// which is exactly the ambiguity CLAUDE.md's Tests section warns about.
		[Fact]
		public void ComputeHash_LiveInstallCrossCheck()
		{
			var inventoryPath = LiveInstall.MergeInventoryPath;
			var modsDir = LiveInstall.ModsDirectory;
			if (inventoryPath == null || modsDir == null)
			{
				Console.WriteLine("ComputeHash_LiveInstallCrossCheck: WSM_TEST_GAME_DIR not set or no MergeInventory.xml found - skipped.");
				return;
			}

			var doc = XDocument.Load(inventoryPath);
			foreach (var mergeEl in doc.Root.Elements("Merge"))
			{
				var relativePath = (string)mergeEl.Element("RelativePath");
				if (relativePath == null)
					continue;

				foreach (var modEl in mergeEl.Elements("IncludedMod"))
				{
					var recordedHash = (string)modEl.Attribute("Hash");
					var modName = modEl.Value;
					if (recordedHash == null || string.IsNullOrEmpty(modName))
						continue;

					var modFilePath = Path.Combine(modsDir, modName, "content", "scripts", relativePath);
					if (!File.Exists(modFilePath))
						continue;

					// One real cross-check is enough to catch a format regression -
					// return as soon as we find (and assert against) one.
					Console.WriteLine($"ComputeHash_LiveInstallCrossCheck: cross-checked {modName}'s {relativePath} against recorded hash {recordedHash}.");
					Assert.Equal(recordedHash, Hasher.ComputeHash(modFilePath));
					return;
				}
			}

			// Reached only when a live inventory exists but none of its recorded mod
			// source files are present on disk anymore - nothing to cross-check against,
			// so this intentionally asserts nothing rather than failing.
			Console.WriteLine("ComputeHash_LiveInstallCrossCheck: found a live MergeInventory.xml, but none of its recorded mod source files are still on disk - nothing cross-checked.");
		}

		static string WriteTempFile(byte[] bytes)
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
			File.WriteAllBytes(path, bytes);
			return path;
		}
	}
}

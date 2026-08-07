using System;
using System.IO;
using System.Text;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Direct coverage of the shared encoding helper both merge engines rely on - see
	// FileEncoding.cs's remarks for why File.ReadAllText(path) (auto-detecting) is used
	// for reads instead of decoding raw bytes with a fixed Encoding.
	//
	// Deliberately never references WitcherScriptMerger.Paths beyond its TempBundleContent
	// const (a compile-time literal, so referencing it can't trigger Paths' static field
	// initializers) - see DiffPlexMergeEngineTests' class remarks for why touching
	// Paths/AppState.Settings from a test host is unsafe.
	public class FileEncodingTests
	{
		[Fact]
		public void HasUtf16LeBom_DetectsBomCorrectly()
		{
			var dir = CreateTempDir();
			try
			{
				var utf16Path = Path.Combine(dir, "utf16.ws");
				File.WriteAllText(utf16Path, "hello", new UnicodeEncoding(false, true));
				var utf8Path = Path.Combine(dir, "utf8.ws");
				File.WriteAllText(utf8Path, "hello", new UTF8Encoding(false));

				Assert.True(FileEncoding.HasUtf16LeBom(utf16Path));
				Assert.False(FileEncoding.HasUtf16LeBom(utf8Path));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void HasUtf16LeBom_DoesNotMisidentifyUtf32LeAsUtf16LE()
		{
			// UTF-16LE's BOM (FF FE) is a byte-for-byte prefix of UTF-32LE's own BOM
			// (FF FE 00 00) - a 2-byte-only check would misidentify this file and skip
			// normalization, producing garbled output. Flagged in code review; see
			// FileEncoding.HasUtf16LeBom's remarks.
			var dir = CreateTempDir();
			try
			{
				var utf32Path = Path.Combine(dir, "utf32.ws");
				File.WriteAllText(utf32Path, "hello", new UTF32Encoding(bigEndian: false, byteOrderMark: true));

				Assert.False(FileEncoding.HasUtf16LeBom(utf32Path));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void HasUtf16LeBom_TinyBomOnlyFileStillDetectedAsUtf16LE()
		{
			// A file containing only the 2-byte UTF-16LE BOM and no content at all - the
			// 4-byte read this method now does for the UTF-32LE disambiguation above must
			// not require 4 bytes to actually exist on disk.
			var dir = CreateTempDir();
			try
			{
				var path = Path.Combine(dir, "bomonly.ws");
				File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE });

				Assert.True(FileEncoding.HasUtf16LeBom(path));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void ReadAnyEncoding_AgreesRegardlessOfSourceEncodingAndStripsBom()
		{
			var dir = CreateTempDir();
			try
			{
				var text = "line1\r\nline2\r\n";
				var utf16Path = Path.Combine(dir, "utf16.ws");
				File.WriteAllText(utf16Path, text, new UnicodeEncoding(false, true));
				var utf8Path = Path.Combine(dir, "utf8.ws");
				File.WriteAllText(utf8Path, text, new UTF8Encoding(false));

				var fromUtf16 = FileEncoding.ReadAnyEncoding(utf16Path);
				var fromUtf8 = FileEncoding.ReadAnyEncoding(utf8Path);

				Assert.Equal(text, fromUtf16);
				Assert.Equal(text, fromUtf8);
				Assert.Equal(fromUtf16, fromUtf8);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void WriteUtf16_ProducesExactBomBytesAndCreatesMissingDirectory()
		{
			var dir = CreateTempDir();
			try
			{
				var path = Path.Combine(dir, "nested", "out.ws");
				FileEncoding.WriteUtf16(path, "content\r\n");

				var bytes = File.ReadAllBytes(path);
				Assert.Equal(0xFF, bytes[0]);
				Assert.Equal(0xFE, bytes[1]);
				Assert.Equal("content\r\n", File.ReadAllText(path));
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void EnsureUtf16File_ReturnsOriginalPathWhenAlreadyUtf16LE()
		{
			var dir = CreateTempDir();
			try
			{
				var path = Path.Combine(dir, "vanilla.ws");
				File.WriteAllText(path, "content", new UnicodeEncoding(false, true));

				var result = FileEncoding.EnsureUtf16File(new FileInfo(path), "TestRole");

				Assert.Equal(path, result);
			}
			finally
			{
				Directory.Delete(dir, true);
			}
		}

		[Fact]
		public void EnsureUtf16File_WritesNormalizedTempCopyWhenNotUtf16LE()
		{
			var dir = CreateTempDir();
			string tempCopyDir = null;
			try
			{
				var path = Path.Combine(dir, "mod.ws");
				File.WriteAllText(path, "content", new UTF8Encoding(false));

				var result = FileEncoding.EnsureUtf16File(new FileInfo(path), "TestRole");
				tempCopyDir = Path.GetDirectoryName(result);

				Assert.NotEqual(path, result);
				Assert.True(FileEncoding.HasUtf16LeBom(result));
				Assert.Equal("content", File.ReadAllText(result));
			}
			finally
			{
				Directory.Delete(dir, true);
				// EnsureUtf16File's temp copy goes under the relative "tempbundlecontent"
				// directory (Paths.TempBundleContent's literal value), not under `dir` -
				// clean it up too so repeated test runs don't accumulate copies, matching
				// CLAUDE.md's own noted precedent for clearing this directory between runs.
				if (tempCopyDir != null && Directory.Exists(tempCopyDir))
					Directory.Delete(tempCopyDir, true);
			}
		}

		static string CreateTempDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			return dir;
		}
	}
}

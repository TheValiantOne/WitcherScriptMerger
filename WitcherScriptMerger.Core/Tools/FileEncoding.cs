using System.IO;
using System.Text;

namespace WitcherScriptMerger.Tools
{
	// Shared UTF-16LE+BOM normalization. DiffPlexMergeEngine merges in-process and only
	// needs the text itself, via ReadAnyEncoding/WriteUtf16 below; EnsureUtf16File (an
	// on-disk temp copy, for a tool that has to be handed a file path rather than raw
	// text) has no in-repo caller since KDiff3MergeEngine's retirement (see
	// docs/decisions/kdiff3-retirement.md - this method used to be a private copy inside
	// WitcherScriptMerger/Tools/KDiff3.cs::EnsureUtf16Encoding) but is kept, and still
	// directly unit-tested, for any future file-based tool that needs it.
	//
	// Vanilla .ws files are UTF-16LE with a BOM; mod authors' files are often plain
	// UTF-8/ASCII with no BOM (confirmed against real files on a live install) - see
	// CLAUDE.md's "Text-merge input encoding" compatibility constraint for why normalizing
	// UP to UTF-16LE (never down to UTF-8) matters: the game may not load a merged .ws
	// file that isn't UTF-16LE.
	public static class FileEncoding
	{
		// UTF-16LE with BOM - matches vanilla .ws file encoding. Never normalize merge
		// output toward UTF-8; the game may not load it.
		public static readonly Encoding Utf16LEWithBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

		// A UTF-16LE BOM (FF FE) is also a byte-for-byte prefix of UTF-32LE's own BOM
		// (FF FE 00 00) - reading only 2 bytes would misidentify a UTF-32LE file as
		// already UTF-16LE, skipping normalization and producing garbled comparison/
		// merge output (flagged in code review, see CLAUDE.md; pre-existing limitation
		// carried over unchanged from the original KDiff3.cs::EnsureUtf16Encoding this
		// was ported from, now fixed here since it's shared by both merge engines).
		// UTF-32 isn't a realistic encoding for real .ws/.xml mod files, but reading 2
		// extra bytes to rule it out is cheap and removes the ambiguity outright.
		public static bool HasUtf16LeBom(string path)
		{
			using (var stream = File.OpenRead(path))
			{
				var bom = new byte[4];
				var bytesRead = stream.Read(bom, 0, 4);
				if (bytesRead < 2 || bom[0] != 0xFF || bom[1] != 0xFE)
					return false;

				var looksLikeUtf32Le = bytesRead >= 4 && bom[2] == 0x00 && bom[3] == 0x00;
				return !looksLikeUtf32Le;
			}
		}

		// Reads a file's text regardless of whether it's UTF-16LE+BOM (vanilla's usual
		// encoding) or plain UTF-8/ASCII with no BOM (common for mod authors' files).
		// File.ReadAllText(path) without an explicit encoding auto-detects a BOM (UTF-16LE
		// included) and falls back to UTF-8 when none is present, which is exactly the two
		// cases this codebase needs - and, importantly, StreamReader strips a detected BOM
		// from the returned text. Decoding the raw bytes manually with a fixed Encoding
		// instead (e.g. Encoding.Unicode.GetString(File.ReadAllBytes(path))) does NOT strip
		// it, leaving a stray U+FEFF glued to the first line - confirmed empirically in this
		// change's verification scratch app. That stray character would make a UTF-16LE
		// vanilla file's first line never equal a UTF-8 mod file's first line, silently
		// reproducing the exact class of false conflict this method exists to avoid (see
		// CLAUDE.md's baseEffect.ws case).
		public static string ReadAnyEncoding(string path) => File.ReadAllText(path);

		// Writes text as UTF-16LE with BOM - the encoding every merge engine's output must
		// use, matching vanilla's own encoding (see class remarks above).
		public static void WriteUtf16(string path, string text)
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			File.WriteAllText(path, text, Utf16LEWithBom);
		}

		// Atomic variant for files an external reader may open concurrently: writes to
		// a same-directory temp file, then swaps it into place with File.Move
		// (same-volume rename - atomic on NTFS). Exists because merged script output
		// lands inside the live game's Mods tree, and the game's script compiler (or a
		// mod manager) reading a merged file mid-WriteAllText sees a truncated script:
		// class members near the file's tail simply absent, compiling into
		// "'X' is not a member of Y" errors that look exactly like a bad merge -
		// observed on a real install when the game was launched while a 40-file
		// headless re-merge was still writing.
		public static void WriteUtf16Atomic(string path, string text)
		{
			var dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			var tempPath = path + ".wsm-tmp";
			File.WriteAllText(tempPath, text, Utf16LEWithBom);
			File.Move(tempPath, path, overwrite: true);
		}

		// Ensures an on-disk copy of `file` is UTF-16LE+BOM, writing a temp copy under
		// Paths.TempBundleContent\Encoding\<role>\ only when a copy is actually needed.
		// For a tool that must be handed a file path rather than raw text (the now-retired
		// KDiff3 engine's command line was the original, and so far only, such caller -
		// see docs/decisions/kdiff3-retirement.md) - an in-process engine that reads/writes
		// strings directly doesn't need this at all, just ReadAnyEncoding/WriteUtf16 above.
		public static string EnsureUtf16File(FileInfo file, string role)
		{
			if (HasUtf16LeBom(file.FullName))
				return file.FullName;

			var text = File.ReadAllText(file.FullName, Encoding.UTF8);

			var tempDir = Path.Combine(Paths.TempBundleContent, "Encoding", role);
			Directory.CreateDirectory(tempDir);

			var tempPath = Path.Combine(tempDir, file.Name);
			File.WriteAllText(tempPath, text, Utf16LEWithBom);

			return tempPath;
		}
	}
}

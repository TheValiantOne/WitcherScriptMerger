using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Tools
{
    static class KDiff3
    {
        public static string ExePath = Program.Settings.Get("KDiff3Path");

        public static int Run(
            FileMerger.MergeSource source1,
            FileMerger.MergeSource source2,
            FileInfo vanillaFile,
            string outputPath)
        {
            if (!File.Exists(ExePath))
            {
                Program.Notifier.ShowError("Can't find KDiff3 at this location:\n\n" + ExePath, "Missing KDiff3");
                return 1;
            }

            var outputDir = Path.GetDirectoryName(outputPath);

            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            var args = BuildArgs(source1, source2, vanillaFile, outputPath, out var hasVanillaVersion);

            if (!Program.Settings.Get<bool>("ReviewEachMerge") && hasVanillaVersion)
            {
                if (source1.TextFile.FullName.EqualsIgnoreCase(outputPath)
                    && source2.Hash != null && source2.Hash.IsOutdated)
                {
                    Program.Notifier.ShowMessage(
                        "You are merging an updated mod file into a merge created with a previous version of the file.\n\n" +
                        "You should carefully inspect this merge, because KDiff3's auto-solving behavior KEEPS changes from the previous version of the mod file that have been REMOVED in the new version.",
                        "Warning",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else
                    args += " --auto";
            }

            var kdiff3Path = ResolveExePath();

            var kdiff3Proc = Process.Start(kdiff3Path, args);
            kdiff3Proc.WaitForExit();

            return kdiff3Proc.ExitCode;
        }

        public enum HeadlessResult { AutoSolved, NeedsManualResolution, Failed }

        // KDiff3 has no fail-fast mode - its own docs (doc/dothemerge.html) say plainly
        // that when manual interaction is needed, a merge window opens, even in its own
        // batch/automation mode. So this doesn't ask KDiff3 to behave headlessly; it
        // launches it normally and detects a stuck merge itself: KDiff3 always briefly
        // shows a plain "Conflicts" window on startup regardless of outcome (not a
        // signal), but only a genuine unresolved conflict leaves open a second window
        // titled "<L1> <-> <L2>[ <-> <L3>] - KDiff3" - the actual comparison/merge
        // editor. If that window is still open past a short grace period, this treats
        // the merge as needing manual resolution, kills the process, and reports it as
        // skipped rather than waiting on it (verified empirically against real and
        // synthetic conflicts - see CLAUDE.md). Never writes to the real outputPath
        // directly: KDiff3's -o target is a scratch path, only copied into place after
        // a confirmed clean exit, so a killed process can never leave a partial file
        // where the game would load it.
        public static HeadlessResult RunHeadless(
            FileMerger.MergeSource source1,
            FileMerger.MergeSource source2,
            FileInfo vanillaFile,
            string outputPath)
        {
            if (!File.Exists(ExePath))
            {
                Program.Notifier.ShowError("Can't find KDiff3 at this location:\n\n" + ExePath, "Missing KDiff3");
                return HeadlessResult.Failed;
            }

            var scratchDir = Path.Combine(Paths.TempBundleContent, "HeadlessOutput");
            Directory.CreateDirectory(scratchDir);
            var scratchOutputPath = Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + Path.GetExtension(outputPath));

            var args = BuildArgs(source1, source2, vanillaFile, scratchOutputPath, out var hasVanillaVersion);

            if (hasVanillaVersion
                && source1.TextFile.FullName.EqualsIgnoreCase(outputPath)
                && source2.Hash != null && source2.Hash.IsOutdated)
            {
                // The interactive path skips --auto here and relies on the user reviewing
                // manually - there's nothing safe to do headlessly but skip it too.
                Program.Notifier.ShowMessage(
                    $"Skipped {source1.Name} + {source2.Name}: merging an updated mod file into a merge " +
                    "created with a previous version needs manual review (KDiff3's auto-solving would keep " +
                    "changes from the previous version that have been removed in the new one).",
                    "Skipped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return HeadlessResult.NeedsManualResolution;
            }
            args += " --auto";

            var proc = Process.Start(ResolveExePath(), args);
            var pid = proc.Id;
            var sw = Stopwatch.StartNew();

            const int gracePeriodMs = 3000;
            const int backstopTimeoutMs = 60000;
            long? mergeWindowFirstSeenMs = null;

            while (!proc.HasExited && sw.ElapsedMilliseconds < backstopTimeoutMs)
            {
                if (HasVisibleMergeWindow(pid))
                {
                    mergeWindowFirstSeenMs ??= sw.ElapsedMilliseconds;
                    if (sw.ElapsedMilliseconds - mergeWindowFirstSeenMs.Value > gracePeriodMs)
                        break;
                }
                else
                {
                    mergeWindowFirstSeenMs = null;
                }
                proc.Refresh();
                Thread.Sleep(250);
            }

            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                DeleteIfExists(scratchOutputPath);
                Program.Notifier.ShowMessage(
                    $"Skipped {source1.Name} + {source2.Name}: needs manual conflict resolution.",
                    "Skipped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return HeadlessResult.NeedsManualResolution;
            }

            if (proc.ExitCode == 0 && File.Exists(scratchOutputPath))
            {
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                File.Copy(scratchOutputPath, outputPath, overwrite: true);
                DeleteIfExists(scratchOutputPath);
                return HeadlessResult.AutoSolved;
            }

            DeleteIfExists(scratchOutputPath);
            return HeadlessResult.Failed;
        }

        static string BuildArgs(
            FileMerger.MergeSource source1,
            FileMerger.MergeSource source2,
            FileInfo vanillaFile,
            string outputPath,
            out bool hasVanillaVersion)
        {
            hasVanillaVersion = (vanillaFile != null && vanillaFile.Exists);

            var vanillaPath = hasVanillaVersion ? EnsureUtf16Encoding(vanillaFile, "Vanilla") : null;
            var source1Path = EnsureUtf16Encoding(source1.TextFile, "Source1");
            var source2Path = EnsureUtf16Encoding(source2.TextFile, "Source2");

            var args = (hasVanillaVersion
                ? "\"" + vanillaPath + "\" "
                : "");

            args +=
                $"\"{source1Path}\" \"{source2Path}\" " +
                $"-o \"{outputPath}\" " +
                "--cs \"WhiteSpace3FileMergeDefault=2\" " +
                "--cs \"CreateBakFiles=0\" " +
                "--cs \"LineEndStyle=1\" " +
                "--cs \"FollowFileLinks=1\" " +
                "--cs \"FollowDirLinks=1\"";

            if (!Program.Settings.Get<bool>("ShowPathsInKDiff3"))
            {
                if (hasVanillaVersion)
                    args += $" --L1 Vanilla --L2 \"{source1.Name}\" --L3 \"{source2.Name}\"";
                else
                    args += $" --L1 \"{source1.Name}\" --L2 \"{source2.Name}\"";
            }

            return args;
        }

        static string ResolveExePath()
        {
            return Path.IsPathRooted(ExePath)
                ? ExePath
                : Path.Combine(Environment.CurrentDirectory, ExePath);
        }

        static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static bool HasVisibleMergeWindow(int pid)
        {
            var found = false;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid == (uint)pid && NativeMethods.IsWindowVisible(hWnd))
                {
                    var sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                    if (sb.ToString().EndsWith(" - KDiff3", StringComparison.Ordinal))
                        found = true;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static class NativeMethods
        {
            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

            [DllImport("user32.dll")]
            public static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        }

        // Vanilla .ws files are UTF-16LE with a BOM, but mod authors' files are often
        // plain UTF-8/ASCII with no BOM. KDiff3 has no way to be told each input's
        // encoding on the command line, so a mismatch makes it treat an entire file as
        // unmatchable and fall back to manual (GUI) conflict resolution instead of
        // auto-solving. Normalizing non-UTF-16LE inputs up to match vanilla's encoding
        // (never down to UTF-8, which the game might not load) fixes this without
        // touching the original files.
        static string EnsureUtf16Encoding(FileInfo file, string role)
        {
            using (var stream = File.OpenRead(file.FullName))
            {
                var bom = new byte[2];
                if (stream.Read(bom, 0, 2) == 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                    return file.FullName;
            }

            var text = File.ReadAllText(file.FullName, Encoding.UTF8);

            var tempDir = Path.Combine(Paths.TempBundleContent, "Encoding", role);
            Directory.CreateDirectory(tempDir);

            var tempPath = Path.Combine(tempDir, file.Name);
            File.WriteAllText(tempPath, text, Encoding.Unicode);

            return tempPath;
        }
    }
}

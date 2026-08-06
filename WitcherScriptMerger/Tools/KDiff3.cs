using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

            var hasVanillaVersion = (vanillaFile != null && vanillaFile.Exists);

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

            if (!Program.Settings.Get<bool>("ReviewEachMerge") && hasVanillaVersion)
            {
                if (source1.TextFile.FullName.EqualsIgnoreCase(outputPath)
                    && source2.Hash != null && source2.Hash.IsOutdated)
                {
                    Program.Notifier.ShowMessage(
                        "You are merging an updated mod file into a merge created with a previous version of the file.\n\n" +
                        "You should carefully inspect this merge, because KDiff3's auto-solving behavior KEEPS changes from the previous version of the mod file that have been REMOVED in the new version.",
                        "Warning",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                }
                else
                    args += " --auto";
            }

            var kdiff3Path = (Path.IsPathRooted(ExePath)
                ? ExePath
                : Path.Combine(Environment.CurrentDirectory, ExePath));

            var kdiff3Proc = Process.Start(kdiff3Path, args);
            kdiff3Proc.WaitForExit();

            return kdiff3Proc.ExitCode;
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

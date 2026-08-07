using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Forms;

namespace WitcherScriptMerger.Inventory
{
	// Host-side counterpart to Core's Inventory/FileMerger.cs, restoring exactly what
	// that class used to do before the Core/host project split: drive an interactive,
	// TreeNode-driven merge on a BackgroundWorker and pop up MergeReportForm/
	// PackReportForm afterward. Its public API (constructor shape,
	// MergeByTreeNodesAsync, RepackBundleAsync) deliberately mirrors the old FileMerger
	// so MainForm's call sites needed only a type-name change - see CLAUDE.md/the PR
	// description for the full split rationale.
	//
	// This class does the TreeNode-specific extraction (pulling relative paths/mod
	// names/hashes out of TreeNode.GetMetadata()) and owns the BackgroundWorker; Core's
	// FileMerger does the actual merge orchestration and never sees a TreeNode.
	class InteractiveMergeRunner
	{
		public MergeProgressInfo ProgressInfo => _fileMerger.ProgressInfo;

		FileMerger _fileMerger;
		BackgroundWorker _bgWorker;

		public InteractiveMergeRunner(
			MergeInventory inventory,
			ProgressChangedEventHandler progressHandler,
			RunWorkerCompletedEventHandler completedHandler)
		{
			_fileMerger = new FileMerger(inventory)
			{
				OnMergeReport = ShowMergeReport,
				OnPackReport = ShowPackReport,
			};

			_bgWorker = new BackgroundWorker
			{
				WorkerReportsProgress = true
			};
			_bgWorker.ProgressChanged += progressHandler;
			_fileMerger.ProgressInfo.PropertyChanged += (sender, e) =>
			{
				_bgWorker.ReportProgress(0, _fileMerger.ProgressInfo);
			};
			_bgWorker.RunWorkerCompleted += completedHandler;
		}

		~InteractiveMergeRunner()
		{
			if (_bgWorker != null)
				_bgWorker.Dispose();
		}

		public void MergeByTreeNodesAsync(IEnumerable<TreeNode> fileNodesToMerge, string mergedModName)
		{
			// TreeNode extraction (ExtractRequest) happens inside DoWork, on the
			// BackgroundWorker's thread - not out here on the UI thread - matching
			// where the pre-split FileMerger.MergeByTreeNodesAsync did its own
			// equivalent TreeNode traversal. This isn't just fidelity for its own
			// sake: ExtractRequest dereferences node metadata and casts Tag with no
			// null/type check, and BackgroundWorker captures any exception from
			// DoWork into RunWorkerCompletedEventArgs.Error instead of letting it
			// propagate. Extracting out here instead would let that same exception
			// throw synchronously from btnMergeFiles_Click on the UI thread, which
			// modern .NET WinForms terminates the process for by default (unlike
			// .NET Framework's more forgiving behavior) - turning what was at worst a
			// silently-swallowed no-op (OnMergeComplete never inspects e.Error
			// either, both before and after this split) into a hard crash.
			_bgWorker.DoWork += (sender, e) =>
			{
				var requests = fileNodesToMerge.Select(ExtractRequest).ToArray();
				_fileMerger.MergeFilesInteractive(requests, mergedModName);
			};
			_bgWorker.RunWorkerAsync();
		}

		static FileMerger.InteractiveMergeRequest ExtractRequest(TreeNode fileNode)
		{
			var isBundle = (ModFileCategory)fileNode.Parent.Tag == Categories.BundleText;

			var checkedModNodes = fileNode.GetTreeNodes().Where(modNode => modNode.Checked).ToArray();

			var orderedSources = checkedModNodes.Select(modNode =>
			{
				var metadata = modNode.GetMetadata();
				var file = new FileInfo(metadata.FilePath);
				return isBundle
					? FileMerger.MergeSource.FromBundle(file, metadata.FileHash)
					: FileMerger.MergeSource.FromFlatFile(file, metadata.FileHash);
			}).ToArray();

			return new FileMerger.InteractiveMergeRequest
			{
				RelativePath = fileNode.Text,
				IsBundle = isBundle,
				VanillaFilePath = isBundle ? null : fileNode.GetMetadata().FilePath,
				OrderedSources = orderedSources,
			};
		}

		public void RepackBundleAsync(string bundlePath)
		{
			if (_bgWorker.IsBusy)
				throw new Exception("BackgroundWorker can't run 2 tasks concurrently.");
			_bgWorker.DoWork += (sender, e) =>
			{
				_fileMerger.RepackBundle(bundlePath);
			};
			_bgWorker.RunWorkerAsync();
		}

		void ShowMergeReport(FileMerger.MergeReportData data)
		{
			if (Program.Settings.Get<bool>("PlayCompletionSounds"))
			{
				System.Media.SystemSounds.Asterisk.Play();
			}
			if (Program.Settings.Get<bool>("ReportAfterMerge"))
			{
				using (var reportForm = new MergeReportForm(
					data.MergeNum, data.TotalMergeCount,
					data.Source1Path, data.Source2Path, data.OutputPath,
					data.Source1Name, data.Source2Name))
				{
					_fileMerger.ProgressInfo.CurrentAction = "Showing merge report";
					Program.MainForm.ShowModal(reportForm);
				}
			}
		}

		void ShowPackReport(string bundlePath)
		{
			if (Program.Settings.Get<bool>("PlayCompletionSounds"))
			{
				System.Media.SystemSounds.Asterisk.Play();
			}
			if (Program.Settings.Get<bool>("ReportAfterPack"))
			{
				using (var reportForm = new PackReportForm(bundlePath))
				{
					_fileMerger.ProgressInfo.CurrentAction = "Showing pack report";
					Program.MainForm.ShowModal(reportForm);
				}
			}
		}
	}
}

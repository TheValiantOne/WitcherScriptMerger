using System.Linq;
using System.Windows.Forms;

namespace WitcherScriptMerger.LoadOrder
{
	static class LoadOrderValidator
	{
		public static void ValidateAndFix(CustomLoadOrder loadOrder)
		{
			if (!loadOrder.Mods.Any())
				return;

			var mergedModName = Paths.RetrieveMergedModName();
			var mergedMod = loadOrder.Mods.Find(m => m.ModName.EqualsIgnoreCase(mergedModName));

			if (mergedMod != null && mergedMod == loadOrder.GetTopPriorityEnabledMod())
				return;

			var choice = PromptToPrioritizeMergedMod(loadOrder.FilePath);
			if (choice == DialogResult.Yes)
			{
				PrioritizeMergedMod(loadOrder, mergedMod);
			}
			else if (choice == DialogResult.Cancel && Program.Notifier.IsInteractive)  // Never
			{
				// IsInteractive guard: HeadlessMergeNotifier's fixed default for
				// YesNoCancel is Cancel, which would otherwise persist this setting
				// on every headless run that reaches here.
				Program.Settings.Set("ValidateCustomLoadOrder", false);
				Program.Settings.Save();
			}
		}

		static DialogResult PromptToPrioritizeMergedMod(string modsSettingsPath)
		{
			// Known, accepted regression: the Cancel button used to be relabeled
			// "Ne&ver" via MessageBoxManager.Register()/Unregister(), which worked
			// only because the old MessageBox.Show call ran on the same background
			// thread (Register()'s SetWindowsHookEx is thread-affine) as this
			// method. Program.Notifier.ShowMessage (MainForm.ShowMessage) Invokes
			// the actual MessageBox.Show onto the UI thread, so that hook can no
			// longer see the dialog's window messages - relabeling can't be
			// preserved without adding custom button-text support to
			// IMergeNotifier, which is out of scope here. The Cancel button now
			// reads "Cancel"; clicking it still permanently disables this check
			// (see the IsInteractive-guarded branch above), just without a label
			// saying so. DialogResult semantics and this method's return value are
			// otherwise unchanged.
			return Program.Notifier.ShowMessage(
				$"{modsSettingsPath}\n\n" +
				"Detected custom load order in the file above, and merged files aren't configured to load first.\n\n" +
				"Would you like Script Merger to modify your custom load order so that your merged files have top priority?",
				"Custom Load Order Problem",
				MessageBoxButtons.YesNoCancel,
				MessageBoxIcon.Exclamation,
				MessageBoxDefaultButton.Button2);
		}

		static void PrioritizeMergedMod(CustomLoadOrder loadOrder, ModLoadSetting mergedModSetting)
		{
			// Priority of min - 1 will be incremented to min
			var priority = CustomLoadOrder.TopPriority - 1;

			if (mergedModSetting != null)
			{
				mergedModSetting.IsEnabled = true;
				mergedModSetting.Priority = priority;
			}
			else
			{
				loadOrder.Mods.Insert(0, new ModLoadSetting
				{
					ModName = Paths.RetrieveMergedModName(),
					IsEnabled = true,
					Priority = priority
				});
			}

			IncrementLeadingContiguousPriorities(loadOrder, priority);

			loadOrder.Save();
		}

		static void IncrementLeadingContiguousPriorities(CustomLoadOrder loadOrder, int startingPriority)
		{
			var nextPriority = startingPriority + 1;
			var modsToIncrement = loadOrder.Mods.Where(mod => mod.Priority == startingPriority).ToArray();
			var displacedMods = loadOrder.Mods.Where(mod => mod.Priority == nextPriority).ToArray();

			if (!modsToIncrement.Any())
				return;

			if (displacedMods.Any() &&
				nextPriority < CustomLoadOrder.BottomPriority)
			{
				IncrementLeadingContiguousPriorities(loadOrder, nextPriority);
			}

			foreach (var mod in modsToIncrement)
				++mod.Priority;
		}
	}
}

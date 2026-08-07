using System.Linq;

namespace WitcherScriptMerger.LoadOrder
{
	public static class LoadOrderValidator
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
			if (choice == NotifyResult.Yes)
			{
				PrioritizeMergedMod(loadOrder, mergedMod);
			}
			else if (choice == NotifyResult.Cancel)  // Never
			{
				AppState.Settings.Set("ValidateCustomLoadOrder", false);
				AppState.Settings.Save();
			}
		}

		// Routed through IMergeNotifier rather than a direct MessageBox.Show(...) call
		// (as this used before the Core/host project split) since Core can't reference
		// System.Windows.Forms at all.
		//
		// Before the split, the direct MessageBox.Show(...) call used
		// MessageBoxManager to relabel the Cancel button "Ne&ver", because Cancel's
		// real effect here is destructive and permanent (ValidateAndFix's Cancel
		// branch below sets ValidateCustomLoadOrder=false and saves it to App.config -
		// this prompt is never shown again after that). IMergeNotifier has no hook
		// for relabeling a button (and MessageBoxManager's relabeling was almost
		// certainly already silently broken even before this split - it hooks via
		// AppDomain.GetCurrentThreadId(), a deprecated API that doesn't reliably
		// return the real Win32 thread ID SetWindowsHookEx needs). Either way, a
		// plain-captioned "Cancel" button carries none of that "this is permanent"
		// signal on its own, so the warning is now spelled out in the message body
		// instead, where it doesn't depend on any button-relabeling mechanism working.
		static NotifyResult PromptToPrioritizeMergedMod(string modsSettingsPath)
		{
			return AppState.Notifier.ShowMessage(
				$"{modsSettingsPath}\n\n" +
				"Detected custom load order in the file above, and merged files aren't configured to load first.\n\n" +
				"Would you like Script Merger to modify your custom load order so that your merged files have top priority?\n\n" +
				"Yes: fix it now.\n" +
				"No: leave it as-is for now; you'll be asked again next time.\n" +
				"Cancel: NEVER ask again - permanently disables this check.",
				"Custom Load Order Problem",
				NotifyButtons.YesNoCancel,
				DialogIcon.Exclamation,
				NotifyResult.No);
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

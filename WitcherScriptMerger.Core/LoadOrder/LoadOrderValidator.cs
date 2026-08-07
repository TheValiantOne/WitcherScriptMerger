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
		// System.Windows.Forms at all. This does mean losing two purely cosmetic
		// touches the direct call had: MessageBoxManager's "Ne&ver" caption on the
		// Cancel button (shown as plain "Cancel" now) and defaulting focus to the No
		// button - both acceptable per the Core split's design notes; see the PR
		// description.
		static NotifyResult PromptToPrioritizeMergedMod(string modsSettingsPath)
		{
			return AppState.Notifier.ShowMessage(
				$"{modsSettingsPath}\n\n" +
				"Detected custom load order in the file above, and merged files aren't configured to load first.\n\n" +
				"Would you like Script Merger to modify your custom load order so that your merged files have top priority?",
				"Custom Load Order Problem",
				NotifyButtons.YesNoCancel,
				NotifyIcon.Exclamation);
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

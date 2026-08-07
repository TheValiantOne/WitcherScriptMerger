using System;

namespace WitcherScriptMerger
{
	// Console-based IMergeNotifier for CLI mode. Never blocks on user input -
	// every decision has a fixed, non-destructive default (don't overwrite,
	// don't use a conflicting merge name, don't retry) so a batch run can
	// never hang waiting for a prompt nobody is watching.
	class HeadlessMergeNotifier : IMergeNotifier
	{
		public NotifyResult ShowMessage(string text,
			string title = "",
			NotifyButtons buttons = NotifyButtons.OK,
			DialogIcon icon = DialogIcon.None,
			NotifyResult defaultResult = NotifyResult.None)
		{
			Write(text, title, icon);

			// A caller-supplied defaultResult is, by IMergeNotifier's contract, the
			// answer that caller considers safe/non-destructive for this specific
			// prompt (see IMergeNotifier.ShowMessage's doc comment) - honor it
			// directly rather than falling through to the generic per-button-set
			// guess below. This matters beyond just respecting the caller's intent:
			// for a button set whose generic "safe" answer doesn't actually hold for
			// every call site (e.g. YesNoCancel's generic Cancel-is-safest guess is
			// wrong for LoadOrderValidator, where Cancel is the one destructive,
			// permanent choice), only the caller - not this generic table - actually
			// knows which answer is safe.
			if (defaultResult != NotifyResult.None)
				return defaultResult;

			return buttons switch
			{
				NotifyButtons.OK => NotifyResult.OK,
				NotifyButtons.YesNo => NotifyResult.No,
				NotifyButtons.YesNoCancel => NotifyResult.Cancel,
				NotifyButtons.AbortRetryIgnore => NotifyResult.Abort,
				NotifyButtons.RetryCancel => NotifyResult.Cancel,
				NotifyButtons.OKCancel => NotifyResult.Cancel,
				_ => NotifyResult.Cancel,
			};
		}

		public NotifyResult ShowError(string text, string title = "Error")
		{
			Write(text, title, DialogIcon.Error);
			return NotifyResult.OK;
		}

		static void Write(string text, string title, DialogIcon icon)
		{
			var prefix = string.IsNullOrEmpty(title) ? "WSM" : title;
			var line = $"[{prefix}] {text}";

			if (icon == DialogIcon.Error || icon == DialogIcon.Warning || icon == DialogIcon.Exclamation)
				Console.Error.WriteLine(line);
			else
				Console.WriteLine(line);
		}
	}
}

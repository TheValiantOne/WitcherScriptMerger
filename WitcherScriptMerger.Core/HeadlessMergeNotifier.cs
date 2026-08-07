using System;

namespace WitcherScriptMerger
{
	// Console-based IMergeNotifier for CLI mode. Never blocks on user input -
	// every decision has a fixed, non-destructive default (don't overwrite,
	// don't use a conflicting merge name, don't retry) so a batch run can
	// never hang waiting for a prompt nobody is watching.
	class HeadlessMergeNotifier : IMergeNotifier
	{
		public bool IsInteractive => false;

		public NotifyResult ShowMessage(string text,
			string title = "",
			NotifyButtons buttons = NotifyButtons.OK,
			NotifyIcon icon = NotifyIcon.None)
		{
			Write(text, title, icon);

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
			Write(text, title, NotifyIcon.Error);
			return NotifyResult.OK;
		}

		static void Write(string text, string title, NotifyIcon icon)
		{
			var prefix = string.IsNullOrEmpty(title) ? "WSM" : title;
			var line = $"[{prefix}] {text}";

			if (icon == NotifyIcon.Error || icon == NotifyIcon.Warning || icon == NotifyIcon.Exclamation)
				Console.Error.WriteLine(line);
			else
				Console.WriteLine(line);
		}
	}
}

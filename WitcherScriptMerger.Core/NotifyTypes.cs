namespace WitcherScriptMerger
{
	// Neutral, WinForms-free equivalents of System.Windows.Forms.DialogResult /
	// MessageBoxButtons / MessageBoxIcon, so IMergeNotifier can live in Core without
	// referencing System.Windows.Forms. MainForm (host project) translates these
	// to/from the real WinForms types around actual MessageBox.Show(...) calls.

	// 1:1 with the DialogResult members this codebase actually returns/compares against.
	public enum NotifyResult
	{
		None,
		OK,
		Cancel,
		Abort,
		Retry,
		Ignore,
		Yes,
		No,
	}

	// Mirrors MessageBoxButtons - the full set HeadlessMergeNotifier already handles
	// defensively, even though not every value has a real call site yet.
	public enum NotifyButtons
	{
		OK,
		OKCancel,
		AbortRetryIgnore,
		YesNoCancel,
		YesNo,
		RetryCancel,
	}

	// Mirrors the subset of MessageBoxIcon actually used at real call sites.
	public enum NotifyIcon
	{
		None,
		Warning,
		Error,
		Exclamation,
		Information,
		Question,
	}
}

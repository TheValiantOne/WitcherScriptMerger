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

	// Mirrors the subset of MessageBoxIcon actually used at real call sites. Deliberately
	// NOT named "NotifyIcon": this type lives in the root WitcherScriptMerger namespace,
	// which every host-project file under WitcherScriptMerger.Forms/.Controls/etc. can
	// already see without a `using` (nested-namespace lookup) - naming it NotifyIcon
	// would silently shadow System.Windows.Forms.NotifyIcon (the tray-icon control
	// class) for any unqualified reference in host code, since an enclosing-namespace
	// type wins over a using-imported one in C#'s simple-name resolution.
	public enum DialogIcon
	{
		None,
		Warning,
		Error,
		Exclamation,
		Information,
		Question,
	}
}

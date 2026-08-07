namespace WitcherScriptMerger
{
	// Implemented by HeadlessMergeNotifier (Core, console output, fixed non-destructive
	// defaults) and by MainForm (host project, translates to/from real WinForms
	// MessageBox.Show(...)/DialogResult around these neutral types) - see CLAUDE.md's
	// IMergeNotifier section. Public: MainForm implements this across the Core/host
	// assembly boundary.
	//
	// ShowModal(Form) was deliberately dropped from this interface during the Core
	// split: every call site (report-form popups) is GUI-only, interactive code that
	// already lives in the host project, so it calls MainForm's ShowModal directly
	// instead of going through the notifier abstraction. See the PR description for
	// the full reasoning.
	public interface IMergeNotifier
	{
		bool IsInteractive { get; }

		NotifyResult ShowMessage(string text,
			string title = "",
			NotifyButtons buttons = NotifyButtons.OK,
			NotifyIcon icon = NotifyIcon.None);

		NotifyResult ShowError(string text, string title = "Error");
	}
}

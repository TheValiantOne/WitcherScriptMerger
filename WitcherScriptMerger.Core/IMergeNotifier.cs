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
	//
	// IsInteractive was also dropped here (it had zero read call sites anywhere in
	// the codebase, before or after the Core split - confirmed dead code, not
	// something this split made unused).
	public interface IMergeNotifier
	{
		// defaultResult picks which button is focused by default when shown
		// interactively (NotifyResult.None means "use the button set's own natural
		// default", e.g. the leftmost button) - added specifically so a caller can
		// request a safe answer be pre-focused for a destructive YesNoCancel-style
		// prompt (see LoadOrderValidator.PromptToPrioritizeMergedMod), matching what a
		// direct MessageBox.Show(..., MessageBoxDefaultButton) call could do before
		// the Core split. Headless mode ignores it - HeadlessMergeNotifier already
		// returns a fixed, non-destructive answer for every button set regardless of
		// what a caller might prefer.
		NotifyResult ShowMessage(string text,
			string title = "",
			NotifyButtons buttons = NotifyButtons.OK,
			DialogIcon icon = DialogIcon.None,
			NotifyResult defaultResult = NotifyResult.None);

		NotifyResult ShowError(string text, string title = "Error");
	}
}

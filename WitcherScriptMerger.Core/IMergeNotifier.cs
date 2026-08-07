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
		// defaultResult is the caller's own answer for "which result is safe/
		// non-destructive for this specific prompt" - added specifically for
		// LoadOrderValidator.PromptToPrioritizeMergedMod, whose YesNoCancel prompt
		// has an inverted-from-usual safety shape (Cancel is the one destructive,
		// permanent choice there, not Yes/No). NotifyResult.None means "no
		// preference, use whatever's generically safe/natural for this button set".
		// Both implementations honor it, not just the interactive one:
		//  - MainForm translates it to the real MessageBoxDefaultButton (which
		//    button is pre-focused), matching what a direct
		//    MessageBox.Show(..., MessageBoxDefaultButton) call could do before the
		//    Core split.
		//  - HeadlessMergeNotifier returns it directly instead of falling through to
		//    its own generic per-button-set guess, since only the caller actually
		//    knows which answer is safe for a prompt like this one.
		NotifyResult ShowMessage(string text,
			string title = "",
			NotifyButtons buttons = NotifyButtons.OK,
			DialogIcon icon = DialogIcon.None,
			NotifyResult defaultResult = NotifyResult.None);

		NotifyResult ShowError(string text, string title = "Error");
	}
}

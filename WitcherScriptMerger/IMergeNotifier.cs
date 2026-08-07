using System.Windows.Forms;

namespace WitcherScriptMerger
{
	interface IMergeNotifier
	{
		bool IsInteractive { get; }

		DialogResult ShowMessage(string text,
			string title = "",
			MessageBoxButtons buttons = MessageBoxButtons.OK,
			MessageBoxIcon icon = MessageBoxIcon.None,
			MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1);

		DialogResult ShowError(string text, string title = "Error");

		// Only ever called when IsInteractive is true.
		DialogResult ShowModal(Form form);
	}
}

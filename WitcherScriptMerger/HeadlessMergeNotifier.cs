using System;
using System.Windows.Forms;

namespace WitcherScriptMerger
{
    // Console-based IMergeNotifier for CLI mode. Never blocks on user input -
    // every decision has a fixed, non-destructive default (don't overwrite,
    // don't use a conflicting merge name, don't retry) so a batch run can
    // never hang waiting for a prompt nobody is watching.
    class HeadlessMergeNotifier : IMergeNotifier
    {
        public bool IsInteractive => false;

        public DialogResult ShowMessage(string text,
            string title = "",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.None)
        {
            Write(text, title, icon);

            return buttons switch
            {
                MessageBoxButtons.OK => DialogResult.OK,
                MessageBoxButtons.YesNo => DialogResult.No,
                MessageBoxButtons.YesNoCancel => DialogResult.Cancel,
                MessageBoxButtons.AbortRetryIgnore => DialogResult.Abort,
                MessageBoxButtons.RetryCancel => DialogResult.Cancel,
                MessageBoxButtons.OKCancel => DialogResult.Cancel,
                _ => DialogResult.Cancel,
            };
        }

        public DialogResult ShowError(string text, string title = "Error")
        {
            Write(text, title, MessageBoxIcon.Error);
            return DialogResult.OK;
        }

        public DialogResult ShowModal(Form form)
        {
            // Report dialogs (MergeReportForm/PackReportForm) are only ever
            // constructed behind an IsInteractive check in headless mode; this
            // is a defensive fallback in case a call site is missed. Never
            // call form.ShowDialog() - there's no message pump running.
            return DialogResult.OK;
        }

        static void Write(string text, string title, MessageBoxIcon icon)
        {
            var prefix = string.IsNullOrEmpty(title) ? "WSM" : title;
            var line = $"[{prefix}] {text}";

            if (icon == MessageBoxIcon.Error || icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Exclamation)
                Console.Error.WriteLine(line);
            else
                Console.WriteLine(line);
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;

namespace WitcherScriptMerger.Tools
{
	// "Open in the OS's default associated app" helper - Core-side (not the host
	// project's Program.TryOpenFile) specifically so the GUI's interactive path and the
	// CLI/MCP headless path can both reach it without Core referencing System.Windows.Forms.
	// DiffPlexMergeEngine.MergeHeadless is the only real call site: when a genuine
	// conflict writes a conflict-marker sidecar (see GetConflictMarkerPath), this is how
	// it gets opened for the user to review - both the interactive path (Merge() just
	// delegates to MergeHeadless()) and the headless CLI/MCP path funnel through that one
	// call, so there's only ever one "open the sidecar" mechanism, not two.
	//
	// Deliberately not a call to Program.TryOpenFile even though that already exists and
	// is used elsewhere (MergeReportForm's "Open Merged File" etc.): its non-.exe branch
	// is a bare `Process.Start(path)` with no UseShellExecute=true, and on modern .NET
	// (unlike .NET Framework, where UseShellExecute defaulted to true) that overload
	// defaults UseShellExecute to false - meaning it tries to launch the target directly
	// as a process image rather than through the shell's file association, throws
	// Win32Exception for a plain text file, and that exception is silently swallowed by
	// TryOpenFile's surrounding catch. That looks like a real pre-existing latent bug
	// (out of scope to fix broadly here - smaller blast radius on an already high-stakes
	// diff), but it means copying that exact pattern into this new code path would make
	// the "opens it now for review" feature silently never actually open anything. Using
	// UseShellExecute=true explicitly here avoids inheriting it.
	public static class FileOpener
	{
		// A field, not a method call, so tests can substitute a fake and verify the exact
		// path passed without a real process ever launching - the same
		// swappable-static-dependency pattern AppState.Notifier already uses for
		// testability elsewhere in this codebase. Defaults to the real implementation.
		public static Func<string, bool> Open = TryOpen;

		public static bool TryOpen(string path)
		{
			if (!File.Exists(path))
				return false;

			try
			{
				Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
				return true;
			}
			catch
			{
				// Best-effort, same as the rest of this codebase's non-critical cleanup/UX
				// helpers (e.g. DiffPlexMergeEngine.DeleteIfExists) - no file association,
				// a denied launch, etc. shouldn't turn "needs manual resolution" into a
				// harder failure than it already is. The conflict-marker sidecar itself is
				// still on disk either way; only the convenience of auto-opening it fails.
				return false;
			}
		}
	}
}

using System.Reflection;
using System.Runtime.CompilerServices;
using WitcherScriptMerger.LoadOrder;
using Xunit;

namespace WitcherScriptMerger.Tests.LoadOrder
{
	// Regression coverage for CustomLoadOrder.ProcessLine's handling of "VK=" lines. A
	// different fork of this project (github.com/IDCs/WitcherScriptMerger) found that
	// Vortex writes "VK=" (VortexKey) lines into mods.settings, which this parser had no
	// tolerance for - falling into the catch-all "unrecognized value" branch and aborting
	// the entire parse (IsValid stays false, no load order is usable) - see
	// CustomLoadOrder.cs's own comment on the "VK=" branch and Core's CLAUDE.md.
	//
	// ProcessLine is a private instance method invoked via reflection rather than exposed
	// publicly - unlike FileMerger.IsVanillaDlcBundleFolder (pure string/regex logic with
	// no other coupling), ProcessLine is inherently stateful across a multi-line parse
	// (accumulates a ModLoadSetting via `ref`) and CustomLoadOrder's constructor reads a
	// real, fixed path under the current user's Documents folder - reflection avoids
	// either widening ProcessLine's visibility or refactoring CustomLoadOrder's file-path
	// coupling just for this test. The instance itself is created via
	// RuntimeHelpers.GetUninitializedObject rather than `new CustomLoadOrder()`, skipping
	// the constructor (and its Refresh() call) entirely - ProcessLine touches no instance
	// state beyond the `ref` setting parameter, so it needs no initialized instance, and
	// skipping construction avoids depending on the test-running machine's real
	// mods.settings file, which - unlike the "file doesn't exist" case Refresh() no-ops
	// on safely - could be present and locked by a running game/Vortex process on a
	// developer machine with a live install, throwing IOException for a reason unrelated
	// to what this test actually covers.
	public class CustomLoadOrderTests
	{
		[Fact]
		public void ProcessLine_VortexKeyLine_IsRecognizedAndIgnored()
		{
			var loadOrder = (CustomLoadOrder)RuntimeHelpers.GetUninitializedObject(typeof(CustomLoadOrder));
			var processLine = typeof(CustomLoadOrder).GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Instance);

			ModLoadSetting setting = null;
			object[] args = { "VK=1a2b3c4d", 1, setting };
			var result = (bool)processLine.Invoke(loadOrder, args);

			// A malformed line returns false and aborts the whole parse (see Refresh()) -
			// true here confirms "VK=..." is treated as a recognized, ignorable line, not
			// as "unrecognized value" like it would have been before this fix.
			Assert.True(result);
		}

		[Fact]
		public void ProcessLine_TrulyUnrecognizedLine_StillFails()
		{
			// Confirms the VK= fix didn't accidentally widen ProcessLine to silently accept
			// everything - a genuinely malformed line must still fail the parse.
			var loadOrder = (CustomLoadOrder)RuntimeHelpers.GetUninitializedObject(typeof(CustomLoadOrder));
			var processLine = typeof(CustomLoadOrder).GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Instance);

			ModLoadSetting setting = null;
			object[] args = { "SomethingElse=1a2b3c4d", 1, setting };
			var result = (bool)processLine.Invoke(loadOrder, args);

			Assert.False(result);
		}
	}
}

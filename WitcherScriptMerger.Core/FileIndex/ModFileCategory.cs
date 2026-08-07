namespace WitcherScriptMerger.FileIndex
{
	public class ModFileCategory
	{
		public ModFileCategory(int orderIndex, string displayName, string toolTipText, bool isSupported, bool isBundled)
		{
			OrderIndex = orderIndex;
			DisplayName = displayName;
			ToolTipText = toolTipText;
			IsSupported = isSupported;
			IsBundled = isBundled;
		}

		public int OrderIndex { get; private set; }
		public string DisplayName { get; private set; }
		public string ToolTipText { get; private set; }
		public bool IsSupported { get; private set; }
		public bool IsBundled { get; private set; }

		public override string ToString()
		{
			return DisplayName;
		}
	}

	// readonly (not just static): callers throughout the codebase compare against
	// these by reference equality (e.g. `category == Categories.Script`), and these
	// fields are now `public` (required for cross-assembly access after the Core
	// split, where they were merely assembly-internal before) - readonly closes off
	// any accidental external reassignment silently breaking every such comparison.
	public static class Categories
	{
		public static readonly ModFileCategory Script = new ModFileCategory(
			1, "Scripts", "These plaintext .ws files can be merged", true, false);

		public static readonly ModFileCategory Xml = new ModFileCategory(
			2, "Non-Bundled XML", "These .xml text files can be merged", true, false);

		public static readonly ModFileCategory BundleText = new ModFileCategory(
			3, "Bundled Text", "These bundled text files can be merged", true, true);

		public static readonly ModFileCategory BundleNotMergeable = new ModFileCategory(
			4, "Bundled Non-text - Not Mergeable", "Right-click mods to define your load order instead of merging", false, true);

		public static readonly ModFileCategory FlatNotMergeable = new ModFileCategory(
			5, "Not Mergeable", "Script Merger doesn't know what these files are", false, false);
	}
}

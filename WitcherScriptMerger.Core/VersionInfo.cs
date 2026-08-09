using System.Reflection;

namespace WitcherScriptMerger
{
	// Backs both hosts' "--version" CLI flag and their MCP server's ServerInfo.Version
	// (see each host's own Program.cs) - shared here, not duplicated per host, even
	// though the two hosts' underlying assembly-attribute setups differ:
	// WitcherScriptMerger.csproj has GenerateAssemblyInfo=false and hand-maintains its
	// version in Properties/AssemblyInfo.cs (no AssemblyInformationalVersionAttribute is
	// ever emitted there), while WitcherScriptMerger.Headless.csproj drives it from its
	// own <Version> property (GenerateAssemblyInfo left on, so the SDK does emit one).
	// GetVersion's fallback chain handles both uniformly.
	public static class VersionInfo
	{
		public static string GetVersion(Assembly assembly)
		{
			var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			if (!string.IsNullOrEmpty(informational))
				return informational;

			var version = assembly.GetName().Version;
			if (version == null)
				return "unknown";

			// System.Version always round-trips through ToString() as 4 dot-separated
			// parts, padding an unset Revision to 0 - trim that back off when it's the
			// default, so a 3-part hand-maintained AssemblyVersion (e.g. "0.6.2", as
			// WitcherScriptMerger/Properties/AssemblyInfo.cs currently has it) prints
			// back out as "0.6.2", not "0.6.2.0".
			return version.Revision == 0
				? $"{version.Major}.{version.Minor}.{version.Build}"
				: version.ToString();
		}
	}
}

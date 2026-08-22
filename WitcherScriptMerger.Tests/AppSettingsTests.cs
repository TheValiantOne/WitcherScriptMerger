using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace WitcherScriptMerger.Tests
{
	// Coverage for AppSettings' WSM_<key> environment-variable override, which Get/Get<T>
	// now check ahead of App.config's <appSettings> block (see AppSettings.cs and Core's
	// CLAUDE.md's "Settings & persistence" section). This exists so a caller like a Vortex
	// extension can point WSM at a game/mods directory without hand-editing
	// WitcherScriptMerger.exe.config - unlike that approach, an env var has no lock/mutex
	// contention with a concurrently-running WSM process, since nothing is written to disk.
	//
	// None of these tests call `new AppSettings()` directly. AppSettings' constructor calls
	// Environment.Exit(1) if it can't find a config file next to
	// Assembly.GetEntryAssembly().Location, and under dotnet test's testhost.dll host (no
	// matching .config) that kills the entire test process, not just one test - see
	// WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety constraints".
	//
	// GetEnvironmentOverride is exercised directly (it's static and side-effect-free - no
	// CachedConfig/AppState touch at all). Get/Get<T> are exercised too, but only via
	// RuntimeHelpers.GetUninitializedObject, which skips the constructor entirely; this is
	// safe specifically because, per GetRawValue's short-circuit return, an env override
	// being present means CachedConfig (and therefore AppState.Notifier) is never touched -
	// confirmed by reading GetRawValue itself, not assumed. This also verifies Get<T>'s
	// Parse-based type conversion runs identically for an env-sourced value as it would for
	// one read from App.config, since GetRawValue is the one place both sources funnel
	// through before Get<T> ever sees the string.
	//
	// The env-var-wins-over-an-actual-config-file case (the other half of "precedence") and
	// the full CLI merge pipeline picking up WSM_GameDirectory/WSM_ModsDirectory are instead
	// verified end-to-end via a scratch game/mods tree with no App.config edits at all - see
	// this feature's PR description for that run, per WitcherScriptMerger.Tests/CLAUDE.md's
	// "AppState.Settings-safety constraints" guidance to prefer isolated logic tests here
	// over risking the test process.
	public class AppSettingsTests
	{
		[Fact]
		public void GetEnvironmentOverride_NotSet_ReturnsNull()
		{
			Assert.Null(AppSettings.GetEnvironmentOverride(UniqueKey()));
		}

		[Theory]
		[InlineData("GameDirectory")]
		[InlineData("ModsDirectory")]
		[InlineData("MergedModName")]
		[InlineData("QuickBmsPath")]
		[InlineData("QuickBmsPluginPath")]
		[InlineData("WccLitePath")]
		[InlineData("CheckBundleContents")]
		[InlineData("SomeHypotheticalFutureKey")]
		public void GetEnvironmentOverride_WorksForAnyKey_NoPerKeyCodeNeeded(string key)
		{
			// Covers every real <appSettings> key today plus one that appears nowhere in
			// App.config, confirming the prefix-and-lookup is genuinely generic string
			// concatenation rather than a hardcoded enumerated list somewhere.
			WithEnvironmentVariable(key, "override-for-" + key, () =>
			{
				Assert.Equal("override-for-" + key, AppSettings.GetEnvironmentOverride(key));
			});
		}

		[Fact]
		public void Get_WithEnvironmentOverride_ReturnsOverrideValue()
		{
			var key = UniqueKey();
			WithEnvironmentVariable(key, @"C:\Some\Overridden\Path", () =>
			{
				var settings = UninitializedAppSettings();

				Assert.Equal(@"C:\Some\Overridden\Path", settings.Get(key));
			});
		}

		[Theory]
		[InlineData("True", true)]
		[InlineData("False", false)]
		public void GetBool_WithEnvironmentOverride_ParsesThroughSameConversionPathAsConfig(string envValue, bool expected)
		{
			var key = UniqueKey();
			WithEnvironmentVariable(key, envValue, () =>
			{
				var settings = UninitializedAppSettings();

				Assert.Equal(expected, settings.Get<bool>(key));
			});
		}

		[Fact]
		public void GetInt_WithEnvironmentOverride_ParsesThroughSameConversionPathAsConfig()
		{
			var key = UniqueKey();
			WithEnvironmentVariable(key, "42", () =>
			{
				var settings = UninitializedAppSettings();

				Assert.Equal(42, settings.Get<int>(key));
			});
		}

		[Fact]
		public void GetInt_WithUnparsableEnvironmentOverride_ReturnsDefaultRatherThanThrowing()
		{
			// Mirrors the existing (unchanged) catch-all behavior for an unparsable
			// config-sourced value - an env-sourced value that fails Parse must fail the
			// same safe way, not bypass it.
			var key = UniqueKey();
			WithEnvironmentVariable(key, "not-a-number", () =>
			{
				var settings = UninitializedAppSettings();

				Assert.Equal(0, settings.Get<int>(key));
			});
		}

		// GetUninitializedObject skips AppSettings' constructor (and therefore its
		// Environment.Exit(1)-on-missing-config-file check) entirely. Only safe to call
		// Get/Get<T> on the result while an environment-variable override is in effect for
		// the key under test - GetRawValue returns the override before ever touching the
		// lazily-initialized CachedConfig property (and, transitively, AppState.Notifier).
		static AppSettings UninitializedAppSettings()
		{
			return (AppSettings)RuntimeHelpers.GetUninitializedObject(typeof(AppSettings));
		}

		static string UniqueKey() => "TestKey_" + Guid.NewGuid().ToString("N");

		static void WithEnvironmentVariable(string key, string value, Action action)
		{
			var envVarName = AppSettings.EnvironmentVariablePrefix + key;

			// Captures and restores whatever was already set, rather than unconditionally
			// clearing it in the finally block below - GetEnvironmentOverride_WorksForAnyKey_
			// NoPerKeyCodeNeeded deliberately parameterizes over real production key names
			// (GameDirectory, ModsDirectory, ...), so if the test process ever legitimately
			// inherited one of those (e.g. a CI job that also drives the CLI merge pipeline in
			// the same shell session - exactly this feature's own intended use), unconditional
			// clearing would silently wipe that override for the rest of the process instead
			// of restoring it.
			var originalValue = Environment.GetEnvironmentVariable(envVarName);
			Environment.SetEnvironmentVariable(envVarName, value);
			try
			{
				action();
			}
			finally
			{
				Environment.SetEnvironmentVariable(envVarName, originalValue);
			}
		}

		#region Vortex sidecar config (WitcherScriptMerger.exe.config)

		// Coverage for AppSettings.ParseAppSettingValue, the parser behind the
		// Vortex-managed sidecar GetRawValue falls back to when our own config leaves a key
		// blank - see AppSettings.cs's own comment on VortexSidecarFileName for why that
		// file exists (Vortex's bundled game-witcher3 extension reads MergedModName from it
		// and writes GameDirectory/VanillaScriptsDirectory/ModsDirectory into it, under the
		// .NET Framework "<exe>.exe.config" name a modern .NET app doesn't use).
		//
		// Exercised as a pure static over an XML string: no filesystem, no AppSettings
		// instance, no AppState - see WitcherScriptMerger.Tests/CLAUDE.md's
		// "AppState.Settings-safety constraints".
		const string SidecarXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""GameDirectory"" value=""G:\Games\Witcher3"" />
    <add key=""ModsDirectory"" value=""G:\Games\Witcher3\mods"" />
    <add key=""MergedModName"" value=""mod0000_MergedFiles"" />
    <add key=""BlankOne"" value="""" />
  </appSettings>
</configuration>";

		[Theory]
		[InlineData("GameDirectory", @"G:\Games\Witcher3")]
		[InlineData("ModsDirectory", @"G:\Games\Witcher3\mods")]
		[InlineData("MergedModName", "mod0000_MergedFiles")]
		public void ParseAppSettingValue_KeyPresent_ReturnsItsValue(string key, string expected)
		{
			Assert.Equal(expected, AppSettings.ParseAppSettingValue(SidecarXml, key));
		}

		// Null, never string.Empty, for anything unusable - GetRawValue's `?? value` fallback
		// relies on that to fall through to its own (blank) config value rather than
		// treating a blank sidecar entry as an answer.
		[Theory]
		[InlineData("NotInTheFile")]
		[InlineData("BlankOne")]
		public void ParseAppSettingValue_MissingOrBlankValue_ReturnsNull(string key)
		{
			Assert.Null(AppSettings.ParseAppSettingValue(SidecarXml, key));
		}

		// Matching is case-sensitive, matching ConfigurationManager's own <appSettings>
		// behavior - "gamedirectory" must not resolve "GameDirectory".
		[Fact]
		public void ParseAppSettingValue_KeyCaseDiffers_ReturnsNull()
		{
			Assert.Null(AppSettings.ParseAppSettingValue(SidecarXml, "gamedirectory"));
		}

		// A malformed/truncated sidecar (Vortex interrupted mid-write, say) must degrade to
		// "no answer" rather than throwing: this parser runs inside every settings read,
		// including on scan paths where an exception would surface as a merge failure.
		[Theory]
		[InlineData("<configuration><appSettings><add key=\"GameDirectory\" value=\"x\" />")]
		[InlineData("not xml at all")]
		[InlineData("<configuration />")]
		[InlineData("<configuration><appSettings /></configuration>")]
		public void ParseAppSettingValue_MalformedOrEmptyXml_ReturnsNullWithoutThrowing(string xml)
		{
			Assert.Null(AppSettings.ParseAppSettingValue(xml, "GameDirectory"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void ParseAppSettingValue_NoXml_ReturnsNull(string xml)
		{
			Assert.Null(AppSettings.ParseAppSettingValue(xml, "GameDirectory"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void ParseAppSettingValue_NoKey_ReturnsNull(string key)
		{
			Assert.Null(AppSettings.ParseAppSettingValue(SidecarXml, key));
		}

		// The file name is the interop contract with Vortex's hardcoded
		// scriptmerger.ts::MERGER_CONFIG_FILE - it is not ours to rename.
		[Fact]
		public void VortexSidecarFileName_MatchesTheNameVortexLooksFor()
		{
			Assert.Equal("WitcherScriptMerger.exe.config", AppSettings.VortexSidecarFileName);
		}

		#endregion
	}
}

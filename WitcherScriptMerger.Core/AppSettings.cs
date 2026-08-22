using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace WitcherScriptMerger
{
	public class AppSettings
	{
		string _assemblyPath;

		Configuration _cachedConfig;
		Configuration CachedConfig
		{
			get
			{
				if (_cachedConfig == null)
					_cachedConfig = ConfigurationManager.OpenExeConfiguration(_assemblyPath);
				return _cachedConfig;
			}
		}

		public bool HasConfigFile => CachedConfig.HasFile;

		public AppSettings()
		{
			_assemblyPath = Assembly.GetEntryAssembly().Location;

			if (!CachedConfig.HasFile)
			{
				AppState.Notifier.ShowError("Config file is missing.", "Script Merger Error");
				Environment.Exit(1);
			}
		}

		// Prefix applied to a setting's key to form the environment-variable name that
		// overrides it (e.g. the "GameDirectory" setting is overridden by "WSM_GameDirectory").
		// Generic by construction - covers every current <appSettings> key and any added
		// later with zero per-key code changes, since it's just string concatenation, not
		// an enumerated switch.
		public const string EnvironmentVariablePrefix = "WSM_";

		// Returns the environment-variable override for a settings key, or null if none is
		// set. Static and side-effect-free (no CachedConfig/AppState touch at all) so it's
		// safe to unit-test without constructing a live AppSettings instance - see
		// WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety constraints" for
		// why constructing one outside a real GUI/CLI/MCP entry point is unsafe (its
		// constructor calls Environment.Exit(1) if it can't find a config file, which kills
		// the whole dotnet test process rather than just failing one test).
		public static string GetEnvironmentOverride(string key)
		{
			return Environment.GetEnvironmentVariable(EnvironmentVariablePrefix + key);
		}

		// Resolves a key's raw string value: an environment-variable override first, then
		// the existing ConfigurationManager-backed lookup, then - only when that yields
		// nothing usable - the Vortex-managed sidecar (see VortexSidecarFileName). Both Get
		// and Get<T> route through this single place so a value from any source goes through
		// the exact same downstream handling (Get<T>'s Parse-based conversion in particular)
		// as one read from App.config - never a separate ad-hoc parser.
		string GetRawValue(string key)
		{
			var envValue = GetEnvironmentOverride(key);
			if (envValue != null)
				return envValue;

			if (CachedConfig.HasFile)
			{
				// Null-conditional, not a bare .Value: Settings[key] returns null for a key
				// that isn't in App.config at all, which used to throw here and get swallowed
				// by Get/Get<T>'s catch. Returning null instead is observably identical for
				// both of those (empty string / default(T)), and it lets a key that exists
				// ONLY in the sidecar still be found below rather than dying first.
				var value = CachedConfig.AppSettings.Settings[key]?.Value;
				if (!string.IsNullOrWhiteSpace(value))
					return value;

				// Blank in our own config. For the path settings that ship blank on purpose
				// (GameDirectory/ModsDirectory/VanillaScriptsDirectory - blank means "derive
				// from the working directory"), a Vortex-written sidecar value is strictly
				// better information than deriving, so prefer it when there is one.
				return ReadVortexSidecarSetting(key) ?? value;
			}

			AppState.Notifier.ShowError($"Config file doesn't exist:\n\n{CachedConfig.FilePath}");
			return null;
		}

		// Vortex's bundled game-witcher3 extension reads AND writes a script-merger config
		// at "<merger dir>\WitcherScriptMerger.exe.config" - the .NET Framework naming
		// convention this project used before the .NET 10 modernization. It parses that file
		// for MergedModName (scriptmerger.ts::getMergedModName) and writes GameDirectory,
		// VanillaScriptsDirectory and ModsDirectory into it (scriptmerger.ts::setMergerConfig)
		// when it configures a merger install. A modern .NET app's own configuration is
		// "<assembly>.dll.config" instead, so without this the two never meet: Vortex writes
		// a file WSM never reads, and the user "configures WSM through Vortex" with no
		// effect at all.
		//
		// Reading it as a *fallback* rather than an override is deliberate. A non-blank
		// value in our own config is an explicit choice (the GUI's own settings screen
		// writes there via Set/Save, and Vortex never writes MergedModName), so it must
		// win; the sidecar only fills in what we'd otherwise have to guess. Env overrides
		// still beat both, unchanged.
		public const string VortexSidecarFileName = "WitcherScriptMerger.exe.config";

		string _sidecarPath;
		bool _sidecarChecked;
		string _sidecarXml;

		string ReadVortexSidecarSetting(string key)
		{
			if (!_sidecarChecked)
			{
				_sidecarChecked = true;
				try
				{
					_sidecarPath = Path.Combine(Path.GetDirectoryName(_assemblyPath) ?? string.Empty, VortexSidecarFileName);
					if (File.Exists(_sidecarPath))
						_sidecarXml = File.ReadAllText(_sidecarPath);
				}
				catch
				{
					// Unreadable/inaccessible sidecar is not an error - it's an optional
					// interop file that usually isn't there at all. Never prompt, never
					// throw: this runs inside every settings read, including on scan paths.
					_sidecarXml = null;
				}
			}

			return _sidecarXml == null ? null : ParseAppSettingValue(_sidecarXml, key);
		}

		// Split out as a pure string-in/string-out function so the sidecar parsing is
		// directly unit-testable without a filesystem, a live AppSettings instance, or
		// AppState - see WitcherScriptMerger.Tests/CLAUDE.md's "AppState.Settings-safety
		// constraints". Returns null for anything it can't confidently read (malformed XML,
		// missing key, blank value), so every caller falls through to its existing
		// behavior rather than acting on a half-parsed file.
		public static string ParseAppSettingValue(string xml, string key)
		{
			if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(key))
				return null;

			try
			{
				var value = XDocument.Parse(xml)
					.Root?.Elements("appSettings")
					.Elements("add")
					.Where(e => string.Equals((string)e.Attribute("key"), key, StringComparison.Ordinal))
					.Select(e => (string)e.Attribute("value"))
					.FirstOrDefault();

				return string.IsNullOrWhiteSpace(value) ? null : value;
			}
			catch
			{
				return null;
			}
		}

		// Deliberately unaware of GetEnvironmentOverride: this still only ever writes to
		// CachedConfig/App.config, same as before the env-var override existed. If a
		// WSM_<key> override is active for a key this call targets, Get/Get<T> keep
		// returning the override afterward regardless of what's written and Save()d here
		// - a known, accepted asymmetry (an env var is meant to act as a caller-supplied
		// override of whatever App.config/the GUI would otherwise produce), not a bug to
		// paper over in this method.
		public void Set(string key, object value)
		{
			try
			{
				CachedConfig.AppSettings.Settings[key].Value = value.ToString();
			}
			catch
			{
				CachedConfig.AppSettings.Settings.Add(key, value.ToString());
			}
		}

		public T Get<T>(string key)
		{
			try
			{
				var valueString = GetRawValue(key);
				if (valueString == null)
					return default(T);

				var parseMethod = typeof(T).GetMethod("Parse", new Type[] { typeof(string) });
				var valueObject = parseMethod.Invoke(null, new object[] { valueString });
				return (T)valueObject;
			}
			catch
			{
				return default(T);
			}
		}

		public string Get(string key)
		{
			try
			{
				return GetRawValue(key) ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		public void Save()
		{
			try
			{
				CachedConfig.Save(ConfigurationSaveMode.Minimal);
			}
			catch (Exception ex)
			{
				AppState.Notifier.ShowError($"Failed to save config due to error:\n\n{ex.Message}");
			}
		}
	}
}

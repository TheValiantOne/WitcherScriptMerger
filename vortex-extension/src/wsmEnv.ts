/**
 * Builds the `WSM_<KeyName>` environment-variable overrides described in
 * `WitcherScriptMerger.Core/AppSettings.cs` (`AppSettings.EnvironmentVariablePrefix` /
 * `GetEnvironmentOverride`) - the *only* sanctioned way this extension configures a
 * spawned WSM process. Never read or write `WitcherScriptMerger.exe.config` /
 * `WitcherScriptMerger.Headless.dll.config` XML directly - see
 * `docs/vortex-extension-design.md` section 4.1 for why (a cached-`Configuration`,
 * explicit-`Save()`-only object on the .NET side makes hand-editing that file while a
 * WSM process is already running against it a real race).
 *
 * This module has no dependency on 'vortex-api' at all - it's plain data in, plain data
 * out - so it's reusable by every future caller that spawns a WSM process: the MCP path
 * (`mcpClient.ts`'s `WsmMcpClientOptions.env`, wired in `test/toolAcquisition.integration.test.ts`
 * as the proof this actually works end-to-end) and, per this unit's own instructions, the
 * as-yet-unbuilt one-shot `merge` CLI invocation path (a later "merge panel" unit) -
 * both should build their spawn `env` from this same function rather than duplicating
 * the `WSM_` prefix/key-name mapping independently.
 */

/** Mirrors `AppSettings.EnvironmentVariablePrefix` on the .NET side exactly. */
export const WSM_ENV_PREFIX = 'WSM_';

export interface WsmEnvConfig {
  gameDirectory?: string;
  modsDirectory?: string;
  mergedModName?: string;
  /**
   * Not consumed by anything in this unit (bundle-tooling acquisition hasn't landed
   * yet - see `storage.ts`'s `getBundleToolsDir` doc comment for the storage
   * convention a later unit should use to produce these three paths). Accepted here
   * now so that later unit only has to supply values, not invent the env-var mapping.
   */
  quickBmsPath?: string;
  quickBmsPluginPath?: string;
  wccLitePath?: string;
}

const CONFIG_KEY_TO_SETTING_NAME: Record<keyof WsmEnvConfig, string> = {
  gameDirectory: 'GameDirectory',
  modsDirectory: 'ModsDirectory',
  mergedModName: 'MergedModName',
  quickBmsPath: 'QuickBmsPath',
  quickBmsPluginPath: 'QuickBmsPluginPath',
  wccLitePath: 'WccLitePath',
};

/**
 * Builds the `WSM_<KeyName>` environment-variable map for the given config values.
 * Keys whose value is `undefined` are omitted entirely (not set to `""`) so a caller
 * can pass a partial config without accidentally overriding an unrelated setting with
 * an empty string - `AppSettings.GetEnvironmentOverride` treats "env var not set" and
 * "env var set to empty string" differently (the latter is itself a real, if unusual,
 * override value), so omission has to be a real absence of the key, not `""`.
 */
export function buildWsmEnv(config: WsmEnvConfig): Record<string, string> {
  const env: Record<string, string> = {};

  for (const key of Object.keys(CONFIG_KEY_TO_SETTING_NAME) as (keyof WsmEnvConfig)[]) {
    const value = config[key];
    if (value !== undefined) {
      env[`${WSM_ENV_PREFIX}${CONFIG_KEY_TO_SETTING_NAME[key]}`] = value;
    }
  }

  return env;
}

/**
 * Merges WSM_* overrides on top of the current process's own environment, for passing
 * directly as `child_process.spawn`'s `env` option (which, when set at all, replaces
 * the child's entire environment rather than augmenting it - so a caller that wants the
 * spawned WSM process to still see a normal PATH etc. needs to spread `process.env`
 * itself; this is that spread, done once, in one place).
 */
export function mergeWithProcessEnv(overrides: Record<string, string>): NodeJS.ProcessEnv {
  return { ...process.env, ...overrides };
}

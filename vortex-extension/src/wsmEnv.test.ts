import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { buildWsmEnv, mergeWithProcessEnv, WSM_ENV_PREFIX } from './wsmEnv';

describe('buildWsmEnv', () => {
  it('maps each provided config key to its WSM_<KeyName> environment variable', () => {
    const env = buildWsmEnv({
      gameDirectory: 'C:\\Games\\Witcher3',
      modsDirectory: 'C:\\Games\\Witcher3\\mods',
      mergedModName: 'mod0000_MergedFiles',
      quickBmsPath: 'C:\\Tools\\quickbms.exe',
      quickBmsPluginPath: 'C:\\Tools\\witcher3.bms',
      wccLitePath: 'C:\\Tools\\wcc_lite.exe',
    });

    expect(env).toEqual({
      WSM_GameDirectory: 'C:\\Games\\Witcher3',
      WSM_ModsDirectory: 'C:\\Games\\Witcher3\\mods',
      WSM_MergedModName: 'mod0000_MergedFiles',
      WSM_QuickBmsPath: 'C:\\Tools\\quickbms.exe',
      WSM_QuickBmsPluginPath: 'C:\\Tools\\witcher3.bms',
      WSM_WccLitePath: 'C:\\Tools\\wcc_lite.exe',
    });
  });

  it('uses the exact "WSM_" prefix AppSettings.cs expects', () => {
    expect(WSM_ENV_PREFIX).toBe('WSM_');
  });

  it('omits keys whose value is undefined rather than setting them to an empty string', () => {
    const env = buildWsmEnv({ modsDirectory: 'C:\\Mods' });

    expect(env).toEqual({ WSM_ModsDirectory: 'C:\\Mods' });
    expect('WSM_GameDirectory' in env).toBe(false);
    expect('WSM_MergedModName' in env).toBe(false);
  });

  it('does set a key when its value is deliberately the empty string', () => {
    const env = buildWsmEnv({ modsDirectory: '' });
    expect(env).toEqual({ WSM_ModsDirectory: '' });
  });

  it('returns an empty object for an empty config', () => {
    expect(buildWsmEnv({})).toEqual({});
  });
});

describe('mergeWithProcessEnv', () => {
  const ORIGINAL_ENV = { ...process.env };

  beforeEach(() => {
    process.env.WSM_TEST_PROBE = 'from-process-env';
    process.env.PATH_LIKE_PROBE = 'still-present';
  });

  afterEach(() => {
    process.env = { ...ORIGINAL_ENV };
  });

  it('keeps existing process.env entries the overrides do not mention', () => {
    const merged = mergeWithProcessEnv({ WSM_ModsDirectory: 'C:\\Mods' });
    expect(merged.PATH_LIKE_PROBE).toBe('still-present');
  });

  it('lets overrides win over an existing process.env value with the same key', () => {
    const merged = mergeWithProcessEnv({ WSM_TEST_PROBE: 'from-override' });
    expect(merged.WSM_TEST_PROBE).toBe('from-override');
  });
});

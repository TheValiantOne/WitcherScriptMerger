import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { notifyConflictsIfChanged, resetConflictNotificationState, WSM_CONFLICTS_NOTIFICATION_ID } from '../src/conflictNotifications';
import { WsmMcpClient } from '../src/mcpClient';

// Real, end-to-end integration test: spawns the actual, compiled WitcherScriptMerger
// Headless host's `mcp` verb against a scratch mods folder containing two real mods that
// both touch the same script file (a genuine conflict, per
// `FileIndex/ModFile.cs`'s `HasConflict => Mods.Count > 1`), runs a real `scan_conflicts`
// call the same way `conflictScan.ts`'s `scanWsmConflicts` does, and then feeds the real
// result into `notifyConflictsIfChanged` to confirm this unit's notification-trigger
// logic reacts to a real scan result correctly - the "scan round-trip" this unit's own
// instructions call for. Mirrors test/mcpClient.integration.test.ts's own scratch-config
// pattern exactly (same App.config shape, same build-if-missing beforeAll).

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '..', '..');
const HEADLESS_CSPROJ = path.join(
  REPO_ROOT,
  'WitcherScriptMerger.Headless',
  'WitcherScriptMerger.Headless.csproj',
);
const HEADLESS_BUILD_DIR = path.join(
  REPO_ROOT,
  'WitcherScriptMerger.Headless',
  'bin',
  'Debug',
  'net10.0',
);
const HEADLESS_EXE = path.join(HEADLESS_BUILD_DIR, 'WitcherScriptMerger.Headless.exe');

// See test/mcpClient.integration.test.ts's identical helper for why this is needed -
// os.tmpdir()/mkdtempSync embeds the current username on Windows, which can contain XML
// special characters.
function escapeXmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function buildScratchConfig(modsDirectory: string): string {
  return `<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <appSettings>
    <add key="GameDirectory" value="" />
    <add key="VanillaScriptsDirectory" value="" />
    <add key="ModsDirectory" value="${escapeXmlAttribute(modsDirectory)}" />
    <add key="CheckScripts" value="true" />
    <add key="CheckXmlFiles" value="true" />
    <add key="CheckBundleContents" value="false" />
    <add key="IgnoreModNames" value="" />
    <add key="MergedModName" value="mod0000_MergedFiles" />
    <add key="QuickBmsPath" value="Tools\\QuickBMS\\quickbms.exe" />
    <add key="QuickBmsPluginPath" value="Tools\\QuickBMS\\witcher3.bms" />
    <add key="WccLitePath" value="Tools\\wcc_lite\\bin\\x64\\wcc_lite.exe" />
  </appSettings>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.5" />
  </startup>
</configuration>
`;
}

let scratchDir: string;
let exePath: string;

beforeAll(() => {
  if (!fs.existsSync(HEADLESS_EXE)) {
    const result = spawnSync('dotnet', ['build', HEADLESS_CSPROJ, '-c', 'Debug'], {
      cwd: REPO_ROOT,
      stdio: 'inherit',
    });
    if (result.status !== 0) {
      throw new Error(
        `dotnet build of WitcherScriptMerger.Headless failed (required to run the conflictScan ` +
          `integration test) - exit code ${result.status}`,
      );
    }
  }

  if (!fs.existsSync(HEADLESS_EXE)) {
    throw new Error(`Expected built exe not found at ${HEADLESS_EXE} even after building.`);
  }

  scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-conflictscan-test-'));
  fs.cpSync(HEADLESS_BUILD_DIR, scratchDir, { recursive: true });

  const modsDir = path.join(scratchDir, 'Mods');
  // Two real mods both placing a same-named script under content\scripts - a genuine
  // conflict per ModFile.HasConflict (Mods.Count > 1), the same shape
  // ModFileIndex.BuildAsync scans for in a real Witcher 3 mods folder.
  const mod1ScriptDir = path.join(modsDir, 'mod0001_First', 'content', 'scripts');
  const mod2ScriptDir = path.join(modsDir, 'mod0002_Second', 'content', 'scripts');
  fs.mkdirSync(mod1ScriptDir, { recursive: true });
  fs.mkdirSync(mod2ScriptDir, { recursive: true });
  fs.writeFileSync(path.join(mod1ScriptDir, 'conflicting.ws'), 'function First() {}\n', 'utf8');
  fs.writeFileSync(path.join(mod2ScriptDir, 'conflicting.ws'), 'function Second() {}\n', 'utf8');

  fs.writeFileSync(
    path.join(scratchDir, 'WitcherScriptMerger.Headless.dll.config'),
    buildScratchConfig(modsDir),
    'utf8',
  );

  exePath = path.join(scratchDir, 'WitcherScriptMerger.Headless.exe');
}, 300_000);

afterAll(() => {
  if (scratchDir) {
    fs.rmSync(scratchDir, { recursive: true, force: true });
  }
});

describe('conflict scan round-trip (real WSM Headless process)', () => {
  it('scan_conflicts reports the real conflicting file, and notifyConflictsIfChanged reacts to it', async () => {
    const client = await WsmMcpClient.connect({ exePath });
    let conflicts;
    try {
      conflicts = await client.scanConflicts();
    } finally {
      await client.close();
    }

    expect(conflicts).toHaveLength(1);
    expect(conflicts[0].relativePath).toBe('conflicting.ws');
    expect(conflicts[0].alreadyResolved).toBe(false);

    resetConflictNotificationState();
    const sendNotification = createNotificationSpy();
    const dismissNotification = createDismissSpy();
    const fakeApi = {
      getState: () => ({ session: { base: { activity: {} } } }),
      sendNotification,
      dismissNotification,
    };

    notifyConflictsIfChanged(fakeApi as never, conflicts);

    expect(sendNotification.calls).toHaveLength(1);
    expect(sendNotification.calls[0].id).toBe(WSM_CONFLICTS_NOTIFICATION_ID);
    expect(sendNotification.calls[0].allowSuppress).toBe(true);

    // Calling again with the identical real scan result must not re-notify.
    notifyConflictsIfChanged(fakeApi as never, conflicts);
    expect(sendNotification.calls).toHaveLength(1);
  }, 30_000);

  it('scan_conflicts reports no conflicts against an empty mods folder', async () => {
    const emptyScratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-conflictscan-empty-test-'));
    try {
      fs.cpSync(HEADLESS_BUILD_DIR, emptyScratchDir, { recursive: true });
      const modsDir = path.join(emptyScratchDir, 'Mods');
      fs.mkdirSync(modsDir, { recursive: true });
      fs.writeFileSync(
        path.join(emptyScratchDir, 'WitcherScriptMerger.Headless.dll.config'),
        buildScratchConfig(modsDir),
        'utf8',
      );

      const client = await WsmMcpClient.connect({ exePath: path.join(emptyScratchDir, 'WitcherScriptMerger.Headless.exe') });
      let conflicts;
      try {
        conflicts = await client.scanConflicts();
      } finally {
        await client.close();
      }

      expect(conflicts).toEqual([]);

      resetConflictNotificationState();
      const sendNotification = createNotificationSpy();
      const fakeApi = {
        getState: () => ({ session: { base: { activity: {} } } }),
        sendNotification,
        dismissNotification: createDismissSpy(),
      };

      notifyConflictsIfChanged(fakeApi as never, conflicts);
      expect(sendNotification.calls).toHaveLength(0);
    } finally {
      fs.rmSync(emptyScratchDir, { recursive: true, force: true });
    }
  }, 30_000);
});

// Tiny hand-rolled spies (no vi.fn() here - this file intentionally exercises the real
// mcpClient/conflictNotifications modules with no mocking whatsoever) that record calls
// for assertion.
function createNotificationSpy() {
  const calls: Array<{ id?: string; type: string; message: string; allowSuppress?: boolean; actions?: unknown[] }> = [];
  const fn = (notification: { id?: string; type: string; message: string; allowSuppress?: boolean; actions?: unknown[] }) => {
    calls.push(notification);
    return notification.id ?? 'generated-id';
  };
  return Object.assign(fn, { calls });
}

function createDismissSpy() {
  const calls: string[] = [];
  const fn = (id: string) => {
    calls.push(id);
  };
  return Object.assign(fn, { calls });
}

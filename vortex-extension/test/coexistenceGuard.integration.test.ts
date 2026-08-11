import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { checkCoexistenceDrift, computeMergeStateSnapshot, resetCoexistenceGuardState, WSM_COEXISTENCE_NOTIFICATION_ID } from '../src/coexistenceGuard';
import { WsmMcpClient } from '../src/mcpClient';

// Real, end-to-end integration test for coexistenceGuard.ts (Unit K): spawns the actual,
// compiled WitcherScriptMerger Headless host's `mcp` verb and drives a real
// get_status -> list_merges -> (recursive fs listing of the real merged-mod folder)
// round trip via computeMergeStateSnapshot, both before and after a genuine
// `merge_conflicts` call - the same "spawn a real process, prove the higher-level logic
// reacts correctly to its real output" pattern conflictScan.integration.test.ts already
// establishes for scanConflicts()/notifyConflictsIfChanged, applied here to
// computeMergeStateSnapshot()/checkCoexistenceDrift().
//
// Deliberately uses only an auto-solving conflict (two mods editing the same .ws file on
// disjoint lines), not a genuinely-conflicting one - unlike
// test/mcpClient.integration.test.ts's own real-merge describe block, which stages a
// second, genuinely-conflicting .xml file specifically to prove the skipped/sidecar path.
// This test only needs the merged-mod folder and MergeInventory.xml to end up
// non-trivially populated by a real merge; a genuine conflict would additionally trigger
// FileOpener.Open on the written conflict-marker sidecar (a real, documented side effect -
// see that other file's own comment), which this test has no reason to also incur.

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

// Same escaping rationale as every other integration test's own identically-named helper
// (e.g. mcpClient.integration.test.ts) - kept local since it's test-fixture plumbing.
function escapeXmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function buildScratchConfig(gameDirectory: string, modsDirectory: string): string {
  return `<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <appSettings>
    <add key="GameDirectory" value="${escapeXmlAttribute(gameDirectory)}" />
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

const VANILLA_WS_CONTENT =
  'function FuncA() {\r\n' +
  '    var a : int;\r\n' +
  '    a = 1;\r\n' +
  '}\r\n' +
  '\r\n' +
  'function FuncB() {\r\n' +
  '    var b : int;\r\n' +
  '    b = 1;\r\n' +
  '}\r\n';
const MOD1_WS_CONTENT = VANILLA_WS_CONTENT.replace('a = 1;', 'a = 100;');
const MOD2_WS_CONTENT = VANILLA_WS_CONTENT.replace('b = 1;', 'b = 200;');

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
        `dotnet build of WitcherScriptMerger.Headless failed (required to run the coexistenceGuard ` +
          `integration test) - exit code ${result.status}`,
      );
    }
  }

  if (!fs.existsSync(HEADLESS_EXE)) {
    throw new Error(`Expected built exe not found at ${HEADLESS_EXE} even after building.`);
  }

  scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-coexistence-test-'));
  fs.cpSync(HEADLESS_BUILD_DIR, scratchDir, { recursive: true });

  const gameDir = path.join(scratchDir, 'Game');
  const modsDir = path.join(scratchDir, 'Mods');
  const vanillaScriptsDir = path.join(gameDir, 'content', 'content0', 'scripts', 'game');
  const mod1ScriptDir = path.join(modsDir, 'mod0001_First', 'content', 'scripts', 'game');
  const mod2ScriptDir = path.join(modsDir, 'mod0002_Second', 'content', 'scripts', 'game');

  for (const dir of [vanillaScriptsDir, mod1ScriptDir, mod2ScriptDir]) {
    fs.mkdirSync(dir, { recursive: true });
  }

  fs.writeFileSync(path.join(vanillaScriptsDir, 'itemA.ws'), VANILLA_WS_CONTENT, 'utf8');
  fs.writeFileSync(path.join(mod1ScriptDir, 'itemA.ws'), MOD1_WS_CONTENT, 'utf8');
  fs.writeFileSync(path.join(mod2ScriptDir, 'itemA.ws'), MOD2_WS_CONTENT, 'utf8');

  fs.writeFileSync(
    path.join(scratchDir, 'WitcherScriptMerger.Headless.dll.config'),
    buildScratchConfig(gameDir, modsDir),
    'utf8',
  );

  exePath = path.join(scratchDir, 'WitcherScriptMerger.Headless.exe');
}, 300_000);

afterAll(() => {
  if (scratchDir) {
    fs.rmSync(scratchDir, { recursive: true, force: true });
  }
});

describe('coexistenceGuard integration (real WSM Headless process, real merge round trip)', () => {
  it('computeMergeStateSnapshot reflects the real merged-mod folder + MergeInventory.xml before and after a real merge, and checkCoexistenceDrift reacts to the difference', async () => {
    resetCoexistenceGuardState();

    const client = await WsmMcpClient.connect({ exePath });
    try {
      // Before any merge: get_status reports a real modsDirectory/mergedModName (plain
      // config reads - WsmMcpTools.GetStatus, confirmed not gated on modsDirectoryExists)
      // even though nothing has been merged yet, and list_merges is empty. Both signals
      // this module compares should therefore be empty strings.
      const before = await computeMergeStateSnapshot(client);
      expect(before.mergedModName).toBe('mod0000_MergedFiles');
      expect(before.folderListingSignature).toBe('');
      expect(before.mergeHistorySignature).toBe('');

      const mergeResult = await client.mergeConflicts({ dryRun: false });
      expect(mergeResult.merged).toEqual([path.join('game', 'itemA.ws')]);

      // After a real, non-dry-run merge, using the same still-open client (the same
      // amortization resolveAction.ts's own runMergeConflictsWorkflow relies on).
      const after = await computeMergeStateSnapshot(client);
      expect(after.folderListingSignature).not.toBe('');
      expect(after.mergeHistorySignature).not.toBe('');
      expect(after.folderListingSignature).not.toBe(before.folderListingSignature);
      expect(after.mergeHistorySignature).not.toBe(before.mergeHistorySignature);

      // Ties the real round trip above to this module's own detect/warn logic, the same
      // way conflictScan.integration.test.ts feeds a real scanConflicts() result into
      // notifyConflictsIfChanged.
      const sendNotification = createNotificationSpy();
      const fakeApi = { getState: () => ({}), sendNotification, showDialog: async () => ({ action: 'Close', input: {} }) };

      checkCoexistenceDrift(fakeApi as never, before);
      expect(sendNotification.calls).toHaveLength(0); // first observation - only seeds the baseline

      checkCoexistenceDrift(fakeApi as never, after);
      expect(sendNotification.calls).toHaveLength(1);
      expect(sendNotification.calls[0].id).toBe(WSM_COEXISTENCE_NOTIFICATION_ID);
      expect(sendNotification.calls[0].type).toBe('warning');

      // Re-checking the same (already-warned-about) state must not re-notify.
      checkCoexistenceDrift(fakeApi as never, after);
      expect(sendNotification.calls).toHaveLength(1);
    } finally {
      await client.close();
    }
  }, 30_000);
});

// Tiny hand-rolled spy (no vi.fn() - this file intentionally exercises the real
// mcpClient/coexistenceGuard modules with no mocking whatsoever), mirroring
// conflictScan.integration.test.ts's own createNotificationSpy.
function createNotificationSpy() {
  const calls: Array<{ id?: string; type: string; message: string; actions?: unknown[] }> = [];
  const fn = (notification: { id?: string; type: string; message: string; actions?: unknown[] }) => {
    calls.push(notification);
    return notification.id ?? 'generated-id';
  };
  return Object.assign(fn, { calls });
}

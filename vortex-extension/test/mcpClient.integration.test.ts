import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { WsmMcpClient } from '../src/mcpClient';

// Real, end-to-end integration test: spawns the actual, compiled WitcherScriptMerger
// Headless host's `mcp` verb and drives it through a full initialize -> tools/list ->
// tools/call(get_status) round trip. No mocks - this is what actually proves the
// hand-rolled newline-delimited JSON-RPC framing in src/mcpClient.ts works against a
// real MCP stdio server, per this unit's own verification requirement.

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

// Backslashes are not special in XML and need no escaping; the characters that *do*
// need escaping inside an XML attribute value are these five. This matters because
// modsDirectory is built from os.tmpdir()/mkdtempSync, which on Windows embeds the
// current username - a username containing '&', '<', '>', or '"' (unusual, but not
// disallowed by Windows) would otherwise produce malformed XML that ConfigurationManager
// fails to parse with an opaque error, rather than a clear path-related one.
function escapeXmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function buildScratchConfig(modsDirectory: string): string {
  // Mirrors WitcherScriptMerger.Headless/App.config's <appSettings> shape (read directly
  // from that file) - only the values that matter for this test are filled in; everything
  // else is left at the same default the real App.config ships with.
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
        `dotnet build of WitcherScriptMerger.Headless failed (required to run the mcpClient ` +
          `integration test) - exit code ${result.status}`,
      );
    }
  }

  if (!fs.existsSync(HEADLESS_EXE)) {
    throw new Error(`Expected built exe not found at ${HEADLESS_EXE} even after building.`);
  }

  // Copy the whole build output to an isolated scratch dir rather than mutating the
  // shared bin/Debug output's own App.config in place.
  scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-mcp-test-'));
  fs.cpSync(HEADLESS_BUILD_DIR, scratchDir, { recursive: true });

  const modsDir = path.join(scratchDir, 'Mods');
  fs.mkdirSync(modsDir, { recursive: true });

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

describe('WsmMcpClient integration (real WSM Headless process)', () => {
  it('completes initialize -> tools/list -> get_status against the real MCP server', async () => {
    const client = await WsmMcpClient.connect({ exePath });
    try {
      const tools = await client.listTools();
      expect(tools.map((t) => t.name).sort()).toEqual(
        ['get_status', 'list_merges', 'merge_conflicts', 'scan_conflicts'].sort(),
      );

      const status = await client.getStatus();
      expect(status.textMergeDependenciesValid).toBe(true);
      expect(status.modsDirectoryExists).toBe(true);
      expect(status.conflictCount).toBe(0);
      expect(status.mergedModName).toBe('mod0000_MergedFiles');
      expect(typeof status.modsDirectory).toBe('string');
    } finally {
      await client.close();
    }
  }, 30_000);

  it('completes initialize -> tools/list -> scan_conflicts and list_merges against an empty mods folder', async () => {
    const client = await WsmMcpClient.connect({ exePath });
    try {
      const conflicts = await client.scanConflicts();
      expect(conflicts).toEqual([]);

      const merges = await client.listMerges();
      expect(merges).toEqual([]);
    } finally {
      await client.close();
    }
  }, 30_000);
});

import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { fetchMergeHistory } from '../src/mergeHistoryDashlet';
import { getWsmToolDir } from '../src/storage';

// Real, end-to-end integration test for fetchMergeHistory (src/mergeHistoryDashlet.ts):
// spawns the actual, compiled WitcherScriptMerger Headless host's `mcp` verb and drives
// it through a full connect -> list_merges -> close round trip, the same way
// mcpClient.integration.test.ts proves the lower-level WsmMcpClient itself. That existing
// test only ever asserts list_merges returns `[]` against an empty mods folder - not
// enough to prove this file's own mapping of a *populated* MergeInventory.xml into a
// MergeHistoryResult, since MergeInventory.Load's bare `catch { inventory = new
// MergeInventory(); }` (WitcherScriptMerger.Core/Inventory/MergeInventory.cs) means a
// malformed fixture would silently also produce `[]` - indistinguishable from "no merges"
// unless a test actually asserts non-empty, field-matched output. This test writes a
// scratch MergeInventory.xml by hand (schema confirmed directly against
// WitcherScriptMerger.Core/Inventory/MergeInventory.cs, Merge.cs, ModFile.cs, FileHash.cs
// - not guessed) specifically to close that gap.

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

// Same escaping rationale as mcpClient.integration.test.ts's own helper of the same name
// - kept local rather than shared/exported since it's test-fixture plumbing, not
// extension code.
function escapeXmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function escapeXmlText(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function buildScratchConfig(modsDirectory: string): string {
  // Mirrors WitcherScriptMerger.Headless/App.config's <appSettings> shape, same as
  // mcpClient.integration.test.ts's own buildScratchConfig.
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

// Schema confirmed directly against WitcherScriptMerger.Core/Inventory/MergeInventory.cs
// (root element defaults to the class name, "MergeInventory"; [XmlElement("Merge")] names
// each item), Merge.cs ([XmlElement] MergedModName), ModFile.cs ([XmlElement] RelativePath,
// [XmlElement("IncludedMod")] Mods), and FileHash.cs ([XmlAttribute] Hash, [XmlText] Name -
// so each mod is `<IncludedMod Hash="...">ModName</IncludedMod>`, name as element text, not
// an attribute or child element). Real, non-null Hash values are supplied deliberately -
// MergeInventory.Load's AddMissingHashes back-fills (and can Save()) any null Hash by
// recomputing it from a real mod file on disk, which this fixture has none of.
function buildScratchInventoryXml(relativePath: string, mergedModName: string, mods: Array<{ name: string; hash: string }>): string {
  const modElements = mods
    .map((mod) => `    <IncludedMod Hash="${escapeXmlAttribute(mod.hash)}">${escapeXmlText(mod.name)}</IncludedMod>`)
    .join('\n');
  return `<?xml version="1.0" encoding="utf-8"?>
<MergeInventory>
  <Merge>
    <RelativePath>${escapeXmlText(relativePath)}</RelativePath>
    <MergedModName>${escapeXmlText(mergedModName)}</MergedModName>
${modElements}
  </Merge>
</MergeInventory>
`;
}

let userDataDir: string;
let toolDir: string;
let fakeApi: Parameters<typeof fetchMergeHistory>[0];

const RELATIVE_PATH = 'content\\scripts\\game\\r4Game.ws';
const MERGED_MOD_NAME = 'mod0000_MergedFiles';
const MOD_ALPHA = { name: 'modAlpha', hash: '1a2b3c4d' };
const MOD_BETA = { name: 'modBeta', hash: '5e6f7089' };

beforeAll(() => {
  if (!fs.existsSync(HEADLESS_EXE)) {
    const result = spawnSync('dotnet', ['build', HEADLESS_CSPROJ, '-c', 'Debug'], {
      cwd: REPO_ROOT,
      stdio: 'inherit',
    });
    if (result.status !== 0) {
      throw new Error(
        `dotnet build of WitcherScriptMerger.Headless failed (required to run the mergeHistory ` +
          `integration test) - exit code ${result.status}`,
      );
    }
  }

  if (!fs.existsSync(HEADLESS_EXE)) {
    throw new Error(`Expected built exe not found at ${HEADLESS_EXE} even after building.`);
  }

  userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-history-integration-'));
  fakeApi = {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
  } as unknown as Parameters<typeof fetchMergeHistory>[0];

  // Lay out a scratch "already acquired" install at the exact location
  // resolveWsmExePath (src/mergeHistoryDashlet.ts) / getWsmToolDir (src/storage.ts)
  // expect, mirroring toolAcquisition.integration.test.ts's own approach.
  toolDir = getWsmToolDir(fakeApi);
  fs.mkdirSync(toolDir, { recursive: true });
  fs.cpSync(HEADLESS_BUILD_DIR, toolDir, { recursive: true });

  const modsDir = path.join(toolDir, 'Mods');
  fs.mkdirSync(modsDir, { recursive: true });
  fs.writeFileSync(
    path.join(toolDir, 'WitcherScriptMerger.Headless.dll.config'),
    buildScratchConfig(modsDir),
    'utf8',
  );

  // Paths.Inventory ("MergeInventory.xml") is a relative path resolved against
  // Environment.CurrentDirectory, which the Headless host pins to AppContext.BaseDirectory
  // (the exe's own directory) before dispatching to `mcp` mode (see
  // WitcherScriptMerger.Core/Mcp/CLAUDE.md) - so this has to sit next to the exe, i.e.
  // directly inside toolDir, not inside modsDir.
  fs.writeFileSync(
    path.join(toolDir, 'MergeInventory.xml'),
    buildScratchInventoryXml(RELATIVE_PATH, MERGED_MOD_NAME, [MOD_ALPHA, MOD_BETA]),
    'utf8',
  );
}, 300_000);

afterAll(() => {
  if (userDataDir) {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
});

describe('fetchMergeHistory integration (real WSM Headless process, populated MergeInventory.xml)', () => {
  it('returns the recorded merge, with matching relative path, merged-mod name, and per-mod hashes', async () => {
    const result = await fetchMergeHistory(fakeApi);

    expect(result.status).toBe('loaded');
    if (result.status !== 'loaded') {
      return;
    }

    expect(result.merges).toHaveLength(1);
    const [merge] = result.merges;
    expect(merge.relativePath).toBe(RELATIVE_PATH);
    expect(merge.mergedModName).toBe(MERGED_MOD_NAME);
    expect(merge.mods).toEqual([MOD_ALPHA, MOD_BETA]);
  }, 30_000);
});

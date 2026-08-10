import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';
import { WITCHER3_GAME_ID } from '../src/gating';
import { WSM_TOOL_ID } from '../src/discoveredTool';
import { DEFAULT_WSM_REPO } from '../src/githubRelease';
import { getWsmToolDir, INSTALLED_VERSION_FILENAME } from '../src/storage';
import { ensureWsmToolRegistered, WSM_HEADLESS_EXE_NAME } from '../src/toolAcquisition';
import { buildWsmEnv, mergeWithProcessEnv } from '../src/wsmEnv';
import { WsmMcpClient } from '../src/mcpClient';

// Real, end-to-end integration test for this unit's acquisition -> registration ->
// env-var-config pipeline. **Does not exercise the actual GitHub-Releases download
// path** (src/githubRelease.ts) - no version tag has been pushed to this repo, so no
// GitHub Release exists yet (see githubRelease.ts's own doc comment and this unit's PR
// description). Instead, this test stands a *locally-built* WSM binary in for "the
// downloaded-and-extracted one": it publishes WitcherScriptMerger.Headless with the
// exact same profile release.yml itself uses (`-p:PublishProfile=win-x64` - self-
// contained, single-file), then lays it out on disk exactly the way `acquireWsmTool`
// would have (see src/toolAcquisition.ts's `getWsmToolDir` layout), so everything
// *downstream* of the download - local-only registration (`ensureWsmToolRegistered`)
// and, critically, the WSM_* env-var configuration mechanism actually reaching a real,
// spawned WSM process - is proven for real, not mocked.
//
// The core proof (see the second `it` below): the scratch `.dll.config` this test
// writes deliberately sets ModsDirectory/MergedModName to *wrong* placeholder values
// nothing else in this test uses, and the env vars this unit's own `buildWsmEnv` builds
// are asserted to win over them - not just "some value came back", but specifically
// *not* the XML's value. That distinguishes "the mechanism works" from "some value
// happened to end up populated by coincidence".

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '..', '..');
const HEADLESS_CSPROJ = path.join(
  REPO_ROOT,
  'WitcherScriptMerger.Headless',
  'WitcherScriptMerger.Headless.csproj',
);
// Matches release.yml's own `build` job matrix entry for
// "WitcherScriptMerger.Headless (win-x64)" exactly - see that job's `publish-dir`.
const PUBLISH_DIR = path.join(
  REPO_ROOT,
  'WitcherScriptMerger.Headless',
  'bin',
  'Release',
  'net10.0',
  'win-x64',
  'publish',
);
const PUBLISHED_EXE = path.join(PUBLISH_DIR, WSM_HEADLESS_EXE_NAME);

// Deliberately wrong values, distinct from anything this test's own assertions use for
// the "real" (env-var) side - see this file's own top comment.
const WRONG_XML_MODS_DIRECTORY = 'C:\\this-is-the-WRONG-xml-value\\mods';
const WRONG_XML_MERGED_MOD_NAME = 'WRONG_XML_MergedModName';

// Same escaping rationale as mcpClient.integration.test.ts's own helper of the same
// name - kept local rather than shared/exported since it's test-fixture plumbing, not
// extension code (this test writes a scratch .dll.config as *test setup*, standing in
// for what a real `acquireWsmTool` extraction would have produced; the extension's own
// production code, per this unit's own instructions, never reads or writes this file).
function escapeXmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function buildScratchConfig(modsDirectory: string, mergedModName: string): string {
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
    <add key="MergedModName" value="${escapeXmlAttribute(mergedModName)}" />
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

let userDataDir: string;
let scratchModsDir: string;
let exePath: string;
let dispatch: ReturnType<typeof vi.fn>;
let fakeApi: Parameters<typeof ensureWsmToolRegistered>[0];

beforeAll(() => {
  if (!fs.existsSync(PUBLISHED_EXE)) {
    // Exact same invocation as .github/workflows/release.yml's "Publish" step for the
    // "WitcherScriptMerger.Headless (win-x64)" matrix entry (minus that workflow's
    // -p:Version=, irrelevant here) - see WitcherScriptMerger.Headless/CLAUDE.md's
    // "Publishing" section.
    const result = spawnSync('dotnet', ['publish', HEADLESS_CSPROJ, '-c', 'Release', '-p:PublishProfile=win-x64'], {
      cwd: REPO_ROOT,
      stdio: 'inherit',
    });
    if (result.status !== 0) {
      throw new Error(
        `dotnet publish (win-x64 profile) of WitcherScriptMerger.Headless failed (required to run this ` +
          `integration test) - exit code ${result.status}`,
      );
    }
  }

  if (!fs.existsSync(PUBLISHED_EXE)) {
    throw new Error(`Expected published exe not found at ${PUBLISHED_EXE} even after publishing.`);
  }

  userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-tool-acquisition-test-'));
  dispatch = vi.fn();
  fakeApi = {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
    getState: () => ({}),
    store: { dispatch },
  } as unknown as Parameters<typeof ensureWsmToolRegistered>[0];

  // Lay out this test's scratch "already acquired" install exactly the way
  // acquireWsmTool (src/toolAcquisition.ts) would have after downloading and
  // extracting a real release asset - same directory (getWsmToolDir), same files
  // (the whole publish output, including the un-doctored WitcherScriptMerger.Core.pdb/
  // WitcherScriptMerger.Headless.pdb alongside the exe, matching a real zip's
  // contents), same INSTALLED_VERSION_FILENAME marker.
  const toolDir = getWsmToolDir(fakeApi);
  fs.mkdirSync(toolDir, { recursive: true });
  fs.cpSync(PUBLISH_DIR, toolDir, { recursive: true });
  fs.writeFileSync(path.join(toolDir, INSTALLED_VERSION_FILENAME), `${DEFAULT_WSM_REPO}@0.6.2`, 'utf8');

  // Overwrite the copied .dll.config with deliberately wrong placeholder values (see
  // this file's own top comment) - test setup only, never done by extension code.
  fs.writeFileSync(
    path.join(toolDir, 'WitcherScriptMerger.Headless.dll.config'),
    buildScratchConfig(WRONG_XML_MODS_DIRECTORY, WRONG_XML_MERGED_MOD_NAME),
    'utf8',
  );

  exePath = path.join(toolDir, WSM_HEADLESS_EXE_NAME);

  scratchModsDir = path.join(userDataDir, 'IntegrationTestMods');
  fs.mkdirSync(scratchModsDir, { recursive: true });
}, 300_000);

afterAll(() => {
  if (userDataDir) {
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
});

describe('tool acquisition end-to-end (locally-built binary standing in for a downloaded one)', () => {
  it('ensureWsmToolRegistered finds the locally-installed binary and registers it, with no network activity', async () => {
    const registered = await ensureWsmToolRegistered(fakeApi);

    expect(registered).toBe(true);
    expect(dispatch).toHaveBeenCalledTimes(1);

    const action = dispatch.mock.calls[0][0] as {
      payload: { gameId: string; toolId: string; manual: boolean; result: { path: string; id: string } };
    };
    expect(action.payload.gameId).toBe(WITCHER3_GAME_ID);
    expect(action.payload.toolId).toBe(WSM_TOOL_ID);
    expect(action.payload.manual).toBe(true);
    expect(action.payload.result.path).toBe(exePath);
    expect(action.payload.result.id).toBe(WSM_TOOL_ID);
  });

  it('WSM_* env vars built by buildWsmEnv override the XML config in a real spawned WSM MCP process', async () => {
    const env = mergeWithProcessEnv(
      buildWsmEnv({ modsDirectory: scratchModsDir, mergedModName: 'IntegrationTestMergedMod' }),
    );

    const client = await WsmMcpClient.connect({ exePath, env });
    try {
      const status = await client.getStatus();

      // The real proof: not the XML's values...
      expect(status.modsDirectory).not.toBe(WRONG_XML_MODS_DIRECTORY);
      expect(status.mergedModName).not.toBe(WRONG_XML_MERGED_MOD_NAME);
      // ...but exactly what this unit's own env-var builder supplied.
      expect(status.modsDirectory).toBe(scratchModsDir);
      expect(status.mergedModName).toBe('IntegrationTestMergedMod');

      expect(status.modsDirectoryExists).toBe(true);
      expect(status.textMergeDependenciesValid).toBe(true);
      expect(status.conflictCount).toBe(0);
    } finally {
      await client.close();
    }
  }, 30_000);
});

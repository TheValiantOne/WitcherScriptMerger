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

// Real merge round trip: this unit (Unit H, the "Resolve Script Conflicts" action +
// merge panel) drives `mergeConflicts({dryRun: true})` then, on confirm,
// `mergeConflicts({dryRun: false})` - src/mergePanel.ts and src/resolveAction.ts's own
// unit tests exercise the panel/orchestration logic against a fake client, but this is
// the proof that a *real* dry-run/real-run round trip against a real WSM process
// returns sensible merged/skipped/functionLevelDecisions data for that logic to
// consume. Two conflicts are staged: a .ws script two mods edit on disjoint lines
// (auto-solves cleanly, whole-file 3-way merge, no conflict blocks at all) and an .xml
// file two mods edit on the very same line to different values (a genuine,
// non-whitespace conflict). The second is deliberately .xml, not .ws:
// DiffPlexMergeEngine.TryFunctionLevelRescue only ever attempts the function-level
// fallback for a ".ws" outputPath, so an .xml conflict can never be silently rescued
// out of `skipped` by that fallback - keeping this fixture's "stays genuinely skipped"
// outcome deterministic without having to out-think that engine's own tiebreak logic.
//
// Reuses the already-built HEADLESS_EXE from the top-level beforeAll above (this file
// is loaded once by vitest; that beforeAll always runs before any test in this file,
// including this describe block's own) rather than triggering a second `dotnet build`
// - avoids racing that build if vitest ever schedules this file's describes
// concurrently. Only the build *output* is reused (via a plain file copy into a second,
// independently-configured scratch install); nothing here shares mutable state with the
// describe block above.
//
// Known, expected side effect of the second `it` below: a real (non-dry) merge_conflicts
// call against a genuine conflict makes the real WSM process call
// Tools/FileOpener.Open on the conflict-marker sidecar it writes (DiffPlexMergeEngine.
// MergeHeadless, openConflictMarkers defaults true for dryRun: false) - i.e. it may
// briefly launch a program or the OS's "how do you want to open this file?" picker for
// the written `.conflict` sidecar. This is real, intentional WSM behavior (see
// WitcherScriptMerger.Core/Mcp/CLAUDE.md's "Minimal required permissions" section), not
// a test bug - Process.Start returns immediately either way, so it doesn't block or fail
// this test even if left uninteracted with.
describe('WsmMcpClient integration - real merge round trip (auto-solve + genuine conflict)', () => {
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

  const VANILLA_XML_CONTENT = '<items>\r\n  <item id="sword_of_destiny" value="100" />\r\n</items>\r\n';
  const MOD1_XML_CONTENT = VANILLA_XML_CONTENT.replace('value="100"', 'value="500"');
  const MOD2_XML_CONTENT = VANILLA_XML_CONTENT.replace('value="100"', 'value="999"');

  const MERGED_SCRIPT_RELATIVE_PATH = path.join('game', 'itemA.ws');
  const CONFLICTING_XML_RELATIVE_PATH = path.join('gameplay', 'items.xml');

  function buildMergeScratchConfig(gameDirectory: string, modsDirectory: string): string {
    // Same shape as buildScratchConfig above, but with a real GameDirectory (needed so
    // ScriptsDirectory/GetVanillaFile resolve to real vanilla content this fixture
    // writes - see this describe block's own top comment) rather than the empty one
    // that outer function hardcodes.
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

  let mergeScratchDir: string;
  let mergeExePath: string;

  beforeAll(() => {
    mergeScratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-mcp-merge-test-'));
    fs.cpSync(HEADLESS_BUILD_DIR, mergeScratchDir, { recursive: true });

    const gameDir = path.join(mergeScratchDir, 'Game');
    const modsDir = path.join(mergeScratchDir, 'Mods');
    const vanillaScriptsDir = path.join(gameDir, 'content', 'content0', 'scripts', 'game');
    const vanillaXmlDir = path.join(gameDir, 'gameplay');
    const mod1ScriptDir = path.join(modsDir, 'mod0001_First', 'content', 'scripts', 'game');
    const mod2ScriptDir = path.join(modsDir, 'mod0002_Second', 'content', 'scripts', 'game');
    const mod1XmlDir = path.join(modsDir, 'mod0001_First', 'gameplay');
    const mod2XmlDir = path.join(modsDir, 'mod0002_Second', 'gameplay');

    for (const dir of [vanillaScriptsDir, vanillaXmlDir, mod1ScriptDir, mod2ScriptDir, mod1XmlDir, mod2XmlDir]) {
      fs.mkdirSync(dir, { recursive: true });
    }

    fs.writeFileSync(path.join(vanillaScriptsDir, 'itemA.ws'), VANILLA_WS_CONTENT, 'utf8');
    fs.writeFileSync(path.join(mod1ScriptDir, 'itemA.ws'), MOD1_WS_CONTENT, 'utf8');
    fs.writeFileSync(path.join(mod2ScriptDir, 'itemA.ws'), MOD2_WS_CONTENT, 'utf8');

    fs.writeFileSync(path.join(vanillaXmlDir, 'items.xml'), VANILLA_XML_CONTENT, 'utf8');
    fs.writeFileSync(path.join(mod1XmlDir, 'items.xml'), MOD1_XML_CONTENT, 'utf8');
    fs.writeFileSync(path.join(mod2XmlDir, 'items.xml'), MOD2_XML_CONTENT, 'utf8');

    fs.writeFileSync(
      path.join(mergeScratchDir, 'WitcherScriptMerger.Headless.dll.config'),
      buildMergeScratchConfig(gameDir, modsDir),
      'utf8',
    );

    mergeExePath = path.join(mergeScratchDir, 'WitcherScriptMerger.Headless.exe');
  }, 60_000);

  afterAll(() => {
    if (!mergeScratchDir) {
      return;
    }
    try {
      fs.rmSync(mergeScratchDir, { recursive: true, force: true });
    } catch {
      // Best-effort, not a test failure: this describe block's own real-run test
      // deliberately exercises FileOpener.Open on the genuine conflict's sidecar (see
      // that test's own comment) - whatever program the OS launched for the
      // `.conflict` file may still be holding a lock on it (or its containing
      // directory) by the time this runs, making the whole scratch tree
      // undeletable-for-now on Windows (EPERM). That's a real, expected consequence of
      // testing this genuine behavior end-to-end, not a bug in this test - the leftover
      // temp directory needs the same manual housekeeping
      // Paths.DiffPlexConflictsDirectory's own doc comment already describes for the
      // real DiffPlexConflicts folder (nothing sweeps it automatically either).
    }
  });

  it('a dry run previews the auto-solving file as merged and the genuinely conflicting file as skipped, without writing anything', async () => {
    const client = await WsmMcpClient.connect({ exePath: mergeExePath });
    try {
      const preview = await client.mergeConflicts({ dryRun: true });

      expect(preview.dryRun).toBe(true);
      expect(preview.merged).toEqual([MERGED_SCRIPT_RELATIVE_PATH]);
      expect(preview.skipped).toEqual([CONFLICTING_XML_RELATIVE_PATH]);
      expect(preview.unmatched).toEqual([]);
      expect(Array.isArray(preview.functionLevelDecisions)).toBe(true);

      const mergedScriptPath = path.join(
        mergeScratchDir,
        'Mods',
        'mod0000_MergedFiles',
        'content',
        'scripts',
        'game',
        'itemA.ws',
      );
      expect(fs.existsSync(mergedScriptPath)).toBe(false);
    } finally {
      await client.close();
    }
  }, 30_000);

  it('a real run merges the auto-solving file with both mods\' changes and leaves a conflict-marker sidecar for the genuinely conflicting one', async () => {
    const client = await WsmMcpClient.connect({ exePath: mergeExePath });
    try {
      const result = await client.mergeConflicts({ dryRun: false });

      expect(result.dryRun).toBe(false);
      expect(result.merged).toEqual([MERGED_SCRIPT_RELATIVE_PATH]);
      expect(result.skipped).toEqual([CONFLICTING_XML_RELATIVE_PATH]);
      expect(result.unmatched).toEqual([]);

      const mergedScriptPath = path.join(
        mergeScratchDir,
        'Mods',
        'mod0000_MergedFiles',
        'content',
        'scripts',
        'game',
        'itemA.ws',
      );
      expect(fs.existsSync(mergedScriptPath)).toBe(true);
      const mergedText = fs.readFileSync(mergedScriptPath, 'utf16le');
      expect(mergedText).toContain('a = 100;');
      expect(mergedText).toContain('b = 200;');

      // See DiffPlexMergeEngine.GetConflictMarkerPath's own comment for why the
      // sidecar lands in a dedicated DiffPlexConflicts folder next to the exe, keyed
      // by an XxHash32 of the file's own would-be output path, rather than at that
      // output path itself.
      const conflictsDir = path.join(mergeScratchDir, 'DiffPlexConflicts');
      expect(fs.existsSync(conflictsDir)).toBe(true);
      const sidecars = fs.readdirSync(conflictsDir);
      expect(sidecars.some((name) => name.startsWith('items.xml.'))).toBe(true);
    } finally {
      await client.close();
    }
  }, 30_000);

  // Depends on the previous test having really merged itemA.ws - vitest runs tests in
  // a file in declaration order, and this whole describe block already relies on that
  // (the dry-run test asserts the merged file does NOT exist yet).
  it('re-merging skips an already-merged file without overwrite, and refreshes it with overwrite: true', async () => {
    const client = await WsmMcpClient.connect({ exePath: mergeExePath });
    try {
      const mergedScriptPath = path.join(
        mergeScratchDir,
        'Mods',
        'mod0000_MergedFiles',
        'content',
        'scripts',
        'game',
        'itemA.ws',
      );
      expect(fs.existsSync(mergedScriptPath)).toBe(true);
      const beforeMtime = fs.statSync(mergedScriptPath).mtimeMs;

      // Without overwrite: the already-merged file is a reported skip (the server
      // never silently rebuilds an existing merge), and a dry run predicts the same.
      const skippedPreview = await client.mergeConflicts({ dryRun: true });
      expect(skippedPreview.merged).toEqual([]);
      expect(skippedPreview.skipped).toContain(MERGED_SCRIPT_RELATIVE_PATH);

      const skippedRun = await client.mergeConflicts({ dryRun: false });
      expect(skippedRun.merged).toEqual([]);
      expect(skippedRun.skipped).toContain(MERGED_SCRIPT_RELATIVE_PATH);
      expect(fs.statSync(mergedScriptPath).mtimeMs).toBe(beforeMtime);

      // With overwrite: the dry run can now answer "would this auto-solve?" for the
      // already-merged file, and the real run actually refreshes it.
      const overwritePreview = await client.mergeConflicts({ dryRun: true, overwrite: true });
      expect(overwritePreview.merged).toEqual([MERGED_SCRIPT_RELATIVE_PATH]);
      expect(fs.statSync(mergedScriptPath).mtimeMs).toBe(beforeMtime);

      const overwriteRun = await client.mergeConflicts({ dryRun: false, overwrite: true });
      expect(overwriteRun.merged).toEqual([MERGED_SCRIPT_RELATIVE_PATH]);
      const refreshedText = fs.readFileSync(mergedScriptPath, 'utf16le');
      expect(refreshedText).toContain('a = 100;');
      expect(refreshedText).toContain('b = 200;');
    } finally {
      await client.close();
    }
  }, 30_000);
});

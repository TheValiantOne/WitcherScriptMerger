#!/usr/bin/env node
// Stages this extension's build output into a distributable zip for manual
// installation into %APPDATA%\Vortex\plugins\<folder>\ - see README.md's "Install"
// section for the full manual-install flow this replaces the copy-by-hand steps for.
//
// Scope note (deliberately minimal): this commits no binaries and asserts no
// distribution model. It only stages `dist/` (webpack's own output - just
// `index.js`/`index.js.map`, see webpack.config.cjs) plus `info.json` (the Vortex
// extension manifest) into a local, gitignored `release/` folder and zips that folder.
// It does not upload, publish, or register anywhere - see
// docs/vortex-extension-design.md, section 6, Open Question 3 (public Nexus-registry
// listing), which remains open and is not resolved by this script existing.
//
// Requires `npm run build` to have already produced `dist/index.js` - this script
// fails fast with a clear message rather than silently packaging a stale/missing dist.
//
// Zipping is platform-conditional rather than an added npm dependency: PowerShell's
// `Compress-Archive` on Windows, or the `zip` CLI on macOS/Linux (present on both by
// default) for anyone building this extension outside Windows even though Vortex
// itself only runs on Windows today (docs/vortex-extension-design.md, Open Question 8)
// - the built extension is only useful there, but nothing about producing the zip
// itself requires it. Both branches `cd` into the staged folder and zip its *contents*
// (not the folder itself), matching this repo's own `.github/workflows/release.yml`
// (its `package-release` job's own `zip -r` step, `( cd publish/... && zip -r
// ../../dist/out.zip . )`) - so the resulting archive extracts flat, with `index.js`/
// `info.json` at the zip root, not nested one level down under a `<name>/` folder.
// (That release.yml step runs on plain Ubuntu via bash's `zip`, not PowerShell - it's
// the *archive layout*, not the tool, this mirrors.)

import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const DIST_DIR = path.join(ROOT, 'dist');
const INFO_JSON = path.join(ROOT, 'info.json');
const RELEASE_DIR = path.join(ROOT, 'release');

const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
// The staged folder's own name - not read from info.json (which declares no explicit
// "id" field; @nexusmods/vortex-api's own IExtension typing marks `id` optional), so
// there's no single canonical extension id to derive this from. package.json's own
// `name` is used instead, matching this repo's git history/npm package identity.
const stageName = pkg.name;
const stageDir = path.join(RELEASE_DIR, stageName);

if (!fs.existsSync(path.join(DIST_DIR, 'index.js'))) {
  console.error("dist/index.js not found - run 'npm run build' first (or use 'npm run package', which does this for you).");
  process.exit(1);
}
if (!fs.existsSync(INFO_JSON)) {
  console.error(`info.json not found at '${INFO_JSON}' - this is this extension's own Vortex manifest and should always be present.`);
  process.exit(1);
}

// package.json and info.json carry the version independently, and nothing used to
// reconcile them: the produced zip is named from package.json's version while the
// manifest Vortex actually reads is info.json's, so a one-sided bump ships an archive
// whose filename disagrees with the version Vortex reports. Fail the package step
// rather than emit that.
const info = JSON.parse(fs.readFileSync(INFO_JSON, 'utf8'));
if (info.version !== pkg.version) {
  console.error(
    `Version mismatch: package.json says '${pkg.version}' but info.json says '${info.version}'. ` +
    `The zip is named from package.json while Vortex reads info.json, so these must agree - ` +
    `update both before packaging.`,
  );
  process.exit(1);
}

// The id is what Vortex uses as the extension's stable identity. Without it, identity
// and the installed folder name derive from the archive filename and can change between
// releases, which makes an update look like a different extension.
if (!info.id) {
  console.error(`info.json is missing an 'id' - Vortex needs a stable extension id that doesn't change between releases.`);
  process.exit(1);
}

fs.rmSync(RELEASE_DIR, { recursive: true, force: true });
fs.mkdirSync(stageDir, { recursive: true });

// cpSync (not a manual readdir+copyFileSync loop) so this doesn't break if
// webpack.config.cjs's output ever grows a subdirectory (e.g. code-splitting) -
// today's output is flat (just index.js/index.js.map), but recursive copy costs
// nothing and removes that assumption.
fs.cpSync(DIST_DIR, stageDir, { recursive: true });
fs.copyFileSync(INFO_JSON, path.join(stageDir, 'info.json'));

const zipPath = path.join(RELEASE_DIR, `${stageName}-${pkg.version}.zip`);

// Escapes a path for embedding inside a PowerShell single-quoted string: doubling an
// embedded `'` is PowerShell's own escape for that context (not a backslash escape,
// which single-quoted PowerShell strings don't interpret at all) - without this, a
// repo checked out under a path containing an apostrophe (e.g. a Windows user profile
// like `C:\Users\O'Brien\...`) would prematurely terminate the quoted string and fail
// with a PowerShell parse error rather than produce a zip.
function escapePowerShellSingleQuoted(value) {
  return value.replace(/'/g, "''");
}

try {
  if (process.platform === 'win32') {
    // -DestinationPath refers to a location one level up from stageDir (RELEASE_DIR),
    // so this doesn't need stageDir to exist as a Compress-Archive *source* root
    // itself - '\*' selects stageDir's contents, producing a flat archive (index.js/
    // info.json at the zip root), matching the posix branch below.
    execFileSync(
      'powershell',
      [
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        `Compress-Archive -Path '${escapePowerShellSingleQuoted(stageDir)}\\*' -DestinationPath '${escapePowerShellSingleQuoted(zipPath)}' -Force`,
      ],
      { stdio: 'inherit' },
    );
  } else {
    // cwd: stageDir (not RELEASE_DIR) + zipping '.' is what makes this flat, mirroring
    // release.yml's own `cd <staged-dir> && zip -r ../../dist/out.zip .` pattern -
    // zipping `stageName` from RELEASE_DIR instead (an earlier version of this script
    // did exactly that) nests every entry under a `<stageName>/` prefix, contradicting
    // this script's own "lands directly in ..., not a nested subfolder" install
    // instructions below.
    execFileSync('zip', ['-r', zipPath, '.'], { cwd: stageDir, stdio: 'inherit' });
  }
} catch (err) {
  console.error(
    `\nFailed to create the zip (${err instanceof Error ? err.message : String(err)}). This script's zip step ` +
      `needs '${process.platform === 'win32' ? 'powershell' : 'zip'}' on PATH. As a fallback, you can skip ` +
      `zipping entirely and copy the staged folder's contents directly instead:\n  ${stageDir}`,
  );
  process.exit(1);
}

console.log(`\nPackaged: ${zipPath}`);
console.log(`Staged (unzipped) folder: ${stageDir}`);
console.log(
  `\nManual install: extract the zip (or copy the staged folder's contents) so they land directly in\n` +
    `  <Vortex userData>\\plugins\\${stageName}\\\n` +
    `where <Vortex userData> is %APPDATA%\\Vortex for a default per-user install, or\n` +
    `C:\\ProgramData\\vortex when Vortex is set up with shared/multi-user storage.\n` +
    `i.e. that folder should directly contain index.js and info.json, not a nested subfolder.`,
);

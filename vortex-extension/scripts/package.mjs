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
// `Compress-Archive` on Windows (matching this repo's own `release.yml`, which is also
// pwsh-driven for its Windows-built assets), or the `zip` CLI on macOS/Linux (present
// on both by default) for anyone building this extension outside Windows even though
// Vortex itself only runs on Windows today (docs/vortex-extension-design.md, Open
// Question 8) - the built extension is only useful there, but nothing about producing
// the zip itself requires it.

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

fs.rmSync(RELEASE_DIR, { recursive: true, force: true });
fs.mkdirSync(stageDir, { recursive: true });

for (const file of fs.readdirSync(DIST_DIR)) {
  fs.copyFileSync(path.join(DIST_DIR, file), path.join(stageDir, file));
}
fs.copyFileSync(INFO_JSON, path.join(stageDir, 'info.json'));

const zipPath = path.join(RELEASE_DIR, `${stageName}-${pkg.version}.zip`);

if (process.platform === 'win32') {
  execFileSync(
    'powershell',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `Compress-Archive -Path '${stageDir}\\*' -DestinationPath '${zipPath}' -Force`,
    ],
    { stdio: 'inherit' },
  );
} else {
  try {
    execFileSync('zip', ['-r', path.basename(zipPath), stageName], { cwd: RELEASE_DIR, stdio: 'inherit' });
  } catch (err) {
    console.error(
      `\nCould not run 'zip' (${err instanceof Error ? err.message : String(err)}). This script's posix zip path ` +
        "hasn't been exercised beyond a missing-binary check - install a 'zip' CLI, or skip the zip step and copy " +
        `the staged folder directly instead:\n  ${stageDir}`,
    );
    process.exit(1);
  }
}

console.log(`\nPackaged: ${zipPath}`);
console.log(`Staged (unzipped) folder: ${stageDir}`);
console.log(
  `\nManual install: extract the zip (or copy the staged folder above) so its contents land directly in\n` +
    `  %APPDATA%\\Vortex\\plugins\\${stageName}\\\n` +
    `i.e. that folder should directly contain index.js and info.json, not a nested subfolder.`,
);

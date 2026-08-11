import * as fs from 'fs';
import * as path from 'path';
import { types } from 'vortex-api';
import { getExtensionStorageDir, getWsmToolDir, WSM_HEADLESS_EXE_NAME } from './storage';

/**
 * The single place the "which WSM executable should this extension use?" question is
 * answered. Two sources, in precedence order:
 *
 *  1. **User override** - an absolute exe path the user pointed at an existing WSM
 *     install (the status dashlet's "Use an existing install..." flow), persisted as a
 *     one-line text file in this extension's private storage
 *     (`tool-path-override.txt`, same trivially-parseable single-value convention as
 *     `storage.ts`'s `installed-version.txt`). Deliberately a file rather than Vortex
 *     Redux state: the discovered-tools state's persistence story for this extension's
 *     own registrations is already unverified (see `discoveredTool.ts`), and a plain
 *     file is exactly as reliable as the acquisition markers this extension already
 *     trusts.
 *  2. **Managed install** - the extension-private tool dir `acquireWsmTool` downloads
 *     into (`storage.ts`'s layout).
 *
 * An override that's set but whose file no longer exists is reported as its own
 * distinct state (`override-missing`), never silently skipped in favor of the managed
 * install: the user explicitly pointed elsewhere, and quietly using a different binary
 * than the one they chose is the kind of surprise this extension exists to avoid. The
 * fix is theirs to make (repair the path, or clear the override).
 *
 * Before this module, the managed-path computation
 * (`getWsmToolDir(api) + WSM_HEADLESS_EXE_NAME`) was duplicated across
 * `conflictScan.ts`, `mergeHistoryDashlet.ts`, `wsmStatusSummary.ts`, and
 * `toolAcquisition.ts` - a documented, deliberate duplication awaiting "a later unit
 * touching either file" (mergeHistoryDashlet.ts's own former comment). This is that
 * unit.
 */

export const WSM_TOOL_PATH_OVERRIDE_FILENAME = 'tool-path-override.txt';

export type WsmExeResolution =
  | { kind: 'override'; exePath: string }
  | { kind: 'managed'; exePath: string }
  | { kind: 'override-missing'; overridePath: string }
  | { kind: 'none' };

function overrideFilePath(api: types.IExtensionApi): string {
  return path.join(getExtensionStorageDir(api), WSM_TOOL_PATH_OVERRIDE_FILENAME);
}

function isEnoent(err: unknown): boolean {
  return typeof err === 'object' && err !== null && (err as NodeJS.ErrnoException).code === 'ENOENT';
}

/** The persisted override exe path, or undefined when none is set. Only ENOENT (and a
 *  blank file) mean "not set" - any other read failure is a real problem the caller
 *  needs to see, same policy as `toolAcquisition.ts`'s `pathExists`. */
export async function getWsmToolPathOverride(api: types.IExtensionApi): Promise<string | undefined> {
  let content: string;
  try {
    content = await fs.promises.readFile(overrideFilePath(api), 'utf8');
  } catch (err) {
    if (isEnoent(err)) {
      return undefined;
    }
    throw err;
  }
  const trimmed = content.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

/** Persists (or, with null, clears) the override exe path. Callers validate the path
 *  BEFORE setting it (exists, is a file, names a WSM executable) - this function only
 *  persists, so the validation logic lives next to the UI that can explain a rejection
 *  to the user. */
export async function setWsmToolPathOverride(api: types.IExtensionApi, exePath: string | null): Promise<void> {
  const filePath = overrideFilePath(api);
  if (exePath === null) {
    try {
      await fs.promises.unlink(filePath);
    } catch (err) {
      if (!isEnoent(err)) {
        throw err;
      }
    }
    return;
  }
  await fs.promises.mkdir(path.dirname(filePath), { recursive: true });
  await fs.promises.writeFile(filePath, exePath, 'utf8');
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    await fs.promises.access(filePath);
    return true;
  } catch (err) {
    if (isEnoent(err)) {
      return false;
    }
    throw err;
  }
}

/** Resolves which WSM executable to use - see this module's own doc comment for the
 *  precedence and the deliberate no-silent-fallback policy on a stale override. */
export async function resolveWsmExe(api: types.IExtensionApi): Promise<WsmExeResolution> {
  const override = await getWsmToolPathOverride(api);
  if (override !== undefined) {
    return (await fileExists(override))
      ? { kind: 'override', exePath: override }
      : { kind: 'override-missing', overridePath: override };
  }

  const managedPath = path.join(getWsmToolDir(api), WSM_HEADLESS_EXE_NAME);
  return (await fileExists(managedPath))
    ? { kind: 'managed', exePath: managedPath }
    : { kind: 'none' };
}

/** Convenience for callers that only need "a usable exe path, or nothing" - the
 *  override-missing state maps to undefined here (unusable), with the richer
 *  distinction left to `resolveWsmExe` callers that can surface it (the status
 *  dashlet). */
export async function resolveWsmExePathIfUsable(api: types.IExtensionApi): Promise<string | undefined> {
  const resolution = await resolveWsmExe(api);
  return resolution.kind === 'override' || resolution.kind === 'managed' ? resolution.exePath : undefined;
}

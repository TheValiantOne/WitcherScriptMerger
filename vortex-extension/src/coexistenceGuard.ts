import * as fs from 'fs';
import * as path from 'path';
import { log, selectors, types } from 'vortex-api';
import { resetConflictNotificationState, WSM_CONFLICTS_NOTIFICATION_ID } from './conflictNotifications';
import { getWsmExePath, isWsmToolAcquired } from './conflictScan';
import { WITCHER3_GAME_ID } from './gating';
import { GetStatusResult, ListMergesResult, WsmMcpClient } from './mcpClient';
import { buildWsmEnv, mergeWithProcessEnv } from './wsmEnv';

/**
 * Unit K: coexistence & Collections handling.
 *
 * `docs/vortex-extension-design.md` section 0 and Open Question 1 name two concrete
 * correctness hazards from Vortex's own built-in `game-witcher3` extension, which this
 * extension coexists with rather than replaces (see `gating.ts`'s own doc comment):
 * Collections import (`importScriptMerges()`) overwriting merge output on confirmation,
 * and per-profile merge backup/restore (`mergeBackup.ts`'s `storeToProfile`/
 * `restoreFromProfile`, wired from `eventHandlers.ts`'s `onProfileWillChange` off the
 * `profile-will-change` event). **There is no `vortex-api` mechanism to intercept,
 * disable, or block another extension's own registrations or event handlers** (re-
 * confirmed against the published `lib/api.d.ts` - no such method exists on
 * `IExtensionContext`/`IExtensionApi`), so this module cannot prevent either hazard. Its
 * job is **detect, warn, reconcile** - notice that WSM's own merge state changed without
 * going through this extension's own workflow, tell the user distinctly from an ordinary
 * "you have conflicts" notification, and point them at the existing remediation surfaces
 * (the merge-history dashlet, `mergeHistoryDashlet.ts`; the "Resolve Script Conflicts"
 * action, `resolveAction.ts`) rather than trying to auto-fix anything itself.
 *
 * **Two mechanistic corrections to the design doc's own framing, found by fetching and
 * reading the real `Nexus-Mods/Vortex` monorepo source directly (`gh api
 * repos/Nexus-Mods/Vortex/contents/extensions/games/game-witcher3/src/...`), not carried
 * over from the design doc's own summary** - both change how "detect" has to work here,
 * so they're recorded with their exact citations rather than only in this unit's PR
 * description:
 *
 * 1. **`storeToProfile`/`restoreFromProfile` operate on a *different* `MergeInventory.xml`
 *    than this extension's own, by default.** `mergeBackup.ts`'s `handleMergedScripts`
 *    resolves the file it moves (`MERGE_INV_MANIFEST`, `"MergeInventory.xml"` -
 *    `common.ts`) against `path.dirname(scriptMergerTool.path)`, where `scriptMergerTool`
 *    is the discovery entry for `SCRIPT_MERGER_ID = "W3ScriptMerger"` (`common.ts`) -
 *    `game-witcher3`'s *own* discovered tool, a separately-acquired binary (typically the
 *    `IDCs/WitcherScriptMerger` fork it auto-downloads - design doc section 0). This
 *    extension registers a **distinct** tool id, `WSM_TOOL_ID = 'WitcherScriptMergerEnhanced'`
 *    (`discoveredTool.ts`), at its own acquired path (`storage.ts`'s `getWsmToolDir`).
 *    Per WSM's own `Paths.Inventory` (`WitcherScriptMerger.Core/Paths.cs`), resolved
 *    against `Environment.CurrentDirectory` - which both hosts pin to
 *    `AppContext.BaseDirectory` before dispatching to `merge`/`mcp`
 *    (`WitcherScriptMerger.Core/Mcp/CLAUDE.md`) - each binary's `MergeInventory.xml`
 *    lives next to *that* binary, not in some shared location. So in the default,
 *    two-separate-binaries configuration, `storeToProfile`/`restoreFromProfile` (and
 *    `importScriptMerges`, which calls the same `handleMergedScripts`) never touch this
 *    extension's own `MergeInventory.xml` file directly. What genuinely *is* shared,
 *    confirmed from the same source: `handleMergedScripts`'s `mergedScriptsPath =
 *    path.join(gamePath, "Mods", mergedModName)` - the real, physical merged-mod-content
 *    folder inside the actual game `Mods` directory Vortex manages, which every WSM
 *    instance (whichever binary produced it) reads/writes via the identical
 *    `GameDirectory`/`ModsDirectory` resolution (design doc section 4.1). That shared
 *    folder, not `MergeInventory.xml` itself, is the resource both hazards actually
 *    contend over - which is why this module snapshots the folder's own contents
 *    (`buildFolderListingSignature` below), not only `MergeInventory.xml`-derived state,
 *    despite `list_merges`/`MergeInventory.xml` being the only *schema* knowledge this
 *    unit is meant to lean on (see `computeMergeHistorySignature`'s own doc comment for
 *    why that half still matters: it's the one signal that *does* catch the case where a
 *    user has pointed `game-witcher3`'s own `W3ScriptMerger` discovery at the exact same
 *    binary this extension acquired).
 * 2. **The per-profile backup/restore hazard is opt-in per profile, not automatic.**
 *    `mergeBackup.ts`'s `genBaseProps` returns `undefined` (a no-op) unless
 *    `state.persistent.profiles[profileId].features.local_merges` is `true` - and
 *    `game-witcher3`'s own `index.ts` registers that feature
 *    (`context.registerProfileFeature("local_merges", "boolean", "settings", "Profile
 *    Data", "This profile will store and restore profile specific data (merged scripts,
 *    loadorder, etc) when switching profiles", ...)`) as an ordinary Vortex profile
 *    toggle, **defaulting to unset/false**. So hazard 2 does not fire "on every profile
 *    switch" as a blanket statement - only for a profile the user has explicitly opted
 *    into that feature. This doesn't change what this module does (it still can't tell
 *    *why* merge state changed, only *that* it did), but it does mean hazard 2 is rarer
 *    in practice than the design doc's own phrasing suggests.
 *
 * **Detection mechanism.** A snapshot-and-compare approach, per this unit's own task
 * description, combining two independent signals so a change is caught regardless of
 * which of the two directory configurations above applies:
 *
 * - `mergeHistorySignature`: an order-independent signature over `list_merges()`'s
 *   result (`RecordedMerge[]`) - reuses `mcpClient.ts`'s existing `WsmMcpClient.listMerges`
 *   (the same MCP tool `mergeHistoryDashlet.ts`'s `fetchMergeHistory` already calls, per
 *   this unit's own instructions to build on that path rather than re-parsing
 *   `MergeInventory.xml` independently), mirroring `conflictNotifications.ts`'s
 *   `computeConflictSignature` shape (sorted, delimited, newline-joined - the same
 *   Windows-path-safe delimiter reasoning applies here unchanged).
 * - `folderListingSignature`: a plain, no-XML-parsing recursive directory listing (path
 *   relative to the merged-mod folder, size, mtime) of the actual merged-mod-content
 *   folder on disk, per the citation above - the one signal that also catches the shared-
 *   folder-only hazard. Located via `get_status`'s own `modsDirectory`/`mergedModName`
 *   fields (`WsmMcpTools.GetStatus()`, `WitcherScriptMerger.Core/Mcp/WsmMcpTools.cs`) -
 *   confirmed by reading that method directly that both are plain `AppState.Settings`/
 *   `Paths` reads, populated unconditionally, *not* gated on `modsDirectoryExists` (a
 *   separate field on the same response) - so they're reliable even when nothing has
 *   been merged yet or the mods directory doesn't exist. `buildFolderListingSignature`
 *   itself tolerates a missing folder (treats it as an empty listing) for exactly that
 *   reason, rather than requiring the caller to pre-check `modsDirectoryExists`.
 *
 * **Trigger points** (wired from `index.ts`): `gamemode-activated` into witcher3 (already
 * listened to for tool re-registration), `profile-did-change` (fires *after* a profile
 * switch completes, per `@nexusmods/vortex-api`'s `docs/EVENTS.md` - `(profileId: string)`
 * - a new listener this unit adds), and `did-deploy`, **positioned above (before) the
 * existing `isModOrDependencyInstallActive` early-return** in `checkForConflictsAfterDeploy`
 * - installing a Collection is precisely the window `isModOrDependencyInstallActive`
 * exists to detect and skip *conflict scanning* during, but it's also precisely the
 * window `importScriptMerges()` (hazard 1) actually runs in, so a coexistence check gated
 * behind that same early-return would never see the deployment where the overwrite
 * happened. Deliberately **not** wired off `profile-will-change` itself: that event is a
 * plain, synchronous `events.on` emit (`EVENTS.md` marks `will-deploy`/`did-deploy`.
 * "Async.", not this one), so `game-witcher3`'s own `onProfileWillChange` handler (also
 * registered via `events.on`, confirmed in its `index.ts`) is never awaited by Vortex
 * before continuing - there is no reliable way to snapshot "before" its file moves and
 * compare "after" from a second listener on the same event. Every trigger point above is
 * instead a *later* checkpoint that re-observes current, settled state, which is what
 * makes an idempotent snapshot-and-compare workable here at all: `profile-will-change`
 * itself is skipped, `profile-did-change` (after the switch, and after `game-witcher3`'s
 * own async handler has had time to run, though not provably so) is used instead.
 *
 * **Re-baselining after this extension's own writes.** `resolveAction.ts`'s
 * `runMergeConflictsWorkflow` calls `recordOwnMergeStateSnapshot` (not
 * `checkCoexistenceDrift`) immediately after a successful `mergeConflicts` call, using the
 * same already-open client (no extra spawn). This is necessary, not just tidy: without
 * it, this extension's *own* successful merges would themselves look like external
 * interference the next time a trigger point re-checks, since a real merge genuinely does
 * change both signals above. Re-snapshotting immediately (rather than tracking a "we just
 * merged" flag/timestamp) is deliberate too - idempotent, no timing window to race against
 * a later checkpoint, and it can't accidentally swallow a genuine external change that
 * happens to land inside some suppression window.
 */

export const WSM_COEXISTENCE_NOTIFICATION_ID = 'witcherscriptmerger-vortex-coexistence-drift';

/** Combined, comparable snapshot of "everything about WSM's merge state this extension
 *  can observe without re-implementing `MergeInventory.xml`'s own XML schema or WSM's
 *  own hashing (`Tools/Hasher.cs`)" - see this module's own header doc comment for why
 *  both halves are needed. */
export interface MergeStateSnapshot {
  folderListingSignature: string;
  mergeHistorySignature: string;
  /** Carried along for notification copy only - not itself part of the comparison
   *  (`mergeHistorySignature`/`folderListingSignature` already reflect anything that
   *  matters about it). */
  mergedModName: string;
}

/** The subset of `WsmMcpClient` this module needs - lets callers (and this module's own
 *  tests) pass an already-connected client without depending on the full class, matching
 *  `resolveAction.ts`'s own `WsmMergeClient`/`wsmStatusSummary.ts`'s own narrowed-surface
 *  seams. */
export interface MergeStateClient {
  getStatus(): Promise<GetStatusResult>;
  listMerges(): Promise<ListMergesResult>;
}

function isEnoent(err: unknown): boolean {
  return typeof err === 'object' && err !== null && (err as NodeJS.ErrnoException).code === 'ENOENT';
}

/**
 * Order-independent signature over `list_merges()`'s result. Deliberately mirrors
 * `conflictNotifications.ts`'s `computeConflictSignature` shape (per-entry field join,
 * sorted, newline-joined) rather than inventing a different convention - same
 * Windows-reserved-character delimiter reasoning applies unchanged (`:`/`|`/`\n` cannot
 * appear in a relative path, a mod name, or a hex hash string).
 */
export function computeMergeHistorySignature(merges: ListMergesResult): string {
  return merges
    .map(
      (m) =>
        `${m.relativePath}:${m.mergedModName}:${[...m.mods.map((mod) => `${mod.name}=${mod.hash}`)].sort().join('|')}`,
    )
    .sort()
    .join('\n');
}

/** Recursively walks `dir`, appending one `"<relative path>:<size>:<mtimeMs>"` entry per
 *  file (relative to `baseDir`) to `out`. A missing directory (the merged-mod folder
 *  doesn't exist yet - nothing has ever been merged, or the mods directory itself is
 *  absent) is treated as "no entries", not an error - this module's own doc comment
 *  explains why `computeMergeStateSnapshot` doesn't pre-check `modsDirectoryExists`
 *  before calling this. Any other error (a permissions problem, a locked file mid-scan)
 *  propagates - silently treating that as "empty" would risk a false "everything was
 *  deleted" drift signal instead of surfacing the real problem. */
async function walkFilesRecursive(dir: string, baseDir: string, out: string[]): Promise<void> {
  let entries: fs.Dirent[];
  try {
    entries = await fs.promises.readdir(dir, { withFileTypes: true });
  } catch (err) {
    if (isEnoent(err)) {
      return;
    }
    throw err;
  }

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      await walkFilesRecursive(fullPath, baseDir, out);
    } else if (entry.isFile() || entry.isSymbolicLink()) {
      // A symlink (isSymbolicLink() true, isFile()/isDirectory() both false - Dirent's
      // type reflects the link entry itself, not whatever it points at) is recorded via
      // lstat, not followed. Deliberately not resolved into a directory recursion (a
      // cyclic symlink would recurse forever) and not dereferenced to the *target*
      // file's own stat (would misattribute a change at some unrelated location as a
      // change *in this folder*, and could reach outside it entirely). Recording it as
      // an opaque entry - its own link metadata, not the target's - is enough for this
      // signature's purpose: notice that *something* here changed, not describe exactly
      // what. lstat (not stat) is used uniformly for the plain-file case too - the two
      // are identical for an actual file (they only differ when the entry itself is a
      // symlink), so one call site covers both without a branch. Caught in code review:
      // an earlier version used isFile() only, silently skipping - neither recording nor
      // recursing into - any symlink entirely, a real gap in the one signal this module
      // has that's supposed to observe the real folder contents unconditionally.
      const stat = await fs.promises.lstat(fullPath);
      out.push(`${path.relative(baseDir, fullPath)}:${stat.size}:${Math.floor(stat.mtimeMs)}`);
    }
  }
}

/**
 * Builds a sorted, newline-joined listing signature for every file recursively under
 * `folderPath`. Uses size + mtime, not a content hash - cheap (no file reads) and, per
 * this module's own header comment, both hazards this unit targets replace file content
 * wholesale (`mergeBackup.ts`'s `moveFiles` deletes-then-copies every file;
 * `importScriptMerges`'s `handleMergedScripts` does the same), so a genuine hazard always
 * changes both fields for every affected file. Accepted, documented tradeoff: an
 * incidental mtime-only touch with unchanged content and size (e.g. some external tool
 * touching the file without editing it) could produce a false-positive "drift" signal -
 * not dangerous (worst case, one extra distinctly-worded notification pointing the user
 * at the merge-history dashlet), just possible noise. Reimplementing WSM's own xxHash32
 * (`Tools/Hasher.cs`) in TypeScript to compare byte-for-byte instead would risk a subtle
 * mismatch with the .NET implementation and duplicate load-bearing hashing logic outside
 * this repo's own source of truth for it - not worth it for a best-effort secondary
 * signal.
 */
export async function buildFolderListingSignature(folderPath: string): Promise<string> {
  const entries: string[] = [];
  await walkFilesRecursive(folderPath, folderPath, entries);
  return entries.sort().join('\n');
}

/**
 * Combines both signals (see this module's own header doc comment) into one comparable
 * snapshot. Takes an already-connected client rather than connecting its own - the two
 * production call sites (`refreshCoexistenceState` below, and `resolveAction.ts`'s
 * `runMergeConflictsWorkflow`) each already have one open for their own purposes, and
 * amortizing this module's two extra tool calls (`get_status`, `list_merges`) onto an
 * existing connection is free; a caller with no client open yet should use
 * `refreshCoexistenceState`, which owns its own short-lived connect/close cycle per
 * `mcpClient.ts`'s documented lifecycle policy.
 */
export async function computeMergeStateSnapshot(client: MergeStateClient): Promise<MergeStateSnapshot> {
  // Run concurrently, not sequentially - get_status and list_merges are independent
  // reads with no data dependency between them (confirmed against WsmMcpTools.cs: each
  // re-scans/re-loads its own state fresh, with nothing shared or cached server-side
  // between calls), and WsmMcpClient's own JSON-RPC id-based request/response matching
  // already supports arbitrary concurrent in-flight requests correctly - this halves
  // this function's own latency contribution on the did-deploy path (itself inside
  // Vortex's awaited did-deploy window) for no correctness cost. Caught in code review -
  // an earlier version awaited these one after the other for no reason.
  const [status, merges] = await Promise.all([client.getStatus(), client.listMerges()]);

  const folderListingSignature = await buildFolderListingSignature(path.join(status.modsDirectory, status.mergedModName));

  return {
    folderListingSignature,
    mergeHistorySignature: computeMergeHistorySignature(merges),
    mergedModName: status.mergedModName,
  };
}

function snapshotsEqual(a: MergeStateSnapshot, b: MergeStateSnapshot): boolean {
  return a.folderListingSignature === b.folderListingSignature && a.mergeHistorySignature === b.mergeHistorySignature;
}

/** Module-level "last known" baseline - in-memory only, scoped to this extension's own
 *  process lifetime, matching `conflictNotifications.ts`'s own `lastNotifiedSignature`
 *  precedent (and its "no persisted cross-session state needed" rationale: a fresh
 *  Vortex session has no baseline to compare the first observation against, which is
 *  exactly the desired behavior - see `checkCoexistenceDrift` below). `undefined` (not a
 *  neutral empty-signature sentinel like that module uses) specifically because "no
 *  baseline yet" and "baseline is a snapshot of empty state" are genuinely different
 *  conditions here: the former must never warn (nothing to compare against), the latter
 *  must compare normally like any other snapshot. */
let lastKnownSnapshot: MergeStateSnapshot | undefined;

/** Test-only reset hook, mirroring `conflictNotifications.ts`'s
 *  `resetConflictNotificationState` - no production caller should ever need this. */
export function resetCoexistenceGuardState(): void {
  lastKnownSnapshot = undefined;
}

/**
 * Records `snapshot` as the new baseline without comparing or notifying - the
 * "re-baseline after our own writes" half of this module's own header doc comment.
 * `resolveAction.ts`'s `runMergeConflictsWorkflow` is the only production caller, for
 * both the dry-run preview and the real merge (harmless either way: a dry run doesn't
 * write anything, so re-recording it is an idempotent no-op against whatever the
 * baseline already was).
 */
export function recordOwnMergeStateSnapshot(snapshot: MergeStateSnapshot): void {
  lastKnownSnapshot = snapshot;
}

/**
 * Compares `snapshot` against the last known baseline and warns distinctly (never
 * throws - safe to call from any of `index.ts`'s event handlers, matching every other
 * module's "must never escape an event handler" convention) when it's genuinely
 * different from a *previously observed* baseline. The very first observation in a
 * session only seeds the baseline - there is nothing to have "drifted" from yet, and
 * treating a fresh session's first snapshot as drift would falsely accuse Vortex's own
 * extension of interference that may have happened in a previous session (or never at
 * all).
 *
 * On a genuine change: resets `conflictNotifications.ts`'s own suppression state
 * (`resetConflictNotificationState`) - a stale `alreadyResolved`-based conflict signature
 * is no longer trustworthy once WSM's own merge-state has changed underneath it (a merge
 * record `scan_conflicts` treated as "already resolved" may no longer reflect what's
 * actually in the merged-mod folder), so the next `did-deploy` should get a fair chance
 * to notify against reality rather than silently matching a now-stale "already seen"
 * signature. Also proactively dismisses the *ordinary* conflicts notification
 * (`WSM_CONFLICTS_NOTIFICATION_ID`) if one happens to be showing - it was computed
 * against merge state that's now known to be stale in *either* direction (conflicts may
 * have been silently resolved, in which case leaving a "N unresolved conflicts" warning
 * on screen is simply wrong; or the real count may now be different, in which case the
 * old number is inaccurate) - and this new, distinctly-worded notification below already
 * tells the user something needs a fresh look, so nothing is lost by clearing the old
 * one rather than leaving a now-unverifiable number next to it. Safe even when nothing
 * was actually showing (`dismissNotification` no-ops on an unknown id, per
 * `conflictNotifications.ts`'s own established assumption). Without this dismissal, the
 * *particular* case of an external change that fully resolved every conflict would never
 * get cleared at all: `resetConflictNotificationState` alone sets
 * `lastNotifiedSignature` back to `''`, which is the exact same value
 * `computeConflictSignature([])` produces for "nothing unresolved" - so the very next
 * did-deploy scan finding zero conflicts would see `signature === lastNotifiedSignature`
 * and return early *before* ever reaching the `dismissNotification` branch.
 *
 * Then sends a notification with an id/wording distinct from `conflictNotifications.ts`'s
 * own `WSM_CONFLICTS_NOTIFICATION_ID` - this is "something changed your WSM merge state
 * outside this extension", not "you have new conflicts", per this unit's own task
 * description. `allowSuppress` is deliberately omitted/`false` here (unlike the ordinary
 * conflicts notification's `allowSuppress: true`) - permanently suppressing "your merge
 * output may have been overwritten" is a worse default than for routine conflict nagging.
 *
 * **`lastKnownSnapshot` is only advanced to `snapshot` after a successful notification
 * (or immediately, for the "nothing changed"/first-observation cases, where there's
 * nothing to fail)** - deliberately not committed unconditionally up front. Mirrors
 * `conflictNotifications.ts`'s own `notifyConflictsIfChanged`, which documents the exact
 * same reasoning: if the baseline were advanced before `sendNotification` completes and
 * that call then throws, the user would never have actually seen the notification, yet
 * every later checkpoint would silently treat this exact drift as "already reported" for
 * the rest of the session - a failed attempt must be retried at the next checkpoint, not
 * recorded as handled.
 */
export function checkCoexistenceDrift(api: types.IExtensionApi, snapshot: MergeStateSnapshot): void {
  const previous = lastKnownSnapshot;

  if (previous === undefined || snapshotsEqual(previous, snapshot)) {
    lastKnownSnapshot = snapshot;
    return;
  }

  resetConflictNotificationState();
  try {
    api.dismissNotification?.(WSM_CONFLICTS_NOTIFICATION_ID);
  } catch (err) {
    // Best-effort only - a failure here must not prevent the coexistence notification
    // itself (the more important of the two) from still being attempted below.
    log('warn', 'witcherscriptmerger-vortex: failed to dismiss the stale conflicts notification during a coexistence-drift check', {
      error: err instanceof Error ? err.message : String(err),
    });
  }

  try {
    api.sendNotification?.({
      id: WSM_COEXISTENCE_NOTIFICATION_ID,
      type: 'warning',
      title: 'WitcherScriptMerger merge state changed outside this extension',
      message:
        'Something other than this extension\'s own "Resolve Script Conflicts" action changed your WitcherScriptMerger ' +
        'merge results - most likely Vortex\'s own built-in Witcher 3 Script Merger support (installing a Collection ' +
        'that bundles script merges, or a per-profile merge restore on switching profiles). Check the ' +
        '"WitcherScriptMerger History" dashlet and re-run "Resolve Script Conflicts" to review current state.',
      actions: [
        {
          title: 'More',
          action: () => {
            api.showDialog?.(
              'info',
              'WitcherScriptMerger Coexistence',
              {
                text:
                  'This companion extension detected that WitcherScriptMerger\'s recorded merges and/or merged-file ' +
                  'output changed since it last checked, without going through this extension\'s own "Resolve Script ' +
                  'Conflicts" action.\n\n' +
                  'Vortex has a separate, built-in Witcher 3 Script Merger integration that this extension coexists ' +
                  'with rather than replaces. That built-in integration can overwrite merge results when you install a ' +
                  'Collection containing script merges (it shows its own warning dialog first), and can back up/restore ' +
                  'merged scripts per Vortex profile if a profile has the "local_merges" profile feature enabled.\n\n' +
                  'This extension cannot prevent either of those - open the "WitcherScriptMerger History" dashlet to ' +
                  'see the current recorded merges, and use "Resolve Script Conflicts" (Mods page toolbar) to review ' +
                  'and re-merge anything that needs it.',
              },
              [{ label: 'Close', default: true }],
            );
          },
        },
      ],
    });
  } catch (err) {
    // Deliberately does NOT advance lastKnownSnapshot below in this branch - see this
    // function's own doc comment on why a failed notification attempt must be retried at
    // the next checkpoint rather than recorded as "already handled."
    log('warn', 'witcherscriptmerger-vortex: failed to show the coexistence-drift notification', {
      error: err instanceof Error ? err.message : String(err),
    });
    return;
  }

  lastKnownSnapshot = snapshot;
}

/**
 * Bounds each individual `get_status`/`list_merges` request (and the `initialize`
 * handshake) - matches `conflictScan.ts`'s own `POST_DEPLOY_SCAN_TIMEOUT_MS` exactly, for
 * the identical reason: this function is called from `index.ts`'s `checkForConflictsAfterDeploy`,
 * which runs *inside* Vortex's own `emitAndAwait('did-deploy', ...)` await window (see that
 * file's own comment, and `conflictScan.ts`'s) - `mcpClient.ts`'s default
 * `DEFAULT_REQUEST_TIMEOUT_MS` (30s) applied per-request would let this specific call site
 * extend Vortex's own reported deployment-completion time by up to a full minute across
 * the handshake + two tool calls. Applied uniformly (not only on the did-deploy path) for
 * simplicity - a bounded wait is also just better UX on the `profile-did-change`/
 * `gamemode-activated` trigger points, even though those aren't part of an awaited chain.
 */
const COEXISTENCE_CHECK_TIMEOUT_MS = 15_000;

export interface RefreshCoexistenceStateDeps {
  /** Test-only seam - defaults to the real `WsmMcpClient.connect`. */
  connect?: typeof WsmMcpClient.connect;
}

/**
 * Owns its own short-lived connect/compute/close cycle (unlike `computeMergeStateSnapshot`
 * above, which expects an already-open client) - the entry point every `index.ts` trigger
 * point (`gamemode-activated`, `profile-did-change`, `did-deploy`) calls directly. Never
 * throws: every failure (no tool acquired yet, connect failure, a tool-call error) is
 * logged and swallowed, exactly like `fetchMergeHistory`/`scanWsmConflicts`'s own
 * "must never break the caller's own primary flow" discipline - this is a secondary,
 * best-effort signal, and `index.ts`'s `checkForConflictsAfterDeploy` in particular must
 * still reach its own `scanWsmConflicts`/`notifyConflictsIfChanged` call afterward even if
 * this fails.
 *
 * Deliberately a **separate** WSM process spawn from `conflictScan.ts`'s own
 * `scanWsmConflicts` on the `did-deploy` path, not amortized onto the same client/
 * connection - a conscious tradeoff, not an oversight. Amortizing would mean either
 * widening `scanWsmConflicts`'s own return shape (an established, separately-tested
 * function with its own overlapping-call coalescing via `inFlightScan`) to also carry
 * `get_status`/`list_merges` results, or bypassing that coalescing with a second,
 * independent connect anyway - both add real coupling between this unit and Unit G's
 * conflict-scanning module for a feature that only needs to catch a *rare* event
 * (someone else's extension interfering with merge state), not a per-deployment hot
 * path. The extra process costs a bounded, sub-second-to-low-seconds WSM startup+
 * `get_status`+`list_merges` round trip once per Witcher3 deployment - acceptable given
 * deployments are user/install-triggered, not a polling loop.
 */
export async function refreshCoexistenceState(api: types.IExtensionApi, deps: RefreshCoexistenceStateDeps = {}): Promise<void> {
  try {
    if (!(await isWsmToolAcquired(api))) {
      log('debug', 'witcherscriptmerger-vortex: no acquired WSM tool - skipping coexistence-state check');
      return;
    }

    const connect = deps.connect ?? WsmMcpClient.connect;
    const gameDirectory = selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.path;
    const env = mergeWithProcessEnv(buildWsmEnv({ gameDirectory }));

    let client: WsmMcpClient | undefined;
    try {
      client = await connect({ exePath: getWsmExePath(api), env, requestTimeoutMs: COEXISTENCE_CHECK_TIMEOUT_MS });
      const snapshot = await computeMergeStateSnapshot(client);
      checkCoexistenceDrift(api, snapshot);
    } finally {
      if (client) {
        await client.close();
      }
    }
  } catch (err) {
    log('warn', 'witcherscriptmerger-vortex: coexistence-state check failed', {
      error: err instanceof Error ? err.message : String(err),
    });
  }
}

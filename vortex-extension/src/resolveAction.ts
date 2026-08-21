import { log, selectors, types } from 'vortex-api';
import { checkCoexistenceDrift, computeMergeStateSnapshot, recordOwnMergeStateSnapshot } from './coexistenceGuard';
import { WSM_TOOL_ID } from './discoveredTool';
import { isWitcher3Active, WITCHER3_GAME_ID } from './gating';
import { GetStatusResult, ListMergesResult, MergeConflictsArgs, MergeConflictsResult, WsmMcpClient, WsmMcpClientOptions } from './mcpClient';
import { buildMergeSummaryDialogContent } from './mergePanel';
import { mergeWithProcessEnv } from './wsmEnv';

/**
 * The "Resolve Script Conflicts" action: a `context.registerAction` entry (Mods page
 * toolbar - group `'mod-icons'`, confirmed against real, current Vortex source rather
 * than guessed: `gh search code "registerAction(\"mod-icons\"" --repo Nexus-Mods/Vortex`
 * turns up exactly this group used for the Mods page's own global-not-per-row toolbar
 * buttons, e.g. `open-directory`'s "Open Mod Staging Folder"/"Open Game Folder" and
 * `mod_management`'s own Deploy/Purge buttons - the same place `docs/vortex-extension-
 * design.md` §5 describes this action belonging) that spawns a `WsmMcpClient` per
 * `mcpClient.ts`'s own documented lifecycle policy (spawn per user-initiated workflow,
 * close in a `finally`), previews a merge via `mergeConflicts({dryRun: true})`, shows
 * the preview to the user (via `mergePanel.ts`'s dialog-content builder), and on
 * confirmation runs the real merge via a *second*, freshly-spawned client (see
 * `resolveScriptConflicts`'s own comment for why not the same client instance).
 *
 * v1 scope, deliberately: a single "merge everything" flow, not per-file selection or
 * `orderOverrides` - `mergeConflicts` is called with no `relativePaths` filter (every
 * detected conflict) and no `orderOverrides` (default load-order-comparer ordering).
 * Exposing that level of control was explicitly optional for v1 per this unit's own
 * task description; noted here (and in this unit's PR description) as a deliberate
 * choice, not an oversight.
 *
 * No separate "launch the GUI to resolve a skipped file by hand" fallback is built here
 * - a real (non-dry-run) `merge_conflicts` call already writes a git/diff3-style
 * conflict-marker sidecar under `DiffPlexConflicts/` for every file that couldn't
 * auto-solve, even with the function-level fallback, and opens it in the OS's default
 * associated editor as a side effect (`DiffPlexMergeEngine.MergeHeadless` ->
 * `Tools/FileOpener.Open`, unless the call was a dry run) - see this unit's own task
 * description and `WitcherScriptMerger.Core/Mcp/CLAUDE.md`'s "Minimal required
 * permissions" section. `mergePanel.ts`'s dialog content for a non-preview result
 * therefore just tells the user that already happened, rather than this file building a
 * second, redundant "launch WSM's GUI" mechanism - which would also need its own
 * separate binary-acquisition step, since Unit F only downloads the GUI-less Headless
 * build.
 */

const ACTIVITY_NOTIFICATION_ID = 'witcherscriptmerger-vortex-resolve-conflicts-activity';

/** The subset of `WsmMcpClient` this file actually needs - lets unit tests inject a
 *  fake without spawning a real WSM process (mirrors `toolAcquisition.ts`'s own
 *  `client`/`extractor` test seams).
 *
 *  `getStatus`/`listMerges` were added alongside Unit K (coexistence & Collections
 *  handling) - not because this file calls the underlying MCP tools directly, but
 *  because `runMergeConflictsWorkflow` passes the already-open client straight into
 *  `coexistenceGuard.ts`'s `computeMergeStateSnapshot(client: MergeStateClient)`, which
 *  needs both. Reusing the same already-connected client (rather than opening a third
 *  one just for this) is the entire point - see that function's own doc comment on why
 *  it takes a client instead of connecting its own. */
/**
 * How long to allow a single `merge_conflicts` MCP call - preview or real merge - before
 * giving up. Ten minutes, matching `nexusDownloader.ts`'s `DEFAULT_DOWNLOAD_TIMEOUT_MS`
 * precedent for "an operation whose runtime is the user's data, not a round trip".
 *
 * `mcpClient.ts`'s general-purpose `DEFAULT_REQUEST_TIMEOUT_MS` (30s) is far too short
 * here, and this is not theoretical: on a real 274-mod load order with 44 conflicting
 * script files, the *preview* alone blew past 30s, so `resolveScriptConflicts` reported
 * "Failed to preview script-conflict merges" and returned at the preview stage - the real
 * merge below it never ran at all. The user was left with an unmerged (and, because a
 * prior deploy had already cleared it, empty) `mod0000_MergedFiles` and a game that
 * refused to start, with ~200 script compile errors naming members the merge is supposed
 * to add to vanilla scripts.
 *
 * Note this applies to the dry-run preview just as much as the real merge: a preview does
 * the entire scan-and-three-way-merge computation and only skips the writes, so it costs
 * essentially the same time as the merge it is previewing. Sizing this off "it's only a
 * preview" would reintroduce the same bug.
 *
 * Deliberately NOT passed to `connect` as the client-wide `requestTimeoutMs`: that also
 * bounds the `initialize` handshake, which should still fail fast when WSM can't start -
 * see `WsmMcpClient.callTool`'s own comment on the per-call override.
 */
const MERGE_CALL_TIMEOUT_MS = 10 * 60 * 1000;

export interface WsmMergeClient {
  mergeConflicts(args?: MergeConflictsArgs, timeoutMs?: number): Promise<MergeConflictsResult>;
  getStatus(): Promise<GetStatusResult>;
  listMerges(): Promise<ListMergesResult>;
  close(): Promise<void>;
}

export interface ResolveScriptConflictsDeps {
  /** Test-only seam - defaults to `WsmMcpClient.connect`. */
  connect?: (options: WsmMcpClientOptions) => Promise<WsmMergeClient>;
}

/** Resolves the WSM tool `toolAcquisition.ts` already registered for Witcher 3 (via
 *  `actions.addDiscoveredTool`), if any - see `discoveredTool.ts`'s own doc comment for
 *  the `IDiscoveredTool` shape this reads (`path`, `environment`). Returns `undefined`
 *  when no tool has been acquired/registered yet, exactly like `selectors.discoveryByGame`
 *  itself can return `undefined` for a game with no discovery result at all
 *  (`toolAcquisition.ts` already relies on this same optional-chaining pattern). */
function getDiscoveredWsmTool(api: types.IExtensionApi): types.IDiscoveredTool | undefined {
  return selectors.discoveryByGame(api.getState(), WITCHER3_GAME_ID)?.tools?.[WSM_TOOL_ID];
}

/**
 * Drives the full preview -> confirm -> merge workflow. Exported (not just wired
 * privately into the registered action below) so this orchestration is directly unit
 * testable against a fake `connect`/fake `api`, without a real WSM process or a real
 * Vortex dialog system.
 *
 * **Two separate client instances, not one kept alive across the confirmation wait**:
 * the dry-run preview's client is connected, used, and closed before the preview
 * dialog is ever shown; if the user confirms, a *second*, freshly-spawned client
 * handles the real merge. `mcpClient.ts`'s own lifecycle policy is "spawn per
 * user-initiated workflow, tear down when the caller is done with it" - keeping a
 * child process alive and idle for as long as the user takes to read and act on the
 * preview dialog (seconds to indefinitely long) isn't "done with it" in any useful
 * sense, and every WSM MCP tool call already re-scans the mods folder from scratch
 * server-side regardless (`WitcherScriptMerger.Core/Mcp/CLAUDE.md`), so there's no
 * real work saved by reuse here - only an idle process kept around for no benefit.
 */
export async function resolveScriptConflicts(
  api: types.IExtensionApi,
  deps: ResolveScriptConflictsDeps = {},
): Promise<void> {
  const connect = deps.connect ?? ((options: WsmMcpClientOptions) => WsmMcpClient.connect(options));

  const tool = getDiscoveredWsmTool(api);
  if (!tool?.path) {
    api.sendNotification?.({
      id: 'witcherscriptmerger-vortex-resolve-conflicts-no-tool',
      type: 'error',
      title: 'WitcherScriptMerger not found',
      message: 'Acquire WitcherScriptMerger before resolving script conflicts.',
    });
    return;
  }

  const env = mergeWithProcessEnv(tool.environment ?? {});

  // The two try/catches below (around each runMergeConflictsWorkflow call) give a
  // phase-specific error message ("preview" vs. "merge") for the failure mode that's
  // actually expected to happen there (the WSM process failing to spawn/respond). This
  // outer try/catch is a separate, general safety net around everything else in this
  // function's body - dialog-content building (mergePanel.ts) and the showDialog calls
  // themselves - so that ANY unexpected exception still reaches the user via
  // reportFailure instead of becoming a silently-swallowed rejected promise (caught
  // only by the last-resort, log-only `.catch()` around this function's own caller in
  // registerResolveScriptConflictsAction). Caught in this unit's own code review: a
  // missing `functionLevelDecisions` field on an older WSM binary's response used to
  // be exactly this kind of unguarded failure (now also fixed at its root in
  // mergePanel.ts's own defensive default) - this net exists for whatever the next
  // one of these turns out to be, not just that specific case.
  try {
    let preview: MergeConflictsResult;
    try {
      preview = await runMergeConflictsWorkflow(api, connect, tool.path, env, {
        dryRun: true,
        // overwrite so the preview answers "would this auto-solve?" for
        // already-merged conflicts too - without it, the server can only predict
        // "skipped: output already exists" for them, and this preview exists to show
        // what the real (also-overwrite) run below will actually do.
        overwrite: true,
        activityMessage: 'Scanning for mergeable script conflicts...',
      });
    } catch (err) {
      reportFailure(api, 'Failed to preview script-conflict merges', err);
      return;
    }

    if (preview.merged.length === 0 && preview.skipped.length === 0 && preview.unmatched.length === 0) {
      await api.showDialog?.(
        'info',
        'Script Merger',
        { text: 'No script conflicts were detected - nothing to merge.' },
        [{ label: 'Close', default: true }],
      );
      return;
    }

    const previewChoice = await api.showDialog?.(
      'question',
      'Resolve Script Conflicts - Preview',
      buildMergeSummaryDialogContent(preview, { isPreview: true }),
      [{ label: 'Cancel' }, { label: 'Merge Now', default: true }],
    );

    if (previewChoice?.action !== 'Merge Now') {
      return;
    }

    let result: MergeConflictsResult;
    try {
      result = await runMergeConflictsWorkflow(api, connect, tool.path, env, {
        dryRun: false,
        // A Vortex-driven re-merge is exactly the "source mod updated, stale merged
        // output should be rebuilt" case overwrite exists for - see
        // MergeConflictsArgs.overwrite's own doc comment (including version-skew
        // behavior against a pre-overwrite WSM build).
        overwrite: true,
        activityMessage: 'Merging script conflicts...',
      });
    } catch (err) {
      reportFailure(api, 'Failed to merge script conflicts', err);
      return;
    }

    await api.showDialog?.(
      result.skipped.length > 0 ? 'info' : 'success',
      'Resolve Script Conflicts - Result',
      buildMergeSummaryDialogContent(result, { isPreview: false }),
      [{ label: 'Close', default: true }],
    );
  } catch (err) {
    reportFailure(api, 'Resolve Script Conflicts failed unexpectedly', err);
  }
}

/** One spawn-call-close cycle, wrapped with an 'activity' notification (spinner icon,
 *  no dismiss button - see `INotification`'s own `type`/`noDismiss` docs) so the user
 *  gets feedback while a scan/merge (which can take a while for a large mods folder) is
 *  in flight. The notification is dismissed in a `finally` so it never lingers past this
 *  call whether `mergeConflicts` succeeds, throws, or `client.close()` itself throws. */
async function runMergeConflictsWorkflow(
  api: types.IExtensionApi,
  connect: (options: WsmMcpClientOptions) => Promise<WsmMergeClient>,
  exePath: string,
  env: NodeJS.ProcessEnv,
  options: MergeConflictsArgs & { activityMessage: string },
): Promise<MergeConflictsResult> {
  const { activityMessage, ...args } = options;

  api.sendNotification?.({
    id: ACTIVITY_NOTIFICATION_ID,
    type: 'activity',
    title: 'Script Merger',
    message: activityMessage,
    noDismiss: true,
  });

  try {
    const client = await connect({ exePath, env });
    try {
      const result = await client.mergeConflicts(args, MERGE_CALL_TIMEOUT_MS);

      // Unit K: reconciles coexistenceGuard.ts's own "last known merge state" against
      // this extension's own just-completed workflow, using the same still-open client
      // (no extra spawn). Deliberately non-fatal (never lets a failure here affect the
      // result the user is about to see) either way, but the *two* calls below are not
      // interchangeable - which one runs depends on whether this was the dry-run preview
      // or the real, confirmed merge:
      //
      // - Real merge (`args.dryRun !== true`): `recordOwnMergeStateSnapshot` - silently
      //   adopts the post-merge state as the new baseline, no comparison, no notification.
      //   This extension itself just wrote that state, so it's known-good by definition;
      //   without this, this extension's own successful merge would look identical to
      //   external interference the next time a trigger point (did-deploy,
      //   profile-did-change, gamemode-activated) re-checks, since a genuine merge does
      //   change both signals `computeMergeStateSnapshot` compares.
      // - Dry-run preview (`args.dryRun === true`): `checkCoexistenceDrift` instead -
      //   compares against the existing baseline and warns if it's already diverged
      //   *before* this workflow ever wrote anything. **Not** a silent re-baseline here:
      //   a dry run performs no write (`WsmMcpTools.MergeConflicts`'s own `dryRun`
      //   contract), so if the two differ, that's evidence of drift that happened
      //   *before* the user even opened this dialog - silently adopting the preview's
      //   read as the new "known good" baseline (as an earlier version of this code did)
      //   would erase that evidence permanently, including for a user who previews and
      //   then cancels without merging anything at all. Caught in code review: a
      //   preview-then-cancel workflow was silently absorbing real drift into the
      //   baseline with no notification ever shown, defeating this unit's entire purpose
      //   for exactly that sequence.
      try {
        const snapshot = await computeMergeStateSnapshot(client);
        // `!== true`, not `=== false`: `MergeConflictsArgs.dryRun` is optional, and the
        // server-side tool's own default when omitted is a *real* merge
        // (`WsmMcpTools.MergeConflicts(..., bool dryRun = false)`) - so an omitted value
        // must land on the "real merge" branch below, matching that server default,
        // rather than being misread as a preview merely because it isn't literally
        // `=== false`. Both of this file's own call sites always pass an explicit
        // `dryRun: true`/`dryRun: false` today, so this only matters for a hypothetical
        // future caller that omits it.
        if (args.dryRun !== true) {
          recordOwnMergeStateSnapshot(snapshot);
        } else {
          checkCoexistenceDrift(api, snapshot);
        }
      } catch (err) {
        log('debug', 'witcherscriptmerger-vortex: failed to reconcile the coexistence-guard baseline after a resolve-script-conflicts workflow', {
          error: err instanceof Error ? err.message : String(err),
        });
      }

      return result;
    } finally {
      await client.close();
    }
  } finally {
    api.dismissNotification?.(ACTIVITY_NOTIFICATION_ID);
  }
}

/**
 * A timed-out `merge_conflicts` call reads, on its own, as a bare transport error
 * ("request 'tools/call' timed out after ...ms") that says nothing about what it means for
 * the user's install. It means specifically that *no merge was written* - and if a deploy
 * had already cleared the merged mod, that leaves the game unable to start, which is a far
 * worse outcome than the wording suggests. `isTimeout` spots that case so `reportFailure`
 * can say so, and point at the one thing that actually helps (a huge load order simply
 * needing longer than `MERGE_CALL_TIMEOUT_MS`).
 */
function isTimeout(err: unknown): boolean {
  return err instanceof Error && /timed out after \d+ms/.test(err.message);
}

function reportFailure(api: types.IExtensionApi, message: string, err: unknown): void {
  const detail = err instanceof Error ? err : String(err);
  log('warn', 'witcherscriptmerger-vortex: resolveScriptConflicts failed', {
    message,
    error: err instanceof Error ? err.message : String(err),
    timedOut: isTimeout(err),
  });
  const shown = isTimeout(err)
    ? `${message}: Script Merger did not finish within ${Math.round(MERGE_CALL_TIMEOUT_MS / 60000)} minutes, so nothing was merged. `
      + 'Your merged mod has been left untouched - if it is empty or out of date, the game may fail to start until a merge completes. '
      + 'Very large load orders can need longer; running Script Merger directly will also do the merge.'
    : message;
  api.showErrorNotification?.(shown, detail);
}

/**
 * Registers the "Resolve Script Conflicts" action. Called directly from `index.ts`'s
 * `main()` - NOT deferred through `context.once` (see `index.ts`'s own header comment
 * for why: `IExtensionContext.once`'s own doc comment says registrations are expected
 * to already be done by the time it fires). Gated on Witcher 3 being the active game
 * via the same live `condition` callback pattern `gating.ts`'s own doc comment
 * prescribes for every registration this extension adds - re-evaluated by Vortex on
 * every game-mode switch, not just checked once at load time.
 */
export function registerResolveScriptConflictsAction(context: types.IExtensionContext): void {
  context.registerAction(
    'mod-icons',
    300,
    'conflict',
    {},
    'Resolve Script Conflicts',
    () => {
      resolveScriptConflicts(context.api).catch((err: unknown) => {
        // resolveScriptConflicts already reports failures it knows about via
        // showErrorNotification - this is a last-resort catch for anything that
        // escaped that (e.g. a bug in the dialog-content builder itself), so it must
        // never throw out of a registerAction callback.
        log('warn', 'witcherscriptmerger-vortex: resolveScriptConflicts action callback failed unexpectedly', {
          error: err instanceof Error ? err.message : String(err),
        });
      });
    },
    () => isWitcher3Active(context.api),
  );
}

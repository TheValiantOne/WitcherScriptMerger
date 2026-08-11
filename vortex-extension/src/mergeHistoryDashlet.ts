import * as React from 'react';
import { Dashlet, types } from 'vortex-api';
import { isWitcher3Active } from './gating';
import { RecordedMerge, WsmMcpClient } from './mcpClient';
import { resolveWsmExePathIfUsable } from './wsmToolPath';

/**
 * Dashboard tile listing every merge already recorded in `MergeInventory.xml` (relative
 * path, which mod folder holds the merged result, and each source mod's recorded hash) -
 * the read-only "merge history" view from `docs/vortex-extension-design.md`'s section 5.
 *
 * **Data source (option (a) of this unit's two options): `WsmMcpClient.listMerges()`,
 * not a direct `MergeInventory.xml` parse.** Two things pointed the same way: (1)
 * `mcpClient.ts`'s own doc comment already names "a merge-history dashlet" as one of the
 * intended per-workflow callers of `WsmMcpClient`, and (2)
 * `docs/vortex-extension-design.md`'s section 5 explicitly recommends `list_merges` "for
 * parity/simplicity" now that it exists, over this extension re-parsing the XML itself.
 * Going through WSM's own MCP tool means this file never has to duplicate
 * `WitcherScriptMerger.Core/Inventory/MergeInventory.cs`'s `XmlSerializer` schema
 * (`RelativePath`/`MergedModName`/`IncludedMod[Hash]` element/attribute names, the
 * `AddMissingHashes` auto-heal-on-load quirk, etc.) - that schema knowledge stays owned
 * by the C# side, at the cost of spawning a short-lived WSM process per fetch instead of
 * a plain file read. For a dashlet that only fetches on mount/manual refresh (not on a
 * timer), that cost is a handful of times per Vortex session, not a hot path.
 *
 * **Process lifecycle**: one `WsmMcpClient` per fetch (initial mount, or a manual
 * "Refresh" click), closed in a `finally` - never a long-lived singleton, per
 * `mcpClient.ts`'s own documented policy ("spawn per user-initiated workflow, tear down
 * when the caller is done with it"). This dashlet reads its own mount, and each of its
 * own subsequent refreshes, as that workflow - there's no multi-step review session (like
 * a future scan-then-merge panel) to amortize a handshake across here.
 */

export interface MergeHistoryFetchDeps {
  /** Test-only seam - defaults to the real `WsmMcpClient.connect`. */
  connect?: typeof WsmMcpClient.connect;
}

export type MergeHistoryResult =
  | { status: 'not-installed' }
  | { status: 'error'; message: string }
  | { status: 'loaded'; merges: RecordedMerge[] };

/**
 * Absolute path to the WSM Headless exe this extension should use, or `null` when
 * nothing usable resolves. Now just the central resolver (`wsmToolPath.ts` - user
 * override first, then the managed install); this module's former private copy of the
 * managed-path computation was the "known duplication, not an oversight" its own
 * comment promised a later unit would extract. Still deliberately not read from
 * Vortex's discovered-tools Redux state - see `discoveredTool.ts` on that state's
 * unverified persistence story.
 */
export async function resolveWsmExePath(api: types.IExtensionApi): Promise<string | null> {
  return (await resolveWsmExePathIfUsable(api)) ?? null;
}

/**
 * Fetches merge history via a short-lived `WsmMcpClient`: connect, `list_merges`, close -
 * always closes, even when `listMerges()` itself throws, per this module's own "process
 * lifecycle" doc comment above. Never throws itself - every failure (no acquired exe,
 * connect failure, a tool-call error) becomes a `MergeHistoryResult` the caller can
 * render directly.
 */
export async function fetchMergeHistory(
  api: types.IExtensionApi,
  deps: MergeHistoryFetchDeps = {},
): Promise<MergeHistoryResult> {
  const exePath = await resolveWsmExePath(api);
  if (exePath === null) {
    return { status: 'not-installed' };
  }

  const connect = deps.connect ?? WsmMcpClient.connect;
  let client: WsmMcpClient | undefined;
  try {
    client = await connect({ exePath });
    const merges = await client.listMerges();
    return { status: 'loaded', merges };
  } catch (err) {
    return { status: 'error', message: err instanceof Error ? err.message : String(err) };
  } finally {
    if (client) {
      await client.close();
    }
  }
}

interface MergeHistoryDashletProps {
  api: types.IExtensionApi;
}

type MergeHistoryDashletState = { status: 'loading' } | MergeHistoryResult;

export class MergeHistoryDashlet extends React.Component<MergeHistoryDashletProps, MergeHistoryDashletState> {
  private mounted = false;

  constructor(props: MergeHistoryDashletProps) {
    super(props);
    this.state = { status: 'loading' };
    this.handleRefreshClick = this.handleRefreshClick.bind(this);
  }

  componentDidMount(): void {
    this.mounted = true;
    void this.load();
  }

  componentWillUnmount(): void {
    this.mounted = false;
  }

  private handleRefreshClick(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.setStateIfMounted({ status: 'loading' });
    const result = await fetchMergeHistory(this.props.api);
    this.setStateIfMounted(result);
  }

  private setStateIfMounted(state: MergeHistoryDashletState): void {
    if (this.mounted) {
      this.setState(state);
    }
  }

  render(): React.ReactElement {
    return React.createElement(
      Dashlet,
      { className: 'wsm-merge-history-dashlet', title: 'WitcherScriptMerger History' },
      this.renderBody(),
    );
  }

  private renderBody(): React.ReactElement {
    const { state } = this;

    const refreshButton = React.createElement(
      'button',
      {
        type: 'button',
        className: 'btn btn-default',
        disabled: state.status === 'loading',
        onClick: this.handleRefreshClick,
      },
      'Refresh',
    );

    return React.createElement('div', null, refreshButton, this.renderContent(state));
  }

  private renderContent(state: MergeHistoryDashletState): React.ReactElement {
    switch (state.status) {
      case 'loading':
        return React.createElement('p', null, 'Loading merge history...');
      case 'not-installed':
        return React.createElement(
          'p',
          null,
          'WitcherScriptMerger has not been acquired yet - no merge history to show.',
        );
      case 'error':
        return React.createElement(
          'p',
          { className: 'text-danger' },
          `Failed to load merge history: ${state.message}`,
        );
      case 'loaded':
        return this.renderMerges(state.merges);
      default: {
        // Exhaustiveness check: a new MergeHistoryDashletState member added without a
        // matching case here fails `tsc`, not just at runtime.
        const exhaustiveCheck: never = state;
        throw new Error(`Unhandled merge history status: ${JSON.stringify(exhaustiveCheck)}`);
      }
    }
  }

  private renderMerges(merges: RecordedMerge[]): React.ReactElement {
    if (merges.length === 0) {
      return React.createElement('p', null, 'No merges recorded yet.');
    }

    const rows = merges.map((merge) =>
      React.createElement(
        'li',
        { key: merge.relativePath },
        React.createElement('strong', null, merge.relativePath),
        ` → ${merge.mergedModName} (${merge.mods.map((mod) => `${mod.name} [${mod.hash}]`).join(', ')})`,
      ),
    );

    return React.createElement('ul', { className: 'wsm-merge-history-list' }, ...rows);
  }
}

/**
 * Registers the merge-history dashlet. Call this synchronously from `index.ts`'s
 * `main()`, **not** from inside `context.once(...)`: despite `index.ts`'s own doc
 * comment suggesting later `context.register*` calls belong inside `once`,
 * `@nexusmods/vortex-api`'s own `IExtensionContext` doc comment
 * (`node_modules/@nexusmods/vortex-api/lib/api.d.ts`, ~line 3578) is explicit that
 * `once` "should be used for all your extension setup **except for the register
 * calls**" - register calls are collected once, synchronously, while every extension's
 * own `init`/`main` runs, before `once` ever fires. (The tool-acquisition unit's own use
 * of `once` for `tryRegisterWsmTool` is a different, legitimate case: that function
 * dispatches a Redux action via `api.store`, which needs the store to exist - a real
 * `once`-shaped requirement, not a register call.)
 *
 * Gated on `isWitcher3Active` via the live `isVisible` callback (matches `gating.ts`'s
 * own documented preference for a live `condition`/`isVisible` over a load-time-only
 * check) - ignores the `state` argument Vortex passes into `isVisible` and re-derives it
 * from `context.api.getState()` instead, so this reuses `gating.ts`'s helper directly
 * rather than duplicating its `selectors.activeGameId` call.
 */
export function registerMergeHistoryDashlet(context: types.IExtensionContext): void {
  context.registerDashlet(
    'WitcherScriptMerger History',
    2,
    2,
    250,
    MergeHistoryDashlet,
    () => isWitcher3Active(context.api),
    () => ({ api: context.api }),
    { closable: true },
  );
}

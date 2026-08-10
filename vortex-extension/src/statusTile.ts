import * as React from 'react';
import { selectors, types, util } from 'vortex-api';
import { QUICKBMS_HOMEPAGE_URL } from './bundleTools';
import { WITCHER3_GAME_ID } from './gating';
import { WCC_LITE_NEXUS_MOD_URL, acquireWccLite } from './wccLiteAcquisition';
import { WsmStatusSummary, getWsmStatusSummary } from './wsmStatusSummary';

/**
 * Dashboard tile showing WSM's dependency/status snapshot - `docs/vortex-extension-design.md`
 * §5's "dependency/status tile" ("whether QuickBMS/wcc_lite are found ..., resolved
 * game/mods directories, ... live conflict count. Useful as a single place to tell the
 * user 'your script-merge tooling isn't set up' before they hit a confusing failure
 * mid-deploy."). Data-fetching itself lives in `wsmStatusSummary.ts` (kept separate so
 * it's unit-testable without a React/DOM harness - see that module's own doc comment);
 * this file is just the `React.ComponentClass`/`FunctionComponent` `context.
 * registerDashlet` needs, plus the registration call itself.
 *
 * Deliberately built with `React.createElement` rather than JSX syntax: this project's
 * `tsconfig.json` `include` only covers `src/**\/*.ts` (no `*.tsx`), and introducing a
 * `.tsx` file/JSX pipeline would be a shared build-config change (`tsconfig.json`,
 * `webpack.config.cjs`'s `ts-loader` rule) that risks colliding with the three sibling
 * units (G/H/I) touching this same extension in parallel - see this unit's own task
 * instructions on keeping shared-file changes additive/minimal. Plain
 * `React.createElement` calls need neither.
 *
 * Deliberately does **not** import `Dashlet` (the `vortex-api`-provided panel chrome
 * component) for the same reason `vortex-api` itself is never imported as a *value* by
 * any earlier unit beyond `actions`/`selectors`/`util`/`log`: `test/testUtils/
 * vortexApiStub.ts` (the module vitest resolves the bare `'vortex-api'` specifier to)
 * doesn't export it, so it would be `undefined` at module-evaluation time in any test
 * that transitively imports this file, including `index.test.ts`'s `main()` smoke
 * tests. A plain wrapper `<div>` keeps this component's own chrome self-contained and
 * keeps that shared test stub untouched.
 */

export interface WsmStatusDashletProps {
  api: types.IExtensionApi;
}

function row(key: string, label: string, value: React.ReactNode): React.ReactElement {
  return React.createElement(
    'div',
    { key, style: { display: 'flex', gap: '0.5em', padding: '2px 0' } },
    React.createElement('span', { style: { fontWeight: 'bold', minWidth: '14em' } }, label),
    React.createElement('span', undefined, value),
  );
}

function yesNo(value: boolean): string {
  return value ? 'Yes' : 'No';
}

/**
 * Opens `url` in the OS's default browser via `vortex-api`'s own `util.opn` (confirmed
 * against `lib/api.d.ts`: `open_2` exported as `opn`, `(target: string, wait?: boolean)
 * => Promise<void>`) rather than a plain `<a target="_blank">` - a bare `target="_blank"`
 * anchor inside Vortex's Electron renderer does not reliably open the user's actual
 * system browser (Electron either blocks the navigation or opens a chrome-less
 * `BrowserWindow` unless the host app registers its own new-window handler for it);
 * `opn` is the mechanism Vortex extensions use for exactly this.
 */
function externalLink(url: string, text: string): React.ReactElement {
  const handleClick = (event: React.MouseEvent): void => {
    event.preventDefault();
    util.opn(url).catch(() => {
      // Best-effort - opn() failing (e.g. no default browser configured on this
      // machine) isn't something this click handler can usefully recover from beyond
      // not crashing.
    });
  };
  return React.createElement('a', { href: url, onClick: handleClick }, text);
}

function renderBundleToolRow(
  key: string,
  label: string,
  detectedPath: string | undefined,
  fallback: React.ReactNode,
): React.ReactElement {
  return row(key, label, detectedPath ?? fallback);
}

export function WsmStatusDashletContent(props: WsmStatusDashletProps): React.ReactElement {
  const { api } = props;
  const [summary, setSummary] = React.useState<WsmStatusSummary | undefined>(undefined);
  const [loading, setLoading] = React.useState<boolean>(true);
  const [wccLiteError, setWccLiteError] = React.useState<string | undefined>(undefined);
  const [fetchingWccLite, setFetchingWccLite] = React.useState<boolean>(false);

  // Combined "something is in flight" flag, used to disable *both* buttons regardless
  // of which action started it - without this, clicking "Refresh" while "Get wcc_lite"
  // is still running (or vice versa) could fire two overlapping WSM process
  // spawns/MCP handshakes/mods-folder scans that race each other's setSummary() call.
  const busy = loading || fetchingWccLite;

  // Returns its own promise (rather than firing-and-forgetting internally) so
  // handleGetWccLite below can genuinely wait for the post-acquisition refresh to
  // finish before clearing fetchingWccLite - otherwise the "Downloading..." button
  // label would revert to idle as soon as refresh() was merely *invoked*, not once the
  // new status (a full WSM process spawn + MCP handshake) actually finished loading.
  const refresh = React.useCallback((): Promise<void> => {
    setLoading(true);
    // A refresh is a "start over" action - any stale wcc_lite-download error from a
    // previous attempt shouldn't keep rendering once the user has asked for a fresh
    // status check (e.g. after installing wcc_lite by hand and clicking Refresh).
    setWccLiteError(undefined);
    return getWsmStatusSummary(api)
      .then((result) => setSummary(result))
      .catch((err: unknown) => setSummary({ kind: 'error', message: err instanceof Error ? err.message : String(err) }))
      .finally(() => setLoading(false));
  }, [api]);

  React.useEffect(() => {
    refresh();
  }, [refresh]);

  const handleGetWccLite = React.useCallback(() => {
    setFetchingWccLite(true);
    setWccLiteError(undefined);
    acquireWccLite({ api })
      .then(() => refresh())
      .catch((err: unknown) => setWccLiteError(err instanceof Error ? err.message : String(err)))
      .finally(() => setFetchingWccLite(false));
  }, [api, refresh]);

  const children: React.ReactNode[] = [
    React.createElement('h4', { key: 'title', style: { marginTop: 0 } }, 'WitcherScriptMerger Status'),
  ];

  if (loading && summary === undefined) {
    children.push(React.createElement('div', { key: 'loading' }, 'Checking WitcherScriptMerger status...'));
  } else if (summary?.kind === 'not-acquired') {
    children.push(
      React.createElement(
        'div',
        { key: 'not-acquired' },
        "WitcherScriptMerger hasn't been downloaded yet.",
      ),
    );
  } else if (summary?.kind === 'error') {
    children.push(
      React.createElement('div', { key: 'error', style: { color: '#c0392b' } }, `Unable to check status: ${summary.message}`),
    );
  } else if (summary?.kind === 'ok') {
    const { status, bundleTools } = summary;
    children.push(
      row('textMergeDeps', 'Text-merge engine ready:', yesNo(status.textMergeDependenciesValid)),
      row('bundleDeps', 'Bundle tooling ready:', yesNo(status.bundleDependenciesValid)),
      row('modsDir', 'Mods directory:', status.modsDirectory || '(not configured)'),
      row('modsDirExists', 'Mods directory exists:', yesNo(status.modsDirectoryExists)),
      row('conflictCount', 'Detected conflicts:', String(status.conflictCount)),
      row('mergedModName', 'Merged mod name:', status.mergedModName || '(not configured)'),
      renderBundleToolRow(
        'quickBms',
        'QuickBMS:',
        bundleTools.quickBmsPath && bundleTools.quickBmsPluginPath ? bundleTools.quickBmsPath : undefined,
        React.createElement(
          React.Fragment,
          undefined,
          'Not found - ',
          externalLink(QUICKBMS_HOMEPAGE_URL, 'get it yourself (QuickBMS homepage)'),
          '. Licensing terms are unclear, so this extension never downloads it automatically.',
        ),
      ),
      renderBundleToolRow(
        'wccLite',
        'wcc_lite:',
        bundleTools.wccLitePath,
        React.createElement(
          React.Fragment,
          undefined,
          'Not found - ',
          React.createElement(
            'button',
            { type: 'button', disabled: busy, onClick: handleGetWccLite },
            fetchingWccLite ? 'Downloading...' : 'Get wcc_lite from Nexus Mods',
          ),
          ' (',
          externalLink(WCC_LITE_NEXUS_MOD_URL, 'mod page'),
          ')',
        ),
      ),
    );
    if (wccLiteError) {
      children.push(row('wccLiteError', 'wcc_lite download failed:', wccLiteError));
    }
  }

  children.push(
    React.createElement(
      'button',
      { key: 'refresh', type: 'button', disabled: busy, onClick: refresh, style: { marginTop: '0.5em' } },
      loading ? 'Refreshing...' : 'Refresh',
    ),
  );

  return React.createElement('div', { className: 'wsm-status-dashlet' }, ...children);
}

/** Registers the dashlet - see this module's own doc comment for why it's a plain
 *  `<div>`-wrapped component rather than one using `vortex-api`'s `Dashlet` chrome.
 *  Called once from `index.ts`'s own `context.once`; visibility itself is re-evaluated
 *  live by Vortex on every game-mode switch via `isVisible`, the same dynamic-gating
 *  shape every other registration in this extension uses (see `gating.ts`'s own doc
 *  comment) - no restart needed to show/hide this tile when switching in or out of
 *  Witcher 3. */
export function registerWsmStatusDashlet(context: types.IExtensionContext): void {
  context.registerDashlet(
    'WitcherScriptMerger Status',
    2,
    2,
    250,
    WsmStatusDashletContent,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (state: any) => selectors.activeGameId(state) === WITCHER3_GAME_ID,
    () => ({ api: context.api }),
    {},
  );
}

import * as fs from 'fs';
import * as https from 'https';

/**
 * Download-from-GitHub-Releases logic for acquiring a WSM build. Parameterized on
 * `repo`/`tag` (never hardcoded beyond `DEFAULT_WSM_REPO`'s default). A real release
 * now exists (v0.6.2, published 2026-08-11, with exactly the asset names this module
 * expects), so this path is exercisable for real - unit tests still go through
 * `githubRelease.test.ts`'s mocked `HttpClient` only (no test makes a real network
 * call).
 *
 * Asset naming matches `.github/workflows/release.yml`'s `package-release` job exactly:
 * `WitcherScriptMerger.Headless-<version>-win-x64.zip` (the CLI/MCP-only, no-GUI host -
 * see `buildAssetFileName`'s own comment for why this asset, not the WinForms host's,
 * is the one this extension wants).
 */

export const DEFAULT_WSM_REPO = 'TheValiantOne/WitcherScriptMerger';

/**
 * The WSM version the status dashlet's "Download WitcherScriptMerger" action acquires -
 * the single place this number lives in the extension. Matches the release tag
 * (`v<version>`) and `WitcherScriptMerger.Headless.csproj`'s own `<Version>`; bump it
 * alongside a new WSM release once that release's assets are published.
 */
export const DEFAULT_WSM_VERSION = '0.8.0';

/**
 * Windows-only for now, matching Vortex itself being Windows-only today (see
 * `docs/vortex-extension-design.md`, Open Question 8) - not a hardcoded assumption
 * baked in silently, just the only platform this extension can actually run on right
 * now. A future Linux/SteamOS Vortex would need a second case here, not a rewrite.
 */
export type WsmAssetPlatform = 'win-x64';

/**
 * The Headless host (CLI + MCP, no GUI) rather than the WinForms host's own win-x64
 * asset: `WitcherScriptMerger.Headless/CLAUDE.md`'s "Dependency gating" section.
 *
 * The WinForms host's **`mcp` verb** gates on the combined `ValidateDependencyPaths()`
 * (QuickBMS + wcc_lite), so it refuses to even start a server without bundle tooling
 * this extension doesn't acquire - and MCP is precisely how this extension drives WSM,
 * so that alone settles the choice. Its `merge` verb no longer does (it gates on
 * `ValidateTextMergeDependencies()` like Headless); this comment previously said both
 * verbs did, which stopped being true once that was fixed. The conclusion is unchanged,
 * and if anything stronger: the one surface this extension actually uses is still gated.
 *
 * The Headless host gates on `ValidateTextMergeDependencies()` for both verbs, so it
 * works for flat-file conflicts with nothing else installed - exactly what
 * `mcpClient.ts` needs.
 */
export function buildAssetFileName(version: string, platform: WsmAssetPlatform = 'win-x64'): string {
  return `WitcherScriptMerger.Headless-${version}-${platform}.zip`;
}

export interface GitHubReleaseAsset {
  name: string;
  browser_download_url: string;
  size: number;
}

interface GitHubReleaseResponse {
  tag_name: string;
  assets: GitHubReleaseAsset[];
}

/**
 * Low-level HTTP transport, injectable for testing. `nodeHttpsClient` (below) is the
 * real, default implementation; every test in `githubRelease.test.ts` supplies its own
 * fake instead, so no test ever makes a real network call.
 */
export interface HttpClient {
  /** GETs `url`, follows redirects, parses the body as JSON. Rejects on a non-2xx final
   *  status or invalid JSON. */
  getJson(url: string): Promise<unknown>;
  /** GETs `url`, follows redirects, streams the body to `destPath`. Rejects on a
   *  non-2xx final status or any I/O error; resolves with the number of bytes written. */
  downloadToFile(url: string, destPath: string): Promise<{ bytesWritten: number }>;
}

const USER_AGENT = 'witcherscriptmerger-vortex';
const MAX_REDIRECTS = 5;
/** Applies to each individual request in a redirect chain (connect-through-response-headers),
 *  not the whole chain/download - a slow-but-progressing large-asset download isn't cut off
 *  by this, only a connection that is accepted but never sends anything back at all. */
const REQUEST_TIMEOUT_MS = 30_000;

function requestFollowingRedirects(
  url: string,
  redirectsLeft: number,
): Promise<{ statusCode: number; response: import('http').IncomingMessage }> {
  return new Promise((resolve, reject) => {
    const req = https.get(
      url,
      {
        headers: { 'User-Agent': USER_AGENT, Accept: 'application/vnd.github+json, application/octet-stream' },
        timeout: REQUEST_TIMEOUT_MS,
      },
      (response) => {
        // A throw in here would otherwise escape as an uncaught exception rather than a
        // Promise rejection - this callback runs asynchronously, outside the executor's
        // own synchronous try/catch, so `new Promise` cannot catch it for us.
        try {
          const statusCode = response.statusCode ?? 0;

          if (statusCode >= 300 && statusCode < 400 && response.headers.location) {
            response.resume(); // discard this response's body before following the redirect
            if (redirectsLeft <= 0) {
              reject(new Error(`Too many redirects while fetching '${url}'.`));
              return;
            }
            resolve(requestFollowingRedirects(new URL(response.headers.location, url).toString(), redirectsLeft - 1));
            return;
          }

          resolve({ statusCode, response });
        } catch (err) {
          response.resume();
          reject(err);
        }
      },
    );
    req.on('error', reject);
    // 'timeout' alone doesn't abort the request or reject anything by itself - per Node's
    // own docs, it only fires; the caller is expected to act on it (here, destroying the
    // request, which then emits 'error' and reaches the handler above).
    req.on('timeout', () => {
      req.destroy(new Error(`Request to '${url}' timed out after ${REQUEST_TIMEOUT_MS}ms.`));
    });
  });
}

async function nodeGetJson(url: string): Promise<unknown> {
  const { statusCode, response } = await requestFollowingRedirects(url, MAX_REDIRECTS);

  const chunks: Buffer[] = [];
  for await (const chunk of response) {
    chunks.push(chunk as Buffer);
  }
  const body = Buffer.concat(chunks).toString('utf8');

  if (statusCode < 200 || statusCode >= 300) {
    throw new Error(`GET '${url}' failed with HTTP ${statusCode}: ${body.slice(0, 500)}`);
  }

  try {
    return JSON.parse(body);
  } catch (err) {
    throw new Error(
      `GET '${url}' returned a non-JSON body (HTTP ${statusCode}): ${err instanceof Error ? err.message : String(err)}`,
    );
  }
}

async function nodeDownloadToFile(url: string, destPath: string): Promise<{ bytesWritten: number }> {
  const { statusCode, response } = await requestFollowingRedirects(url, MAX_REDIRECTS);

  if (statusCode < 200 || statusCode >= 300) {
    response.resume();
    throw new Error(`Download of '${url}' failed with HTTP ${statusCode}.`);
  }

  let bytesWritten = 0;
  await new Promise<void>((resolve, reject) => {
    const out = fs.createWriteStream(destPath);

    // Node's pipe() does not auto-destroy the destination when the source errors (or vice
    // versa) - without this, a failed download leaks an open file handle on `out` on top
    // of leaving a partial file behind.
    const onError = (err: Error) => {
      out.destroy();
      response.destroy();
      reject(err);
    };

    response.on('data', (chunk: Buffer) => {
      bytesWritten += chunk.length;
    });
    response.on('error', onError);
    out.on('error', onError);
    // 'finish' fires once all data has been flushed to the stream's internal buffer, but
    // does *not* guarantee the underlying file descriptor has actually been closed yet
    // (per Node's own docs, 'close' is the event that guarantees that) - resolving on
    // 'finish' left a real, if narrow, window where a caller that immediately re-opens
    // destPath (e.g. archiveExtractor.ts extracting it right after) could race an
    // OS-level handle that isn't released yet, particularly on Windows.
    out.on('close', resolve);
    response.pipe(out);
  });

  return { bytesWritten };
}

/** The real transport - Node's own `https`, no third-party HTTP dependency. */
export const nodeHttpsClient: HttpClient = {
  getJson: nodeGetJson,
  downloadToFile: nodeDownloadToFile,
};

export interface ResolveReleaseAssetOptions {
  /** `"owner/repo"`, e.g. `DEFAULT_WSM_REPO`. */
  repo: string;
  /** A real release tag, e.g. `"v0.6.2"` (including the leading "v" - matches
   *  `.github/workflows/release.yml`'s own `push: tags: - 'v*'` trigger). */
  tag: string;
  assetFileName: string;
  client?: HttpClient;
}

export interface ResolvedReleaseAsset {
  downloadUrl: string;
  size: number;
}

/**
 * Looks up a release by tag and finds the asset matching `assetFileName` by exact
 * name. Throws a clear, specific error (naming the repo/tag/asset actually looked for)
 * if the release or the asset within it doesn't exist - the expected outcome against
 * this repo today, since no release has been tagged yet.
 */
export async function resolveReleaseAsset(options: ResolveReleaseAssetOptions): Promise<ResolvedReleaseAsset> {
  const client = options.client ?? nodeHttpsClient;
  const url = `https://api.github.com/repos/${options.repo}/releases/tags/${options.tag}`;

  let release: GitHubReleaseResponse;
  try {
    release = (await client.getJson(url)) as GitHubReleaseResponse;
  } catch (err) {
    throw new Error(
      `Could not fetch release '${options.tag}' for '${options.repo}' (${url}): ${err instanceof Error ? err.message : String(err)}`,
    );
  }

  const asset = (release.assets ?? []).find((a) => a.name === options.assetFileName);
  if (!asset) {
    const available = (release.assets ?? []).map((a) => a.name).join(', ') || '(none)';
    throw new Error(
      `Release '${options.tag}' for '${options.repo}' has no asset named '${options.assetFileName}'. Available assets: ${available}.`,
    );
  }

  return { downloadUrl: asset.browser_download_url, size: asset.size };
}

export interface DownloadReleaseAssetOptions {
  downloadUrl: string;
  destPath: string;
  /** The size GitHub's API reported for this asset (`ResolvedReleaseAsset.size`).
   *  Compared against the actual downloaded byte count as this download's only
   *  integrity check - `release.yml` publishes no checksum manifest, so this is a
   *  transfer-completeness check, not a cryptographic verification. */
  expectedSize: number;
  client?: HttpClient;
}

/**
 * Downloads a release asset to `destPath` and verifies the downloaded byte count
 * matches the size GitHub's API reported for it. Throws if they don't match (a
 * truncated/corrupted download) rather than silently handing a caller a bad file - and
 * removes the bad file from `destPath` first (best-effort - a failure removing it is
 * logged nowhere and swallowed, since the size-mismatch error is the one that actually
 * matters to the caller), rather than leaving corrupt/truncated debris behind in what's
 * meant to be a disposable download cache (`storage.ts`'s `getDownloadCacheDir`).
 */
export async function downloadReleaseAsset(options: DownloadReleaseAssetOptions): Promise<void> {
  const client = options.client ?? nodeHttpsClient;
  const { bytesWritten } = await client.downloadToFile(options.downloadUrl, options.destPath);

  if (bytesWritten !== options.expectedSize) {
    try {
      await fs.promises.unlink(options.destPath);
    } catch {
      // Best-effort cleanup only - the error below is what actually matters here.
    }

    throw new Error(
      `Downloaded '${options.downloadUrl}' to '${options.destPath}' but got ${bytesWritten} bytes, expected ${options.expectedSize} (GitHub's reported asset size). The download may be incomplete or corrupted.`,
    );
  }
}

import { spawn, ChildProcessWithoutNullStreams } from 'child_process';

/**
 * MCP stdio client for WitcherScriptMerger (WSM)'s `mcp` server mode.
 *
 * WSM exposes a standard MCP (Model Context Protocol) server over stdio when launched as
 * `<wsm-exe> mcp` (see `WitcherScriptMerger.Core/Mcp/CLAUDE.md` and
 * `WitcherScriptMerger.Core/CLAUDE.md`'s "CLI & MCP orchestration" section in the .NET
 * side of this repo). This module is a hand-rolled client for that transport - deliberately
 * not going through `@nexusmods/vortex-api`'s `runExecutable`, whose `IRunOptions` (verified
 * against the published package's own typings) exposes no stdio/pipe access at all and so
 * cannot carry MCP's JSON-RPC frames.
 *
 * Framing: MCP's stdio transport is newline-delimited JSON-RPC 2.0 - one complete,
 * embedded-newline-free JSON object per line, UTF-8 encoded (confirmed against the current
 * MCP specification; this is *not* LSP-style `Content-Length`-prefixed framing). WSM's
 * server routes all of its own logging to stderr, so stdout carries protocol frames only.
 *
 * Process lifecycle policy: **spawn per user-initiated workflow, tear down when the caller
 * is done with it** - this class is not a long-lived singleton, and callers should not try
 * to make it one. Every WSM MCP tool call already re-scans the mods folder and re-loads
 * `MergeInventory.xml` from disk server-side (documented in
 * `WitcherScriptMerger.Core/CLAUDE.md`), so keeping a WSM process alive across unrelated
 * workflows would only save the `initialize` handshake's cost - not worth the added
 * crash/restart/orphaned-process bookkeeping for a v1 client. Each later unit (conflict
 * scanning, the merge panel, a merge-history dashlet, a status tile) should call
 * `WsmMcpClient.connect(...)` for its own short-lived operation and `close()` it in a
 * `finally` block, the same way the integration test in `test/mcpClient.integration.test.ts`
 * does.
 */

/** The MCP protocol version this client requests during the `initialize` handshake. */
const MCP_PROTOCOL_VERSION = '2025-06-18';

const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;

/** How much of the child process's stderr to retain for diagnostics on failure. */
const STDERR_TAIL_LIMIT = 4000;

export interface WsmMcpClientOptions {
  /**
   * Absolute path to a WSM executable capable of `mcp` mode (either
   * `WitcherScriptMerger.exe`, the full WinForms build, or
   * `WitcherScriptMerger.Headless.exe`, the CLI/MCP-only build). Resolving *which* binary
   * to use, and where it lives on disk, is a later unit's (tool acquisition) job - this
   * client just spawns whatever path it's given.
   */
  exePath: string;
  /** Arguments to pass to `exePath`. Defaults to `['mcp']`. */
  args?: string[];
  /**
   * Working directory for the spawned process. Note: WSM itself pins its own
   * `Environment.CurrentDirectory` to its own executable's directory as the first thing
   * it does on startup (see `WitcherScriptMerger.Headless/CLAUDE.md`), so this has no
   * effect on which `App.config`/`MergeInventory.xml`/etc. WSM resolves - it's accepted
   * here only for ordinary process-spawning hygiene (e.g. tests spawning a scratch copy
   * of the exe), not as a way to steer WSM's own config resolution.
   */
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  /** Timeout for each individual JSON-RPC request, including the initial handshake. */
  requestTimeoutMs?: number;
}

export interface McpToolDescriptor {
  name: string;
  description?: string;
  inputSchema?: unknown;
}

export interface ScannedConflictMod {
  name: string;
  hash: string;
  isOutdated: boolean;
}

export interface ScannedConflict {
  relativePath: string;
  category: string;
  mods: ScannedConflictMod[];
  defaultOrder: string[];
  alreadyResolved: boolean;
}

export type ScanConflictsResult = ScannedConflict[];

export interface MergeConflictsArgs {
  relativePaths?: string[];
  orderOverrides?: Record<string, string[]>;
  dryRun?: boolean;
}

export interface MergeConflictsResult {
  merged: string[];
  skipped: string[];
  unmatched: string[];
  dryRun: boolean;
}

export interface GetStatusResult {
  gameDirectory: string;
  modsDirectory: string;
  dependenciesValid: boolean;
  textMergeDependenciesValid: boolean;
  bundleDependenciesValid: boolean;
  modsDirectoryExists: boolean;
  mergedModName: string;
  conflictCount: number;
}

export interface RecordedMergeMod {
  name: string;
  hash: string;
}

export interface RecordedMerge {
  relativePath: string;
  mergedModName: string;
  mods: RecordedMergeMod[];
}

export type ListMergesResult = RecordedMerge[];

/** Thrown when the WSM process exits (or fails to start) while a request is in flight,
 *  or before `connect()` completes its handshake. */
export class WsmMcpProcessError extends Error {
  constructor(
    message: string,
    public readonly exitCode: number | null,
    public readonly stderrTail: string,
  ) {
    super(message);
    this.name = 'WsmMcpProcessError';
  }
}

/** Thrown when a tool call's `result.isError` is true, or the result shape is not one
 *  this client knows how to interpret. */
export class WsmMcpToolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'WsmMcpToolError';
  }
}

interface JsonRpcRequest {
  jsonrpc: '2.0';
  id: number;
  method: string;
  params?: unknown;
}

interface JsonRpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: unknown;
}

interface JsonRpcSuccess {
  jsonrpc: '2.0';
  id: number;
  result: unknown;
}

interface JsonRpcFailure {
  jsonrpc: '2.0';
  id: number;
  error: { code: number; message: string; data?: unknown };
}

type JsonRpcResponse = JsonRpcSuccess | JsonRpcFailure;

function isJsonRpcResponse(value: unknown): value is JsonRpcResponse {
  return (
    typeof value === 'object' &&
    value !== null &&
    'id' in value &&
    ('result' in value || 'error' in value)
  );
}

interface McpContentItem {
  type: string;
  text?: string;
  [key: string]: unknown;
}

interface McpToolCallResult {
  content?: McpContentItem[];
  structuredContent?: unknown;
  isError?: boolean;
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class WsmMcpClient {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly requestTimeoutMs: number;
  private nextId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private stdoutBuffer = '';
  private stderrTail = '';
  private closed = false;
  private exitInfo: { code: number | null } | null = null;

  private constructor(child: ChildProcessWithoutNullStreams, requestTimeoutMs: number) {
    this.child = child;
    this.requestTimeoutMs = requestTimeoutMs;

    this.child.stdout.setEncoding('utf8');
    this.child.stdout.on('data', (chunk: string) => this.onStdoutData(chunk));

    this.child.stderr.setEncoding('utf8');
    this.child.stderr.on('data', (chunk: string) => {
      this.stderrTail = (this.stderrTail + chunk).slice(-STDERR_TAIL_LIMIT);
    });

    // A write can race a process that has already exited or whose stdin has already been
    // closed (e.g. connect() writing the 'initialize' request to a WSM process that exits
    // immediately - AppSettings' Environment.Exit(1) when App.config is missing, per
    // WitcherScriptMerger.Core/CLAUDE.md). Without this listener, Node treats an
    // unhandled 'error' on a Writable stream as fatal and crashes the *host* process
    // (Vortex itself) instead of surfacing it through the normal request-rejection path
    // below.
    this.child.stdin.on('error', (err) => {
      this.failAllPending(
        new WsmMcpProcessError(
          `WSM MCP process stdin error: ${err.message}`,
          this.exitInfo?.code ?? null,
          this.stderrTail,
        ),
      );
    });

    this.child.on('exit', (code) => {
      this.exitInfo = { code };
      this.failAllPending(
        new WsmMcpProcessError(
          `WSM MCP process exited unexpectedly (code ${code ?? 'null'})`,
          code,
          this.stderrTail,
        ),
      );
    });

    this.child.on('error', (err) => {
      this.failAllPending(
        new WsmMcpProcessError(
          `WSM MCP process could not be spawned or crashed: ${err.message}`,
          null,
          this.stderrTail,
        ),
      );
    });
  }

  /**
   * Spawns `options.exePath` in `mcp` mode and performs the MCP `initialize` handshake
   * (an `initialize` request, followed by a `notifications/initialized` notification once
   * the server responds). Resolves with a ready-to-use client; on any failure, the spawned
   * process (if any) is killed before rejecting - no leaked child process on a failed
   * connect.
   */
  static async connect(options: WsmMcpClientOptions): Promise<WsmMcpClient> {
    const requestTimeoutMs = options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
    const args = options.args ?? ['mcp'];

    const child = spawn(options.exePath, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: ['pipe', 'pipe', 'pipe'],
    }) as ChildProcessWithoutNullStreams;

    const client = new WsmMcpClient(child, requestTimeoutMs);

    try {
      await client.request('initialize', {
        protocolVersion: MCP_PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: 'witcherscriptmerger-vortex', version: '0.1.0' },
      });
      // No response expected for a notification - the server transitions into normal
      // operation once it sees this, per the MCP lifecycle.
      client.notify('notifications/initialized');
    } catch (err) {
      client.killImmediately();
      throw err;
    }

    return client;
  }

  async listTools(): Promise<McpToolDescriptor[]> {
    const result = (await this.request('tools/list', {})) as { tools?: McpToolDescriptor[] };
    return result.tools ?? [];
  }

  async callTool<T = unknown>(name: string, args?: Record<string, unknown>): Promise<T> {
    const result = (await this.request('tools/call', {
      name,
      arguments: args ?? {},
    })) as McpToolCallResult;

    if (result.isError) {
      const message = result.content?.find((c) => c.type === 'text')?.text ?? `Tool '${name}' reported an error.`;
      throw new WsmMcpToolError(message);
    }

    // The MCP spec (2025-06-18+) lets a tool return pre-parsed `structuredContent`
    // alongside/instead of text content; prefer it when present. Otherwise, fall back to
    // parsing the first text content block as JSON - WSM's tools all return plain JSON
    // objects/arrays server-side (see WitcherScriptMerger.Core/Mcp/WsmMcpTools.cs), so one
    // of these two paths should always apply. Which one the C# ModelContextProtocol SDK
    // actually takes for this server was left genuinely unverified until exercised by the
    // real integration test - see that test file and this unit's PR description for what
    // was actually observed.
    if (result.structuredContent !== undefined) {
      return result.structuredContent as T;
    }

    const textItem = result.content?.find((c) => c.type === 'text' && typeof c.text === 'string');
    if (textItem?.text !== undefined) {
      return JSON.parse(textItem.text) as T;
    }

    throw new WsmMcpToolError(
      `Tool '${name}' returned a result with neither structuredContent nor text content: ${JSON.stringify(result)}`,
    );
  }

  scanConflicts(): Promise<ScanConflictsResult> {
    return this.callTool<ScanConflictsResult>('scan_conflicts');
  }

  mergeConflicts(args?: MergeConflictsArgs): Promise<MergeConflictsResult> {
    return this.callTool<MergeConflictsResult>('merge_conflicts', args as Record<string, unknown> | undefined);
  }

  getStatus(): Promise<GetStatusResult> {
    return this.callTool<GetStatusResult>('get_status');
  }

  listMerges(): Promise<ListMergesResult> {
    return this.callTool<ListMergesResult>('list_merges');
  }

  /** Ends stdin, gives the process a short grace period to exit on its own, then force
   *  kills it if it hasn't. Safe to call more than once. */
  async close(): Promise<void> {
    if (this.closed) {
      return;
    }
    this.closed = true;

    this.failAllPending(new WsmMcpProcessError('WSM MCP client was closed', null, this.stderrTail));

    if (this.exitInfo !== null) {
      return;
    }

    await new Promise<void>((resolve) => {
      const onExit = () => resolve();
      this.child.once('exit', onExit);

      const forceKillTimer = setTimeout(() => {
        this.child.off('exit', onExit);
        this.child.kill();
        resolve();
      }, 2000);
      this.child.once('exit', () => clearTimeout(forceKillTimer));

      try {
        this.child.stdin.end();
      } catch {
        // Process may already be gone - the exit/timeout handling above still resolves.
      }
    });
  }

  private killImmediately(): void {
    try {
      this.child.kill();
    } catch {
      // Already dead - nothing to do.
    }
  }

  private request(method: string, params?: unknown): Promise<unknown> {
    const id = this.nextId++;
    const message: JsonRpcRequest = { jsonrpc: '2.0', id, method, params };

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`WSM MCP request '${method}' timed out after ${this.requestTimeoutMs}ms`));
      }, this.requestTimeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      this.writeMessage(message);
    });
  }

  private notify(method: string, params?: unknown): void {
    const message: JsonRpcNotification = { jsonrpc: '2.0', method, params };
    this.writeMessage(message);
  }

  private writeMessage(message: JsonRpcRequest | JsonRpcNotification): void {
    try {
      this.child.stdin.write(JSON.stringify(message) + '\n');
    } catch (err) {
      // Belt-and-suspenders alongside the stdin 'error' listener above - some Node
      // versions throw synchronously (ERR_STREAM_WRITE_AFTER_END) instead of emitting
      // 'error' for a write after stdin has already ended.
      const errorMessage = err instanceof Error ? err.message : String(err);
      this.failAllPending(
        new WsmMcpProcessError(`WSM MCP process stdin write failed: ${errorMessage}`, this.exitInfo?.code ?? null, this.stderrTail),
      );
    }
  }

  private onStdoutData(chunk: string): void {
    this.stdoutBuffer += chunk;

    let newlineIndex: number;
    while ((newlineIndex = this.stdoutBuffer.indexOf('\n')) !== -1) {
      const line = this.stdoutBuffer.slice(0, newlineIndex);
      this.stdoutBuffer = this.stdoutBuffer.slice(newlineIndex + 1);

      const trimmed = line.trim();
      if (trimmed.length === 0) {
        continue;
      }

      this.handleLine(trimmed);
    }
  }

  private handleLine(line: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      // Not a valid MCP frame - the spec requires the server to never write anything
      // else to stdout, but don't let one malformed line take down every in-flight
      // request over an otherwise-healthy connection.
      return;
    }

    if (!isJsonRpcResponse(parsed)) {
      // A request or notification *from* the server - WSM's server doesn't send any
      // today, so there's nothing to route it to yet; ignore rather than throw.
      return;
    }

    const pending = this.pending.get(parsed.id);
    if (!pending) {
      return;
    }

    this.pending.delete(parsed.id);
    clearTimeout(pending.timer);

    if ('error' in parsed) {
      pending.reject(new Error(`WSM MCP error ${parsed.error.code}: ${parsed.error.message}`));
    } else {
      pending.resolve(parsed.result);
    }
  }

  private failAllPending(error: unknown): void {
    for (const [id, pending] of this.pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
      this.pending.delete(id);
    }
  }
}

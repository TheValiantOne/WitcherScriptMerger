import { EventEmitter } from 'events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Mocked before the module under test is imported, so WsmMcpClient.connect spawns this
// fake instead of a real WSM process. The integration suite (test/mcpClient.integration.test.ts)
// covers the real binary; this file covers only the request-deadline plumbing, which
// needs no process at all and would otherwise be untestable without one.
const { spawnMock } = vi.hoisted(() => ({ spawnMock: vi.fn() }));
vi.mock('child_process', () => ({ spawn: spawnMock }));

const { WsmMcpClient, NO_REQUEST_TIMEOUT } = await import('./mcpClient');

/** A newline-delimited-JSON stand-in for a spawned WSM `mcp` process. Answers
 *  `initialize` (so connect() resolves) and, by default, nothing else - leaving a
 *  `tools/call` pending forever so a test can drive it purely off the request timer. */
function fakeChild() {
  const stdout = new EventEmitter() as EventEmitter & { setEncoding(enc: string): void };
  stdout.setEncoding = () => undefined;
  const stderr = new EventEmitter() as EventEmitter & { setEncoding(enc: string): void };
  stderr.setEncoding = () => undefined;

  const written: string[] = [];
  const stdin = Object.assign(new EventEmitter(), {
    write: (chunk: string) => {
      written.push(chunk);
      for (const line of chunk.split('\n').filter((l) => l.trim() !== '')) {
        const msg = JSON.parse(line) as { id?: number; method?: string };
        // Answer only the handshake; every other request is left hanging on purpose.
        // Emitted synchronously rather than via queueMicrotask/setTimeout: vi.useFakeTimers
        // controls both of those, and the reply has to land without any timer being
        // advanced (advancing time is exactly what these tests use to trigger the
        // deadlines under test). Safe because request() registers its pending entry
        // before calling writeMessage, so the response can never arrive "too early".
        if (msg.method === 'initialize' && msg.id !== undefined) {
          stdout.emit('data', JSON.stringify({ jsonrpc: '2.0', id: msg.id, result: {} }) + '\n');
        }
      }
      return true;
    },
    end: () => undefined,
  });

  const child = Object.assign(new EventEmitter(), {
    stdout,
    stderr,
    stdin,
    kill: vi.fn(),
    killed: false,
    pid: 1234,
  });

  return { child, written };
}

/** Tracks settlement without letting an expected rejection escape as unhandled. */
function track<T>(p: Promise<T>) {
  const state = { settled: false, rejected: false, error: undefined as unknown };
  p.then(
    () => { state.settled = true; },
    (err) => { state.settled = true; state.rejected = true; state.error = err; },
  );
  return state;
}

describe('WsmMcpClient request deadlines', () => {
  beforeEach(() => {
    spawnMock.mockReset();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function connectFake(requestTimeoutMs?: number) {
    const { child, written } = fakeChild();
    spawnMock.mockReturnValue(child);
    const client = await WsmMcpClient.connect({ exePath: 'C:\\wsm\\fake.exe', requestTimeoutMs });
    return { client, child, written };
  }

  // The bug this whole change exists for: merge_conflicts used to inherit the 30s
  // general-purpose default, and a big load order's merge (or its equally expensive
  // dry-run preview) blew straight through it. A merge now gets no deadline at all.
  it('never times out a call made with NO_REQUEST_TIMEOUT, however long it runs', async () => {
    const { client } = await connectFake(30_000);

    const state = track(client.mergeConflicts({ dryRun: true }, NO_REQUEST_TIMEOUT));

    // an hour of wall clock - far past the 30s client default
    await vi.advanceTimersByTimeAsync(60 * 60 * 1000);

    expect(state.settled).toBe(false);
  });

  // Liveness, not the clock, is what makes an unbounded wait safe: if WSM dies the
  // request must still reject promptly rather than hang forever.
  it('still rejects an unbounded call when the WSM process exits', async () => {
    const { client, child } = await connectFake(30_000);

    const pending = client.mergeConflicts({ dryRun: true }, NO_REQUEST_TIMEOUT);
    const assertion = expect(pending).rejects.toThrow(/exited unexpectedly \(code 1\)/);

    child.emit('exit', 1);

    await assertion;
  });

  it('still rejects an unbounded call when the WSM process fails at the pipe level', async () => {
    const { client, child } = await connectFake(30_000);

    const pending = client.mergeConflicts({ dryRun: true }, NO_REQUEST_TIMEOUT);
    const assertion = expect(pending).rejects.toThrow(/stdin error: broken pipe/);

    child.stdin.emit('error', new Error('broken pipe'));

    await assertion;
  });

  it('falls back to the client default when no override is given', async () => {
    const { client } = await connectFake(30_000);

    const pending = client.getStatus();
    const assertion = expect(pending).rejects.toThrow(/timed out after 30000ms/);

    await vi.advanceTimersByTimeAsync(30_000);

    await assertion;
  });

  // The unbounded wait must stay scoped to the one call. If it leaked onto the client it
  // would also cover the initialize handshake, and a WSM process that fails to start would
  // hang forever instead of failing fast.
  it('does not let an unbounded call stop later calls on the same client from timing out', async () => {
    const { client } = await connectFake(30_000);

    const unbounded = track(client.mergeConflicts({ dryRun: true }, NO_REQUEST_TIMEOUT));

    const short = client.getStatus();
    const shortAssertion = expect(short).rejects.toThrow(/timed out after 30000ms/);

    await vi.advanceTimersByTimeAsync(30_000);
    await shortAssertion;

    expect(unbounded.settled).toBe(false);
  });

  it('uses the client default for the initialize handshake', async () => {
    const { child } = fakeChild();
    // Swallow the handshake so it can only ever end in a timeout.
    child.stdin.write = () => true;
    spawnMock.mockReturnValue(child);

    const connecting = WsmMcpClient.connect({ exePath: 'C:\\wsm\\fake.exe', requestTimeoutMs: 30_000 });
    const assertion = expect(connecting).rejects.toThrow(/'initialize' timed out after 30000ms/);

    await vi.advanceTimersByTimeAsync(30_000);

    await assertion;
  });
});

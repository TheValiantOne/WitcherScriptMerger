import { EventEmitter } from 'events';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Mocked before the module under test is imported, so WsmMcpClient.connect spawns this
// fake instead of a real WSM process. The integration suite (test/mcpClient.integration.test.ts)
// covers the real binary; this file covers only the request-deadline plumbing, which
// needs no process at all and would otherwise be untestable without one.
const { spawnMock } = vi.hoisted(() => ({ spawnMock: vi.fn() }));
vi.mock('child_process', () => ({ spawn: spawnMock }));

const { WsmMcpClient } = await import('./mcpClient');

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
    return { client: await WsmMcpClient.connect({ exePath: 'C:\\wsm\\fake.exe', requestTimeoutMs }), written };
  }

  // The bug this whole change exists for: merge_conflicts used to inherit the 30s
  // general-purpose default, and a big load order's merge (or its equally expensive
  // dry-run preview) blew straight through it.
  it('honors a per-call timeout override instead of the client default', async () => {
    const { client } = await connectFake(30_000);

    const pending = client.mergeConflicts({ dryRun: true }, 600_000);
    const assertion = expect(pending).rejects.toThrow(/timed out after 600000ms/);

    // Well past the 30s client default - must NOT have rejected yet.
    await vi.advanceTimersByTimeAsync(120_000);
    // ...and now past the override.
    await vi.advanceTimersByTimeAsync(600_000);

    await assertion;
  });

  it('falls back to the client default when no override is given', async () => {
    const { client } = await connectFake(30_000);

    const pending = client.getStatus();
    const assertion = expect(pending).rejects.toThrow(/timed out after 30000ms/);

    await vi.advanceTimersByTimeAsync(30_000);

    await assertion;
  });

  // The override must stay scoped to the one call. If it leaked onto the client it would
  // also bound the initialize handshake, and a WSM process that fails to start would hang
  // for the full merge-sized deadline instead of failing fast.
  it('does not let a per-call override leak into later calls on the same client', async () => {
    const { client } = await connectFake(30_000);

    const long = client.mergeConflicts({ dryRun: true }, 600_000);
    const longAssertion = expect(long).rejects.toThrow(/timed out after 600000ms/);

    const short = client.getStatus();
    const shortAssertion = expect(short).rejects.toThrow(/timed out after 30000ms/);

    await vi.advanceTimersByTimeAsync(30_000);
    await shortAssertion;

    await vi.advanceTimersByTimeAsync(600_000);
    await longAssertion;
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

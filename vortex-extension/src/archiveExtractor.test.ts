import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { createVortexArchiveExtractor } from './archiveExtractor';

// This only tests createVortexArchiveExtractor's own glue (destDir creation, delegating
// to api.openArchive/archive.extractAll, error handling when extractAll is missing) -
// the real archive-handler behavior behind api.openArchive is Vortex's own, and isn't
// something this repo can exercise without a real Vortex host (see archiveExtractor.ts's
// own doc comment).
function fakeApi(openArchive: (archivePath: string, options?: unknown) => Promise<{ extractAll?: (dest: string) => Promise<void> }>) {
  return { openArchive } as unknown as Parameters<typeof createVortexArchiveExtractor>[0];
}

describe('createVortexArchiveExtractor', () => {
  let scratchDir: string;

  beforeEach(() => {
    scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'wsm-vortex-extract-test-'));
  });

  afterEach(() => {
    fs.rmSync(scratchDir, { recursive: true, force: true });
  });

  it('creates destDir, opens the archive, and calls extractAll(destDir)', async () => {
    const destDir = path.join(scratchDir, 'nested', 'destination');
    const extractAllCalls: string[] = [];
    let openArchiveCall: { archivePath: string; options: unknown } | undefined;

    const extractor = createVortexArchiveExtractor(
      fakeApi(async (archivePath, options) => {
        openArchiveCall = { archivePath, options };
        return {
          extractAll: async (dest: string) => {
            extractAllCalls.push(dest);
          },
        };
      }),
    );

    await extractor.extractAll('C:\\fake\\asset.zip', destDir);

    expect(fs.existsSync(destDir)).toBe(true);
    expect(openArchiveCall?.archivePath).toBe('C:\\fake\\asset.zip');
    expect(extractAllCalls).toEqual([destDir]);
  });

  it('throws a clear error when the opened archive has no extractAll', async () => {
    const destDir = path.join(scratchDir, 'destination');
    const extractor = createVortexArchiveExtractor(fakeApi(async () => ({})));

    await expect(extractor.extractAll('C:\\fake\\asset.zip', destDir)).rejects.toThrow(/does not support/);
  });
});

import * as fs from 'fs';
import { types } from 'vortex-api';

/**
 * Extracts a downloaded WSM release archive. Deliberately goes through Vortex's own
 * `api.openArchive`/`Archive.extractAll` (`@nexusmods/vortex-api`'s own `lib/api.d.ts`
 * documents `openArchive(archivePath, options?, extension?): Promise<Archive>` with an
 * `extractAll(outputPath): Promise<void>` member, backed by whatever archive-handler
 * extension Vortex has registered for the file's format) rather than a hand-rolled zip
 * reader or a new npm dependency - this is the idiomatic mechanism a Vortex extension
 * already has for exactly this job.
 *
 * Behind a one-function interface (`ArchiveExtractor`) so `toolAcquisition.ts` stays
 * unit-testable with extraction stubbed - this real implementation is never exercised
 * by any test in this repo, since doing so would need a real Vortex host providing a
 * real archive-handler extension (`api.openArchive` has no meaningful behavior outside
 * one). See this unit's PR description for what was/wasn't verified.
 */
export interface ArchiveExtractor {
  extractAll(archivePath: string, destDir: string): Promise<void>;
}

export function createVortexArchiveExtractor(api: types.IExtensionApi): ArchiveExtractor {
  return {
    async extractAll(archivePath: string, destDir: string): Promise<void> {
      await fs.promises.mkdir(destDir, { recursive: true });

      // verify: true requests whatever integrity check Vortex's own archive handler
      // supports (a CRC pass, or possibly nothing, depending on the handler) - its exact
      // behavior is unverified here, like the rest of this real implementation (see this
      // function's own doc comment above).
      const archive = await api.openArchive(archivePath, { verify: true });
      if (!archive.extractAll) {
        throw new Error(
          `Vortex's archive handler for '${archivePath}' does not support extracting the whole archive (extractAll is undefined).`,
        );
      }
      await archive.extractAll(destDir);
    },
  };
}

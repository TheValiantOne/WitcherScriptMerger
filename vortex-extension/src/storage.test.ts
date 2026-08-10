import * as path from 'path';
import { describe, expect, it } from 'vitest';
import { getBundleToolsDir, getDownloadCacheDir, getExtensionStorageDir, getWsmToolDir } from './storage';

// A minimal stand-in for IExtensionApi - only getPath is used by storage.ts, matching
// gating.test.ts's own fakeApi pattern for the same reason.
function fakeApi(userDataDir: string) {
  return {
    getPath: (name: string) => (name === 'userData' ? userDataDir : `/unexpected/${name}`),
  } as unknown as Parameters<typeof getExtensionStorageDir>[0];
}

describe('storage', () => {
  const api = fakeApi(path.join('C:', 'fake', 'userData'));

  it('roots the extension storage dir under userData with a recognizable name', () => {
    expect(getExtensionStorageDir(api)).toBe(path.join('C:', 'fake', 'userData', 'witcherscriptmerger-vortex'));
  });

  it('gives each subdirectory a distinct, stable path under the storage root', () => {
    const root = getExtensionStorageDir(api);
    expect(getWsmToolDir(api)).toBe(path.join(root, 'tool'));
    expect(getDownloadCacheDir(api)).toBe(path.join(root, 'downloads'));
    expect(getBundleToolsDir(api)).toBe(path.join(root, 'bundle-tools'));
  });

  it('never collides two subdirectories on the same path', () => {
    const dirs = [getWsmToolDir(api), getDownloadCacheDir(api), getBundleToolsDir(api)];
    expect(new Set(dirs).size).toBe(dirs.length);
  });
});

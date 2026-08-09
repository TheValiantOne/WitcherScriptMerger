import path from 'path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      // See test/testUtils/vortexApiStub.ts - the real 'vortex-api' specifier is only
      // ever resolved by Vortex itself at runtime (or, for typechecking, via
      // tsconfig.json's `paths` alias to @nexusmods/vortex-api). vitest actually
      // executes code, so it needs something real to resolve this bare specifier to.
      'vortex-api': path.resolve(__dirname, 'test/testUtils/vortexApiStub.ts'),
    },
  },
  test: {
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
  },
});

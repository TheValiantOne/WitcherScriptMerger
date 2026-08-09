// Bundles this extension's src/index.ts into a single dist/index.js that Vortex loads
// with a plain Node `require()` (its extension loader is CommonJS-based) - hence
// `libraryTarget: 'commonjs2'` and `target: 'electron-renderer'` below, matching the
// convention used by the (now-archived) Nexus-Mods/vortex-api repo's own bundled
// webpack helper (bin/webpack.js), read directly during this unit's research.
//
// `vortex-api` is never actually installed as a runtime package - the npm package this
// project depends on (`@nexusmods/vortex-api`) is types-only (see tsconfig.json's `paths`
// alias). At runtime, Vortex's own extension loader injects a module resolvable under the
// literal, unscoped specifier `'vortex-api'`. So `vortex-api` itself, `electron`, every
// peer dependency `@nexusmods/vortex-api` declares (react, redux, bluebird, etc. - all
// provided by Vortex's own runtime), and Node's own built-ins must all be excluded from
// the bundle (`externals`) rather than resolved/bundled by webpack - see
// docs/MIGRATION.md inside the `@nexusmods/vortex-api` package for the source of this
// pattern.
const fs = require('fs');
const path = require('path');

// @nexusmods/vortex-api's own package.json declares an "exports" map with only a
// "types" condition (see tsconfig.json's `paths` alias comment) - Node's strict
// exports-map enforcement means even `require('@nexusmods/vortex-api/package.json')`
// is blocked (no './package.json' subpath is exported), so this reads the file
// directly off disk instead of going through module resolution at all.
const vortexApiPackageJsonPath = path.join(
  __dirname,
  'node_modules',
  '@nexusmods',
  'vortex-api',
  'package.json',
);
const { peerDependencies } = JSON.parse(fs.readFileSync(vortexApiPackageJsonPath, 'utf8'));

const nodeBuiltins = ['fs', 'path', 'os', 'child_process', 'net', 'util'];

function asExternals(names) {
  return names.reduce((acc, name) => {
    acc[name] = `commonjs ${name}`;
    return acc;
  }, {});
}

module.exports = {
  mode: process.env.NODE_ENV === 'development' ? 'development' : 'production',
  entry: path.resolve(__dirname, 'src/index.ts'),
  target: 'electron-renderer',
  output: {
    path: path.resolve(__dirname, 'dist'),
    filename: 'index.js',
    libraryTarget: 'commonjs2',
  },
  resolve: {
    extensions: ['.ts', '.tsx', '.js', '.json'],
  },
  module: {
    rules: [
      {
        test: /\.tsx?$/,
        loader: 'ts-loader',
        exclude: /node_modules/,
        options: {
          // tsconfig.json sets noEmit:true so the standalone `tsc --noEmit` typecheck
          // script never writes files - ts-loader needs real emit to hand to webpack,
          // so it's overridden here rather than in the shared tsconfig.
          compilerOptions: { noEmit: false },
        },
      },
    ],
  },
  externals: asExternals([
    'vortex-api',
    'electron',
    ...Object.keys(peerDependencies || {}),
    ...nodeBuiltins,
  ]),
  devtool: 'source-map',
};

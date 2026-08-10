import { types } from 'vortex-api';
import { MergeConflictsResult } from './mcpClient';

/**
 * Dialog-content builders for the "Resolve Script Conflicts" action's two dialogs (a
 * dry-run preview, then the real merge's result) - kept separate from
 * `resolveAction.ts`'s orchestration so the *content* of what gets shown is a plain,
 * synchronous, `WsmMcpClient`-free function this file's own unit tests can exercise
 * directly, without spawning any process or touching Vortex's real dialog system.
 *
 * Deliberately built on `vortex-api`'s own `IDialogContent`/`api.showDialog` (a plain-
 * data dialog Vortex itself renders generically, including a Markdown `md` field, per
 * `lib/api.d.ts`) rather than a custom React component (`MergePanel.tsx`, one example
 * filename this unit's own task description suggested): this project's TypeScript
 * toolchain has no JSX support wired up yet, confirmed directly rather than assumed -
 * `tsconfig.json` sets no `jsx` compiler option, `package.json` has no `@types/react`
 * dependency, and `node_modules/react` (present only indirectly, as a peer dependency
 * `@nexusmods/vortex-api` itself declares) ships no bundled TypeScript declarations of
 * its own. Standing up a whole React/JSX toolchain (a `jsx` pragma, `@types/react`
 * pinned to the exact React 16.14.0 Vortex itself ships, ...) is bigger, separate scope
 * from this unit's actual job - preview, confirm, and surface `functionLevelDecisions`
 * prominently - and `IDialogContent`'s `md` field already renders Markdown natively, so
 * a headed, bulleted audit trail (exactly the "here's what happened and why" shape this
 * feature needs) doesn't need a hand-rolled component to look right. If a later unit
 * needs genuine interactive controls inside the dialog (e.g. per-file checkboxes
 * driving `relativePaths`/`orderOverrides`), that's the point where standing up JSX
 * support for real becomes worth it - not before; see this unit's own PR description
 * for this same call, made explicitly rather than silently.
 */

export interface MergeSummaryDialogOptions {
  /**
   * True for the dry-run preview dialog, false for the real-merge result dialog -
   * changes the heading and the skipped-files wording. A dry run never opens
   * conflict-marker sidecars (`DiffPlexMergeEngine.MergeHeadless`'s
   * `openConflictMarkers` parameter, threaded from `FileMerger.MergeTextHeadless`'s
   * `openConflictMarkers: !dryRun`), so only the non-dry-run wording can honestly say
   * they were opened for review.
   */
  isPreview: boolean;
}

// Escapes characters that could otherwise be misread as Markdown syntax in *plain
// prose* text (the function-level decision lines below - the only caller of this
// function) - e.g. a decision line naming a mod called "mod0000_MergedFiles" would
// otherwise have its underscores rendered as emphasis.
//
// Deliberately NOT used for the backtick-wrapped file paths in pathListSection below,
// even though an earlier version of this file did exactly that: per CommonMark,
// "Backslash escapes do not work in ... code spans" - a code span's content is already
// rendered completely literally by definition, so escaping before wrapping in
// backticks doesn't suppress anything; it just makes the backslashes themselves show
// up in the rendered output (e.g. a path like "mod0000_MergedFiles\a_b.ws" would
// render as the literal text "mod0000\_MergedFiles\a\_b.ws", backslashes and all).
// Caught in this unit's own code review, confirmed against the CommonMark spec and
// fixed here - the regression test in mergePanel.test.ts previously asserted the
// broken, double-escaped output as if it were correct.
function escapeMarkdown(value: string): string {
  return value.replace(/([*_`[\]])/g, '\\$1');
}

/**
 * A heading + bullet list of file paths, each wrapped in a code span (backticks) -
 * deliberately unescaped (see `escapeMarkdown`'s own comment above for why escaping
 * and code-span-wrapping don't compose). `introText`, if given, is a line of prose
 * between the heading and the list - shared by every call site below that needs one
 * (the "Needs manual review" section's differing preview/real-run wording) so the
 * heading/bullet-list shape and its code-span convention live in exactly one place.
 */
function pathListSection(heading: string, paths: string[], introText?: string): string[] {
  if (paths.length === 0) {
    return [];
  }
  const lines = ['', `### ${heading}`, ''];
  if (introText !== undefined) {
    lines.push(introText, '');
  }
  lines.push(...paths.map((p) => `- \`${p}\``));
  return lines;
}

/**
 * Builds the `IDialogContent` for either the dry-run preview dialog or the real-merge
 * result dialog - see this file's own header comment for why both share one builder
 * rather than two near-duplicates (the only real differences are wording, not shape).
 *
 * Defensive against a `result` missing `functionLevelDecisions` even though the
 * `MergeConflictsResult` type says it's always present: `WsmMcpClient.callTool`
 * (`mcpClient.ts`) does a blind `JSON.parse(...) as T` with no runtime shape
 * validation, and this extension's own `toolAcquisition.ts` never force-upgrades an
 * already-acquired WSM binary - a user could plausibly still have one predating this
 * field. Without this guard, reading `.length` off `undefined` would throw a
 * `TypeError` from inside a `showDialog` argument expression in `resolveAction.ts`,
 * escaping the try/catch there entirely and making the whole action appear to do
 * nothing (caught only by the last-resort `.catch()` around the action's own
 * callback, which just logs a warning) - caught in this unit's own code review.
 * Treated as "no function-level decisions to report" rather than an error: version
 * skew here is benign (the feature just doesn't have anything extra to show), not a
 * failure worth interrupting the user over.
 */
export function buildMergeSummaryDialogContent(
  result: MergeConflictsResult,
  options: MergeSummaryDialogOptions,
): types.IDialogContent {
  const { isPreview } = options;
  const functionLevelDecisions = result.functionLevelDecisions ?? [];
  const lines: string[] = [];

  if (isPreview) {
    lines.push('**Preview only - no files have been changed yet.**', '');
  }

  const mergedVerb = isPreview ? 'would merge automatically' : 'merged automatically';
  lines.push(`- **${result.merged.length}** file(s) ${mergedVerb}`);

  const skippedVerb = isPreview ? 'would need manual review' : 'need manual review';
  lines.push(`- **${result.skipped.length}** file(s) ${skippedVerb}`);

  if (result.unmatched.length > 0) {
    lines.push(`- **${result.unmatched.length}** requested path(s) no longer match a detected conflict`);
  }

  // The itemized "here's exactly what happened and why" audit trail - surfaced
  // prominently, right after the headline counts and before the plain merged/skipped
  // path lists below, per this unit's own task description: worth showing, not burying
  // under the counts.
  if (functionLevelDecisions.length > 0) {
    lines.push(
      '',
      '### Function-level merge decisions',
      '',
      "Some files couldn't be merged as a whole, but merged cleanly once split into individual functions:",
      '',
      ...functionLevelDecisions.map((decision) => `- ${escapeMarkdown(decision)}`),
    );
  }

  lines.push(...pathListSection('Merged', result.merged));

  lines.push(
    ...pathListSection(
      'Needs manual review',
      result.skipped,
      isPreview
        ? "No conflict-marker files are written for these yet - nothing is written until you confirm the merge below."
        : 'Conflict-marker sidecar files were written to a `DiffPlexConflicts` folder ' +
            "next to the WitcherScriptMerger executable, and opened for review in your " +
            "system's default editor, for each of these:",
    ),
  );

  lines.push(...pathListSection('Unmatched paths', result.unmatched));

  return { md: lines.join('\n') };
}

import { describe, expect, it } from 'vitest';
import { MergeConflictsResult } from './mcpClient';
import { buildMergeSummaryDialogContent } from './mergePanel';

function result(overrides: Partial<MergeConflictsResult> = {}): MergeConflictsResult {
  return {
    merged: [],
    skipped: [],
    unmatched: [],
    dryRun: false,
    functionLevelDecisions: [],
    ...overrides,
  };
}

describe('buildMergeSummaryDialogContent', () => {
  it('renders merged/skipped counts in the markdown body', () => {
    const content = buildMergeSummaryDialogContent(
      result({ merged: ['game\\a.ws'], skipped: ['game\\b.ws', 'game\\c.xml'] }),
      { isPreview: false },
    );

    expect(content.md).toContain('**1** file(s) merged automatically');
    expect(content.md).toContain('**2** file(s) need manual review');
  });

  it('uses preview-tense wording ("would merge"/"would need") and a preview banner when isPreview is true', () => {
    const content = buildMergeSummaryDialogContent(result({ merged: ['a.ws'] }), { isPreview: true });

    expect(content.md).toContain('Preview only');
    expect(content.md).toContain('would merge automatically');
  });

  it('uses non-preview wording and no preview banner when isPreview is false', () => {
    const content = buildMergeSummaryDialogContent(result({ merged: ['a.ws'] }), { isPreview: false });

    expect(content.md).not.toContain('Preview only');
    expect(content.md).toContain('merged automatically');
    expect(content.md).not.toContain('would merge automatically');
  });

  it('surfaces functionLevelDecisions prominently - before the plain merged/skipped path lists, not buried', () => {
    const decisions = [
      "game\\actor.ws: function OnTakeDamage: kept modX's version (9 changed diff blocks vs. vanilla...), discarded modY's conflicting change to this function.",
    ];
    const content = buildMergeSummaryDialogContent(
      result({ merged: ['game\\actor.ws'], functionLevelDecisions: decisions }),
      { isPreview: false },
    );

    expect(content.md).toContain('Function-level merge decisions');
    expect(content.md).toContain('kept modX');

    const decisionsIndex = content.md!.indexOf('Function-level merge decisions');
    const mergedListIndex = content.md!.indexOf('### Merged');
    expect(decisionsIndex).toBeGreaterThan(-1);
    expect(mergedListIndex).toBeGreaterThan(-1);
    expect(decisionsIndex).toBeLessThan(mergedListIndex);
  });

  it('omits the "Function-level merge decisions" section entirely when there are none', () => {
    const content = buildMergeSummaryDialogContent(result({ merged: ['a.ws'] }), { isPreview: false });

    expect(content.md).not.toContain('Function-level merge decisions');
  });

  it('tells the user conflict-marker sidecars were written and opened for a real (non-preview) run with skipped files', () => {
    const content = buildMergeSummaryDialogContent(result({ skipped: ['gameplay\\items.xml'] }), {
      isPreview: false,
    });

    expect(content.md).toContain('DiffPlexConflicts');
    expect(content.md).toContain('opened for review');
    expect(content.md).toContain('items.xml');
  });

  it('does not claim anything was opened for a preview (dry-run) run with skipped files', () => {
    const content = buildMergeSummaryDialogContent(result({ skipped: ['gameplay\\items.xml'] }), {
      isPreview: true,
    });

    expect(content.md).not.toContain('opened for review');
    expect(content.md).toContain('nothing is written until you confirm');
  });

  it('lists unmatched paths when present', () => {
    const content = buildMergeSummaryDialogContent(result({ unmatched: ['no\\such\\file.ws'] }), {
      isPreview: false,
    });

    expect(content.md).toContain('Unmatched paths');
    expect(content.md).toContain('no\\such\\file.ws');
  });

  it('omits the unmatched section entirely when there are no unmatched paths', () => {
    const content = buildMergeSummaryDialogContent(result(), { isPreview: false });

    expect(content.md).not.toContain('Unmatched paths');
  });

  it('does not escape Markdown-significant characters in file paths - they are already inside a code span, and CommonMark code spans do not process backslash escapes', () => {
    // Regression test: an earlier version of buildMergeSummaryDialogContent escaped
    // paths *and* wrapped them in backticks, which - per CommonMark ("Backslash
    // escapes do not work in ... code spans") - rendered the backslashes themselves
    // instead of suppressing anything, corrupting the extension's own default
    // merged-mod-name pattern ("mod0000_MergedFiles") into a stray-backslash mess.
    // Caught in code review; this test now asserts the correct, literal rendering.
    const content = buildMergeSummaryDialogContent(result({ merged: ['mod0000_MergedFiles\\a_b.ws'] }), {
      isPreview: false,
    });

    expect(content.md).toContain('`mod0000_MergedFiles\\a_b.ws`');
    expect(content.md).not.toContain('\\_');
  });

  it('escapes Markdown-significant characters in function-level decision text (plain prose, not a code span)', () => {
    const content = buildMergeSummaryDialogContent(
      result({ functionLevelDecisions: ["mod0000_MergedFiles: kept modX's edit"] }),
      { isPreview: false },
    );

    expect(content.md).toContain('mod0000\\_MergedFiles');
  });

  it('treats a missing functionLevelDecisions field as "no decisions" instead of throwing - defends against an older WSM binary whose response predates this field', () => {
    const malformed = { merged: ['a.ws'], skipped: [], unmatched: [], dryRun: false } as unknown as MergeConflictsResult;

    expect(() => buildMergeSummaryDialogContent(malformed, { isPreview: false })).not.toThrow();
    const content = buildMergeSummaryDialogContent(malformed, { isPreview: false });
    expect(content.md).not.toContain('Function-level merge decisions');
  });
});

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger.Tools
{
	public enum ScriptUnitKind
	{
		Function,
		Field,
	}

	// One function/event declaration (with its body, or just a signature for a
	// body-less forward/interface declaration) or one @addField-decorated field
	// declaration, as a verbatim slice of the original file text. FullText always
	// includes any immediately-preceding annotation lines (@wrapMethod/@addMethod/
	// @replaceMethod/@addField) - see ScriptUnitExtractor.Extract.
	public readonly struct ScriptUnit
	{
		public string Name { get; }
		public ScriptUnitKind Kind { get; }
		public bool HasBody { get; }
		public int StartOffset { get; }
		public int EndOffset { get; }
		public string FullText { get; }

		public ScriptUnit(string name, ScriptUnitKind kind, bool hasBody, int startOffset, int endOffset, string fullText)
		{
			Name = name;
			Kind = kind;
			HasBody = hasBody;
			StartOffset = startOffset;
			EndOffset = endOffset;
			FullText = fullText;
		}
	}

	// A file split into alternating gap/unit segments: Gaps[0] + Units[0].FullText +
	// Gaps[1] + ... + Units[N-1].FullText + Gaps[N] reassembles byte-for-byte to the
	// original input (see ScriptUnitExtractor.Reassemble) - Gaps always has exactly
	// one more entry than Units, by construction.
	public sealed class ScriptDocument
	{
		public IReadOnlyList<string> Gaps { get; }
		public IReadOnlyList<ScriptUnit> Units { get; }

		public ScriptDocument(IReadOnlyList<string> gaps, IReadOnlyList<ScriptUnit> units)
		{
			Gaps = gaps;
			Units = units;
		}
	}

	// Splits a .ws file's text into function/event/@addField-field units for the
	// function-level merge engine (see WitcherScriptMerger.Core/CLAUDE.md's
	// "Function-level merge engine" section for the full design rationale).
	// Deliberately a brace-matching tokenizer, not a full parser or a bound to the third-party
	// tree-sitter-witcherscript grammar: confirmed via direct research into
	// WitcherScript's grammar that class/state/struct/enum declarations are top-level
	// only (never nested) and the language has no nested function-like constructs at
	// all (no lambdas, local functions, or closures) - a function body only ever gains
	// brace depth from control flow, never from another function declaration. That
	// structural simplicity is what makes plain brace/paren counting sufficient here,
	// as long as it's string/comment-aware (see ClassifySpans) so a brace or paren
	// inside a string literal or comment can never be mistaken for real syntax.
	public static class ScriptUnitExtractor
	{
		// Thrown when the input doesn't parse cleanly - unbalanced braces/parens, or an
		// unterminated string/block comment. Mirrors DiffPlexMergeEngine.
		// DiffAlgorithmException's shape: a distinct exception type so callers can catch
		// specifically "this input isn't safe to extract from" and fall back to
		// whatever they did before this class existed, rather than crashing.
		public sealed class ExtractionException : Exception
		{
			public ExtractionException(string message) : base(message) { }
		}

		// Every specifier/flavour keyword that can precede "function"/"event" in a real
		// declaration (confirmed against WitcherScript's grammar during this feature's
		// design research) plus "override", included defensively even though it wasn't
		// independently confirmed - an unrecognized word here just means the line
		// doesn't match and its content stays in gap territory, never a crash, so
		// including an extra plausible keyword costs nothing if it turns out to be
		// wrong.
		const string SpecifierAlternation =
			"public|private|protected|final|latent|exec|entry|timer|storyscene|quest|reward|" +
			"cleanup|import|editable|const|out|optional|inlined|statemachine|saved|abstract|override";

		static readonly Regex DeclarationRegex = new Regex(
			@"^[ \t]*(?:(?:" + SpecifierAlternation + @")\s+)*(?<kw>function|event)\s+(?<name>\w+)\s*\(",
			RegexOptions.Compiled | RegexOptions.Multiline);

		// A single, simple annotation argument (or none) - e.g. "@wrapMethod(CR4Player)".
		// Deliberately doesn't support nested parens or a multi-line argument list: real
		// WitcherScript modding annotations are single-line with at most one identifier
		// argument, and an annotation shaped some other way just fails to match here,
		// falling into gap territory (attached to nothing) rather than crashing - a safe
		// degradation, not silent corruption.
		static readonly Regex AnnotationLineRegex = new Regex(@"^@\w+\s*(\([^()\r\n]*\))?\s*$", RegexOptions.Compiled);
		static readonly Regex AddFieldAnnotationRegex = new Regex(@"^@addField\s*\([^()\r\n]*\)\s*$", RegexOptions.Compiled);
		static readonly Regex FieldNameRegex = new Regex(@"\bvar\s+(?<name>\w+)\s*:", RegexOptions.Compiled);

		enum SpanKind : byte
		{
			None,
			String,
			LineComment,
			BlockComment,
		}

		#region Public API

		public static ScriptDocument Extract(string text)
		{
			var kinds = ClassifySpans(text);
			var mask = BuildMask(text, kinds, blankStringsToo: true);
			var lineStarts = ComputeLineStarts(text);
			var addFieldLineStarts = FindAllAddFieldAnnotationLineStarts(mask, lineStarts);

			var gaps = new List<string>();
			var units = new List<ScriptUnit>();

			var cursor = 0;
			var pos = 0;
			// Monotonic pointer into addFieldLineStarts, not a fresh scan per unit -
			// see that list's own comment for the real O(units * remaining lines) cost
			// a per-call rescan used to have (a vanilla file has zero @addField
			// annotations at all, since it's a mod-only construct, so every one of a
			// large vanilla file's function extractions used to scan all the way to
			// EOF just to confirm that). pos only ever increases across iterations, so
			// this index never needs to rewind.
			var addFieldIndex = 0;
			while (pos <= text.Length)
			{
				var funcMatch = DeclarationRegex.Match(mask, pos);

				while (addFieldIndex < addFieldLineStarts.Count && addFieldLineStarts[addFieldIndex] < pos)
					++addFieldIndex;
				var fieldLineStart = addFieldIndex < addFieldLineStarts.Count ? (int?)addFieldLineStarts[addFieldIndex] : null;

				var funcPos = funcMatch.Success ? funcMatch.Index : int.MaxValue;
				var fieldPos = fieldLineStart ?? int.MaxValue;

				if (funcPos == int.MaxValue && fieldPos == int.MaxValue)
					break;

				ScriptUnit unit;
				if (fieldPos < funcPos)
					unit = ExtractField(text, mask, lineStarts, fieldPos, cursor);
				else
					unit = ExtractFunction(text, mask, lineStarts, funcMatch, cursor);

				gaps.Add(text.Substring(cursor, unit.StartOffset - cursor));
				units.Add(unit);
				cursor = unit.EndOffset;
				pos = unit.EndOffset;
			}

			gaps.Add(text.Substring(cursor));
			return new ScriptDocument(gaps, units);
		}

		public static string Reassemble(ScriptDocument document)
		{
			var sb = new StringBuilder();
			for (var i = 0; i < document.Units.Count; ++i)
			{
				sb.Append(document.Gaps[i]);
				sb.Append(document.Units[i].FullText);
			}
			sb.Append(document.Gaps[document.Gaps.Count - 1]);
			return sb.ToString();
		}

		// Blanks comment spans only (line and block), preserving string-literal content
		// and everything else verbatim - unlike the brace-safe mask Extract uses
		// internally, this is meant to produce comparable, still-readable text for the
		// function-level merge engine's distinctness metric (see the plan), not a
		// scratch buffer for structural matching.
		public static string StripComments(string text)
		{
			var kinds = ClassifySpans(text);
			return BuildMask(text, kinds, blankStringsToo: false);
		}

		#endregion

		#region Unit extraction

		static ScriptUnit ExtractFunction(string text, string mask, List<int> lineStarts, Match declMatch, int cursor)
		{
			var unitStart = Math.Max(cursor, ExtendStartBackwardOverAnnotations(mask, lineStarts, declMatch.Index));

			var openParen = declMatch.Index + declMatch.Length - 1;
			if (mask[openParen] != '(')
				throw new ExtractionException(
					"Internal error: declaration match for '" + declMatch.Groups["name"].Value +
					"' did not end on its own opening parenthesis.");

			var closeParen = FindMatchingDelimiter(mask, openParen, '(', ')');
			if (closeParen < 0)
				throw new ExtractionException(
					"Unbalanced parameter-list parentheses in the declaration of '" +
					declMatch.Groups["name"].Value + "' starting at offset " + declMatch.Index + ".");

			var terminator = FindNextSemicolonOrBrace(mask, closeParen + 1);
			if (terminator < 0)
				throw new ExtractionException(
					"Reached end of file looking for ';' or '{' after the declaration of '" +
					declMatch.Groups["name"].Value + "' starting at offset " + declMatch.Index + ".");

			int unitEnd;
			bool hasBody;
			if (mask[terminator] == ';')
			{
				hasBody = false;
				unitEnd = terminator + 1;
			}
			else
			{
				var closeBrace = FindMatchingDelimiter(mask, terminator, '{', '}');
				if (closeBrace < 0)
					throw new ExtractionException(
						"Unbalanced braces in the body of '" + declMatch.Groups["name"].Value +
						"' starting at offset " + declMatch.Index + ".");
				hasBody = true;
				unitEnd = closeBrace + 1;
			}

			return new ScriptUnit(
				declMatch.Groups["name"].Value, ScriptUnitKind.Function, hasBody,
				unitStart, unitEnd, text.Substring(unitStart, unitEnd - unitStart));
		}

		static ScriptUnit ExtractField(string text, string mask, List<int> lineStarts, int annotationLineStart, int cursor)
		{
			var unitStart = Math.Max(cursor, ExtendStartBackwardOverAnnotations(mask, lineStarts, annotationLineStart));

			var annotationLineEnd = GetLineEnd(mask, lineStarts, annotationLineStart);
			var terminator = FindNextChar(mask, annotationLineEnd, ';');
			if (terminator < 0)
				throw new ExtractionException(
					"Reached end of file looking for the ';' terminating the @addField declaration " +
					"starting at offset " + annotationLineStart + ".");

			var unitEnd = terminator + 1;
			var fullText = text.Substring(unitStart, unitEnd - unitStart);
			var nameMatch = FieldNameRegex.Match(mask, annotationLineEnd, terminator - annotationLineEnd);
			var name = nameMatch.Success ? nameMatch.Groups["name"].Value : "@addField#" + unitStart;

			return new ScriptUnit(name, ScriptUnitKind.Field, hasBody: false, unitStart, unitEnd, fullText);
		}

		// Walks backward over any immediately preceding @-annotation lines (tolerating
		// blank lines between them, and between the last annotation and the unit
		// itself), so @wrapMethod/@addMethod/@replaceMethod/@addField stay glued to
		// what they decorate. Clamped to never walk back past cursor (the end of the
		// previously extracted unit/gap), so a pathological run of stacked declarations
		// with no blank line between them can never make one unit's annotation walk
		// swallow part of the previous unit.
		static int ExtendStartBackwardOverAnnotations(string mask, List<int> lineStarts, int unitStart)
		{
			var resultStart = unitStart;
			var lineIndex = GetLineIndex(lineStarts, unitStart) - 1;

			while (lineIndex >= 0)
			{
				var lineStart = lineStarts[lineIndex];
				var lineEnd = GetLineEnd(mask, lineStarts, lineStart);
				var content = mask.Substring(lineStart, lineEnd - lineStart).Trim();

				if (content.Length == 0)
				{
					--lineIndex;
					continue;
				}

				if (AnnotationLineRegex.IsMatch(content))
				{
					resultStart = lineStart;
					--lineIndex;
					continue;
				}

				break;
			}

			return resultStart;
		}

		// A single O(lines) forward pass over the whole document, computed once per
		// Extract call - not a fresh scan-to-EOF per extracted unit (see Extract's own
		// comment on why that mattered). lineEnd is computed directly from the loop's
		// own lineIndex rather than via GetLineEnd (which would redundantly re-derive
		// that same index through a binary search).
		static List<int> FindAllAddFieldAnnotationLineStarts(string mask, List<int> lineStarts)
		{
			var result = new List<int>();
			for (var lineIndex = 0; lineIndex < lineStarts.Count; ++lineIndex)
			{
				var lineStart = lineStarts[lineIndex];
				var lineEnd = lineIndex + 1 < lineStarts.Count ? lineStarts[lineIndex + 1] : mask.Length;
				var content = mask.Substring(lineStart, lineEnd - lineStart).Trim();
				if (content.Length == 0)
					continue;
				if (AddFieldAnnotationRegex.IsMatch(content))
					result.Add(lineStart);
			}
			return result;
		}

		#endregion

		#region Masking (string/comment-safe scanning)

		// One left-to-right scan classifying every character as inside a "//" comment,
		// a "/* */" comment (non-nesting), a "\"...\"" string literal (backslash-escape
		// aware - "\\\"" doesn't terminate the string, and "\\\\" consumes as one
		// escaped-backslash unit rather than misreading the following character as an
		// escape target), or none of those. Single-quote literals aren't handled - not
		// part of the confirmed WitcherScript syntax facts this class was built from.
		// Reused by both the brace-safe mask (BuildMask, blankStringsToo: true) and
		// StripComments (blankStringsToo: false) so the actual comment/string detection
		// logic exists exactly once.
		static SpanKind[] ClassifySpans(string text)
		{
			var kinds = new SpanKind[text.Length];
			var i = 0;
			while (i < text.Length)
			{
				var c = text[i];
				var next = i + 1 < text.Length ? text[i + 1] : '\0';

				if (c == '/' && next == '/')
				{
					var start = i;
					while (i < text.Length && text[i] != '\n' && text[i] != '\r')
						++i;
					for (var k = start; k < i; ++k)
						kinds[k] = SpanKind.LineComment;
					continue;
				}

				if (c == '/' && next == '*')
				{
					var start = i;
					i += 2;
					while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
						++i;
					if (i + 1 >= text.Length)
						throw new ExtractionException("Unterminated block comment starting at offset " + start + ".");
					i += 2;
					for (var k = start; k < i; ++k)
						kinds[k] = SpanKind.BlockComment;
					continue;
				}

				if (c == '"')
				{
					var start = i;
					++i;
					while (i < text.Length && text[i] != '"')
					{
						if (text[i] == '\\' && i + 1 < text.Length)
							i += 2;
						else
							++i;
					}
					if (i >= text.Length)
						throw new ExtractionException("Unterminated string literal starting at offset " + start + ".");
					++i;
					for (var k = start; k < i; ++k)
						kinds[k] = SpanKind.String;
					continue;
				}

				++i;
			}
			return kinds;
		}

		// Blanks masked characters to a literal space, EXCEPT '\n'/'\r' - a multi-line
		// block comment or string must keep its own line breaks intact, or line-start
		// offsets (ComputeLineStarts) and the '^'-anchored DeclarationRegex would both
		// silently desync from the real text.
		static string BuildMask(string text, SpanKind[] kinds, bool blankStringsToo)
		{
			var chars = text.ToCharArray();
			for (var i = 0; i < chars.Length; ++i)
			{
				var blank = kinds[i] == SpanKind.LineComment || kinds[i] == SpanKind.BlockComment
					|| (blankStringsToo && kinds[i] == SpanKind.String);
				if (blank && chars[i] != '\n' && chars[i] != '\r')
					chars[i] = ' ';
			}
			return new string(chars);
		}

		static int FindMatchingDelimiter(string mask, int openIndex, char open, char close)
		{
			var depth = 0;
			for (var i = openIndex; i < mask.Length; ++i)
			{
				if (mask[i] == open) ++depth;
				else if (mask[i] == close)
				{
					--depth;
					if (depth == 0)
						return i;
				}
			}
			return -1;
		}

		static int FindNextSemicolonOrBrace(string mask, int start)
		{
			for (var i = start; i < mask.Length; ++i)
				if (mask[i] == ';' || mask[i] == '{')
					return i;
			return -1;
		}

		static int FindNextChar(string mask, int start, char target)
		{
			for (var i = start; i < mask.Length; ++i)
				if (mask[i] == target)
					return i;
			return -1;
		}

		#endregion

		#region Line bookkeeping

		static List<int> ComputeLineStarts(string text)
		{
			var starts = new List<int> { 0 };
			for (var i = 0; i < text.Length; ++i)
				if (text[i] == '\n')
					starts.Add(i + 1);
			return starts;
		}

		static int GetLineIndex(List<int> lineStarts, int offset)
		{
			var lo = 0;
			var hi = lineStarts.Count - 1;
			while (lo < hi)
			{
				var mid = (lo + hi + 1) / 2;
				if (lineStarts[mid] <= offset) lo = mid;
				else hi = mid - 1;
			}
			return lo;
		}

		static int GetLineEnd(string mask, List<int> lineStarts, int lineStart)
		{
			var lineIndex = GetLineIndex(lineStarts, lineStart);
			return lineIndex + 1 < lineStarts.Count ? lineStarts[lineIndex + 1] : mask.Length;
		}

		#endregion
	}
}

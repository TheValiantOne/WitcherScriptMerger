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
		// A whole `enum Name { ... }` declaration, extracted as ONE unit (members and
		// braces included). Enum members are bare identifiers, not statements, so they
		// can't be per-member units - but leaving whole enums in gap territory meant a
		// mod ADDING a member had that addition silently reverted to vanilla while the
		// code using the new member survived ("I dont know any 'HVS_Modcrab'",
		// observed live: modalchemyrequiresmeditation extends hud.ws's
		// EHudVisibilitySource). As a single unit, an enum edited by one side takes
		// that side's whole block, and a both-sides edit goes through the normal
		// per-unit 3-way merge/tiebreak.
		EnumDeclaration,
		// A plain (non-@addField) member declaration: `[specifiers] var a, b : T;`,
		// `default x = value;`, or `[specifiers] autobind c : T = ...;`. Promoted to
		// unit status (rather than living in gap territory) because real mods add
		// these to vanilla classes routinely, and gap content always reverts to
		// vanilla's own text on reassembly - which silently dropped such declarations
		// while the code referencing them survived, producing merged output the game
		// refuses to compile (docs/bugs/function-level-merge-gap-handling.md,
		// defect 2).
		MemberDeclaration,
	}

	// One function/event declaration (with its body, or just a signature for a
	// body-less forward/interface declaration), one @addField-decorated field
	// declaration, or one plain member declaration (var/default/autobind - see
	// ScriptUnitKind.MemberDeclaration), as a verbatim slice of the original file
	// text. FullText always includes any immediately-preceding annotation lines
	// (@wrapMethod/@addMethod/@replaceMethod/@addField) - see
	// ScriptUnitExtractor.Extract.
	public readonly struct ScriptUnit
	{
		public string Name { get; }
		// Name qualified by the enclosing top-level type ("CR4Player::mCSMCR"), or
		// just Name for a global-scope unit. This - not Name - is what UnitAligner
		// matches on: member names ("owner", "isActive", ...) recur across the
		// multiple classes a single real .ws file contains, so name-only identity
		// would routinely mis-align a member of one class against a same-named member
		// of another. (Function names were measured not to collide within real files,
		// but scoping them too costs nothing and closes the same latent risk.)
		public string ScopedName { get; }
		public ScriptUnitKind Kind { get; }
		public bool HasBody { get; }
		public int StartOffset { get; }
		public int EndOffset { get; }
		public string FullText { get; }

		public ScriptUnit(string name, string scopedName, ScriptUnitKind kind, bool hasBody, int startOffset, int endOffset, string fullText)
		{
			Name = name;
			ScopedName = scopedName;
			Kind = kind;
			HasBody = hasBody;
			StartOffset = startOffset;
			EndOffset = endOffset;
			FullText = fullText;
		}

		// The human-facing noun for audit/decision messages - "function OnSpawned" vs
		// "declaration mCSMCR" - so FunctionLevelMergeEngine's notes don't call a
		// variable a function.
		public string DescribeKind() => Kind switch
		{
			ScriptUnitKind.Function => "function",
			ScriptUnitKind.EnumDeclaration => "enum",
			_ => "declaration",
		};
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

		// Plain member declarations (ScriptUnitKind.MemberDeclaration). Three shapes,
		// all ';'-terminated:
		//   [specifiers] var a, b : Type;          (multi-declarator lists are one unit,
		//                                           keyed by the comma-joined name list -
		//                                           a mod splitting/merging declarators
		//                                           aligns as delete+insert, which the
		//                                           engine already resolves conservatively)
		//   default x = value;
		//   [specifiers] autobind c : Type = ...;
		// ^-anchored against the mask like DeclarationRegex, so a masked-out string/
		// comment can never fake one. Local variables inside function bodies are never
		// reached: Extract consumes an entire function body as one unit and resumes
		// scanning after it, so member scans only ever run over between-unit territory.
		static readonly Regex MemberDeclRegex = new Regex(
			@"^[ \t]*(?:(?:" + SpecifierAlternation + @")\s+)*(?:(?<varkw>var|autobind)\s+(?<names>\w+(?:\s*,\s*\w+)*)\s*:|(?<defkw>default)\s+(?<defname>\w+)\s*=)",
			RegexOptions.Compiled | RegexOptions.Multiline);

		// A whole-enum unit's header. Anchored like DeclarationRegex; the block is
		// consumed through its matching close brace (see ExtractEnum).
		static readonly Regex EnumHeaderRegex = new Regex(
			@"^[ 	]*enum\s+(?<name>\w+)", RegexOptions.Compiled | RegexOptions.Multiline);

		// Top-level type headers, for scope tracking (see ScriptUnit.ScopedName).
		// Matched against the mask; types are top-level-only in WitcherScript (see this
		// class's own header comment), so scanning header -> matching close brace ->
		// next header never has to consider nesting.
		static readonly Regex TypeHeaderRegex = new Regex(
			@"\b(?<kind>class|state|struct|enum)\s+(?<name>\w+)(?:\s+in\s+(?<parent>\w+))?",
			RegexOptions.Compiled);

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
			var typeRanges = FindTypeRanges(mask);

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
				var memberMatch = MemberDeclRegex.Match(mask, pos);
				var enumMatch = EnumHeaderRegex.Match(mask, pos);

				while (addFieldIndex < addFieldLineStarts.Count && addFieldLineStarts[addFieldIndex] < pos)
					++addFieldIndex;
				var fieldLineStart = addFieldIndex < addFieldLineStarts.Count ? (int?)addFieldLineStarts[addFieldIndex] : null;

				var funcPos = funcMatch.Success ? funcMatch.Index : int.MaxValue;
				var fieldPos = fieldLineStart ?? int.MaxValue;
				var memberPos = memberMatch.Success ? memberMatch.Index : int.MaxValue;
				var enumPos = enumMatch.Success ? enumMatch.Index : int.MaxValue;

				if (funcPos == int.MaxValue && fieldPos == int.MaxValue && memberPos == int.MaxValue && enumPos == int.MaxValue)
					break;

				// Earliest match wins. An @addField unit's own `var ...` line also
				// matches MemberDeclRegex, but its annotation line sits strictly
				// earlier, so the field extraction always claims it first and consumes
				// through the terminating ';' before the member scan can see it.
				ScriptUnit unit;
				if (fieldPos <= funcPos && fieldPos <= memberPos && fieldPos <= enumPos)
					unit = ExtractField(text, mask, lineStarts, fieldPos, cursor, typeRanges);
				else if (enumPos < funcPos && enumPos <= memberPos)
					unit = ExtractEnum(text, mask, enumMatch, cursor);
				else if (memberPos < funcPos)
					unit = ExtractMemberDeclaration(text, mask, memberMatch, cursor, typeRanges);
				else
					unit = ExtractFunction(text, mask, lineStarts, funcMatch, cursor, typeRanges);

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

		// Strings AND comments blanked - the same brace-safe scratch buffer Extract
		// uses internally, exposed for FunctionLevelMergeEngine's post-rescue sanity
		// gate (which needs to walk brace depth over reassembled output without a
		// literal/comment brace corrupting the count). Internal, not public: a
		// structural scratch buffer, not part of this class's stable contract. Throws
		// ExtractionException on unterminated constructs, same as Extract.
		internal static string BuildStructuralMask(string text)
		{
			var kinds = ClassifySpans(text);
			return BuildMask(text, kinds, blankStringsToo: true);
		}

		#endregion

		#region Unit extraction

		static ScriptUnit ExtractFunction(string text, string mask, List<int> lineStarts, Match declMatch, int cursor, List<TypeRange> typeRanges)
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

			var name = declMatch.Groups["name"].Value;
			return new ScriptUnit(
				name, QualifyName(typeRanges, declMatch.Index, name), ScriptUnitKind.Function, hasBody,
				unitStart, unitEnd, text.Substring(unitStart, unitEnd - unitStart));
		}

		static ScriptUnit ExtractField(string text, string mask, List<int> lineStarts, int annotationLineStart, int cursor, List<TypeRange> typeRanges)
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

			return new ScriptUnit(name, QualifyName(typeRanges, annotationLineStart, name), ScriptUnitKind.Field, hasBody: false, unitStart, unitEnd, fullText);
		}

		// A whole `enum Name { ... }` block as one unit - see
		// ScriptUnitKind.EnumDeclaration's comment for why per-member extraction isn't
		// viable and what silently broke while enums were gap territory. Keyed
		// "enum:Name" so an enum can never collide with a same-named function's
		// identity. Enums are top-level in WitcherScript, so no scope qualification.
		static ScriptUnit ExtractEnum(string text, string mask, Match enumMatch, int cursor)
		{
			var unitStart = Math.Max(cursor, enumMatch.Index);

			var openBrace = FindNextChar(mask, enumMatch.Index + enumMatch.Length, '{');
			if (openBrace < 0)
				throw new ExtractionException(
					"Reached end of file looking for '{' after the enum declaration of '" +
					enumMatch.Groups["name"].Value + "' starting at offset " + enumMatch.Index + ".");

			var closeBrace = FindMatchingDelimiter(mask, openBrace, '{', '}');
			if (closeBrace < 0)
				throw new ExtractionException(
					"Unbalanced braces in the body of enum '" + enumMatch.Groups["name"].Value +
					"' starting at offset " + enumMatch.Index + ".");

			var unitEnd = closeBrace + 1;
			var name = "enum:" + enumMatch.Groups["name"].Value;
			return new ScriptUnit(
				name, name, ScriptUnitKind.EnumDeclaration, hasBody: true,
				unitStart, unitEnd, text.Substring(unitStart, unitEnd - unitStart));
		}

		// A plain member declaration - see MemberDeclRegex for the shapes covered. The
		// unit is the whole statement through its terminating ';'. `default x = ...` is
		// keyed "default:x", distinct from the member variable x it initializes - a mod
		// changing a default value and a mod changing the declaration are different
		// edits to different statements, and conflating their identities would make one
		// mod's default-value change look like an edit to the other's declaration.
		static ScriptUnit ExtractMemberDeclaration(string text, string mask, Match declMatch, int cursor, List<TypeRange> typeRanges)
		{
			var unitStart = Math.Max(cursor, declMatch.Index);

			var terminator = FindNextChar(mask, declMatch.Index + declMatch.Length, ';');
			if (terminator < 0)
				throw new ExtractionException(
					"Reached end of file looking for the ';' terminating the member declaration " +
					"starting at offset " + declMatch.Index + ".");

			var unitEnd = terminator + 1;
			string name;
			if (declMatch.Groups["defkw"].Success)
			{
				name = "default:" + declMatch.Groups["defname"].Value;
			}
			else
			{
				// Multi-declarator lists ("var a, b : int;") stay one unit, keyed by the
				// whitespace-normalized comma-joined list.
				name = Regex.Replace(declMatch.Groups["names"].Value, @"\s+", "");
			}

			return new ScriptUnit(
				name, QualifyName(typeRanges, declMatch.Index, name), ScriptUnitKind.MemberDeclaration,
				hasBody: false, unitStart, unitEnd, text.Substring(unitStart, unitEnd - unitStart));
		}

		// One top-level type's body span: units whose start offset falls inside
		// (OpenBrace, CloseBrace) get ScopeName as their ScopedName qualifier.
		readonly struct TypeRange
		{
			public string ScopeName { get; }
			public int OpenBrace { get; }
			public int CloseBrace { get; }

			public TypeRange(string scopeName, int openBrace, int closeBrace)
			{
				ScopeName = scopeName;
				OpenBrace = openBrace;
				CloseBrace = closeBrace;
			}
		}

		// Finds every top-level type's body span, for scope-qualifying unit names.
		// Types are top-level only in WitcherScript (never nested - see this class's
		// header comment), so each header's matching close brace can be found by plain
		// depth counting and the scan can resume after the header rather than after the
		// body (a body-less `import class CX;`-style declaration has no braces at all).
		// A `state Combat in CR4Player` header qualifies as "Combat@CR4Player" - the
		// same state name recurs across parent classes in real game scripts.
		static List<TypeRange> FindTypeRanges(string mask)
		{
			var result = new List<TypeRange>();
			foreach (Match header in TypeHeaderRegex.Matches(mask))
			{
				// Skip a header that sits inside a previously recorded type's body -
				// grammar says that can't happen, but a stray masked keyword in an
				// unusual construct shouldn't corrupt scope attribution for the rest of
				// the file.
				if (result.Count > 0 && header.Index < result[result.Count - 1].CloseBrace)
					continue;

				var terminator = FindNextSemicolonOrBrace(mask, header.Index + header.Length);
				if (terminator < 0 || mask[terminator] == ';')
					continue; // body-less declaration - nothing to scope

				var closeBrace = FindMatchingDelimiter(mask, terminator, '{', '}');
				if (closeBrace < 0)
					throw new ExtractionException(
						"Unbalanced braces in the body of type '" + header.Groups["name"].Value +
						"' starting at offset " + header.Index + ".");

				var scopeName = header.Groups["parent"].Success
					? header.Groups["name"].Value + "@" + header.Groups["parent"].Value
					: header.Groups["name"].Value;
				result.Add(new TypeRange(scopeName, terminator, closeBrace));
			}
			return result;
		}

		static string QualifyName(List<TypeRange> typeRanges, int offset, string name)
		{
			foreach (var range in typeRanges)
			{
				if (offset > range.OpenBrace && offset < range.CloseBrace)
					return range.ScopeName + "::" + name;
				if (range.OpenBrace > offset)
					break; // ranges are in file order; nothing later can contain offset
			}
			return name;
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

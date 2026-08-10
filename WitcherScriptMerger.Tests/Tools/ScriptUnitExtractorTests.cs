using System.Linq;
using WitcherScriptMerger.Tools;
using Xunit;

namespace WitcherScriptMerger.Tests.Tools
{
	// Regression coverage for ScriptUnitExtractor - the function-level merge engine's
	// splitter (see WitcherScriptMerger.Core/CLAUDE.md once this lands there, and
	// the plan this class was built from). Every fixture asserts the round-trip
	// property (Reassemble(Extract(x)) == x) AND the specific units found, since
	// round-tripping alone doesn't prove extraction found the right boundaries - a
	// document with zero units detected still round-trips trivially as one big gap.
	public class ScriptUnitExtractorTests
	{
		[Fact]
		public void Extract_PlainFunction_FindsOneUnitAndRoundTrips()
		{
			var text = "class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
			Assert.Equal(ScriptUnitKind.Function, unit.Kind);
			Assert.True(unit.HasBody);
		}

		[Fact]
		public void Extract_EventDeclaration_FindsOneUnitAndRoundTrips()
		{
			var text = "class Foo\r\n{\r\n\tevent OnSpawned( SEntitySpawnData data )\r\n\t{\r\n\t\treturn true;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("OnSpawned", unit.Name);
		}

		[Fact]
		public void Extract_ForwardDeclarationTerminatedBySemicolon_HasBodyIsFalse()
		{
			var text = "class Foo\r\n{\r\n\timport function Bar();\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
			Assert.False(unit.HasBody);
			Assert.EndsWith(";", unit.FullText);
		}

		[Fact]
		public void Extract_AnnotationImmediatelyPrecedingFunction_IsIncludedInUnit()
		{
			var text = "class Foo\r\n{\r\n\t@wrapMethod(Foo)\r\n\tfunction Bar()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.StartsWith("\t@wrapMethod(Foo)", unit.FullText);
		}

		[Fact]
		public void Extract_AnnotationSeparatedFromFunctionByBlankLine_IsStillIncludedInUnit()
		{
			var text = "class Foo\r\n{\r\n\t@wrapMethod(Foo)\r\n\r\n\tfunction Bar()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.StartsWith("\t@wrapMethod(Foo)", unit.FullText);
		}

		[Fact]
		public void Extract_AddFieldAnnotatedField_IsFoundAsFieldUnit()
		{
			var text = "class Foo\r\n{\r\n\t@addField(Foo)\r\n\tvar myNewField : bool;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("myNewField", unit.Name);
			Assert.Equal(ScriptUnitKind.Field, unit.Kind);
			Assert.False(unit.HasBody);
			Assert.StartsWith("\t@addField(Foo)", unit.FullText);
		}

		[Fact]
		public void Extract_NestedControlFlowBraces_DoNotConfuseBodyEnd()
		{
			var text =
				"class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n" +
				"\t\tif (x > 0)\r\n\t\t{\r\n\t\t\twhile (y < 10)\r\n\t\t\t{\r\n\t\t\t\ty += 1;\r\n\t\t\t}\r\n\t\t}\r\n" +
				"\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
			Assert.EndsWith("\t}", unit.FullText);
		}

		[Fact]
		public void Extract_BraceInsideStringLiteral_DoesNotConfuseBodyEnd()
		{
			var text = "class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\ts = \"{ not a real brace }\";\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
			Assert.Contains("not a real brace", unit.FullText);
		}

		[Fact]
		public void Extract_BraceInsideBlockComment_DoesNotConfuseBodyEnd()
		{
			var text = "class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\t/* if (x) { y = 1; } */\r\n\t\tz = 2;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
		}

		[Fact]
		public void Extract_MultiLineBlockComment_PreservesLineStructureForLaterUnits()
		{
			var text =
				"/* a multi-line\r\n   block comment\r\n   { with braces } inside */\r\n" +
				"class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Bar", unit.Name);
		}

		[Fact]
		public void Extract_LineCommentContainingWhatLooksLikeADeclaration_IsNotTreatedAsAUnit()
		{
			var text =
				"class Foo\r\n{\r\n\t// function FakeOne() { return 1; }\r\n" +
				"\tfunction Real()\r\n\t{\r\n\t\tx = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Real", unit.Name);
		}

		[Fact]
		public void Extract_MultipleFunctionsWithGapsBetween_FindsAllInOrderAndRoundTrips()
		{
			var text =
				"class Foo\r\n{\r\n\tvar x : int;\r\n\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n\r\n" +
				"\t// a comment gap\r\n\r\n\tfunction B()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(new[] { "A", "B" }, doc.Units.Select(u => u.Name));
			Assert.Equal(3, doc.Gaps.Count);
		}

		[Fact]
		public void Extract_NoFunctionsAtAll_ReturnsSingleGapAndRoundTrips()
		{
			var text = "class Foo\r\n{\r\n\tvar x : int;\r\n\tvar y : int;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Empty(doc.Units);
			Assert.Single(doc.Gaps);
		}

		[Fact]
		public void Extract_EmptyString_ReturnsSingleEmptyGap()
		{
			var doc = ScriptUnitExtractor.Extract(string.Empty);

			Assert.Equal(string.Empty, ScriptUnitExtractor.Reassemble(doc));
			Assert.Empty(doc.Units);
			Assert.Single(doc.Gaps);
		}

		[Fact]
		public void Extract_UnbalancedBraceInBody_ThrowsExtractionException()
		{
			// No closing brace anywhere after the function's own opening brace - the
			// extractor only ever brace-matches within a function body, never validates
			// enclosing class/state braces, so the "missing" brace must be the
			// function's own for this to actually exercise the unbalanced-body path.
			var text = "class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\tx = 1;\r\n";

			var ex = Assert.Throws<ScriptUnitExtractor.ExtractionException>(() => ScriptUnitExtractor.Extract(text));
			Assert.NotNull(ex.Message);
		}

		[Fact]
		public void Extract_UnterminatedStringLiteral_ThrowsExtractionException()
		{
			var text = "class Foo\r\n{\r\n\tfunction Bar()\r\n\t{\r\n\t\ts = \"never closed;\r\n\t}\r\n}\r\n";

			Assert.Throws<ScriptUnitExtractor.ExtractionException>(() => ScriptUnitExtractor.Extract(text));
		}

		[Fact]
		public void Extract_UnterminatedBlockComment_ThrowsExtractionException()
		{
			var text = "class Foo\r\n{\r\n\t/* never closed\r\n\tfunction Bar() { x = 1; }\r\n}\r\n";

			Assert.Throws<ScriptUnitExtractor.ExtractionException>(() => ScriptUnitExtractor.Extract(text));
		}

		[Fact]
		public void StripComments_BlanksCommentsButPreservesStringContentAndLineStructure()
		{
			var text = "x = 1; // trailing comment\r\ny = \"keep { this }\"; /* block */\r\nz = 3;\r\n";

			var stripped = ScriptUnitExtractor.StripComments(text);

			Assert.DoesNotContain("trailing comment", stripped);
			Assert.DoesNotContain("block", stripped);
			Assert.Contains("keep { this }", stripped);
			Assert.Equal(text.Split('\n').Length, stripped.Split('\n').Length);
		}
	}
}

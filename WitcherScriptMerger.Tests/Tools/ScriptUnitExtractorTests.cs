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
			// "var x : int;" is a MemberDeclaration unit too (gap-handling v2 - see
			// docs/bugs/function-level-merge-gap-handling.md defect 2), so three units
			// come back, in file order, all scope-qualified by the enclosing class.
			Assert.Equal(new[] { "x", "A", "B" }, doc.Units.Select(u => u.Name));
			Assert.Equal(new[] { "Foo::x", "Foo::A", "Foo::B" }, doc.Units.Select(u => u.ScopedName));
			Assert.Equal(4, doc.Gaps.Count);
		}

		[Fact]
		public void Extract_OnlyMemberDeclarations_ExtractedAsScopedUnitsAndRoundTrips()
		{
			var text = "class Foo\r\n{\r\n\tvar x : int;\r\n\tvar y : int;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			// Plain member declarations are units now (gap-handling v2): a mod adding
			// one to a vanilla class must participate in per-unit resolution rather
			// than being silently dropped with vanilla's gap text - see
			// docs/bugs/function-level-merge-gap-handling.md defect 2.
			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(new[] { "Foo::x", "Foo::y" }, doc.Units.Select(u => u.ScopedName));
			Assert.All(doc.Units, u => Assert.Equal(ScriptUnitKind.MemberDeclaration, u.Kind));
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

		#region Member declarations & scoping (gap-handling v2)

		[Fact]
		public void Extract_DefaultStatement_IsItsOwnUnitDistinctFromTheVariable()
		{
			var text = "class Foo\r\n{\r\n\tvar d : float;\r\n\tdefault d = 4.5f;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			// The declaration and its default are separate statements a mod can edit
			// independently - separate units with distinct identities.
			Assert.Equal(new[] { "Foo::d", "Foo::default:d" }, doc.Units.Select(u => u.ScopedName));
		}

		[Fact]
		public void Extract_MultiDeclaratorVarLine_OneUnitKeyedByJoinedNames()
		{
			var text = "class Foo\r\n{\r\n\tprivate var a, b : int;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Foo::a,b", unit.ScopedName);
		}

		[Fact]
		public void Extract_SameMemberNameInTwoClasses_ScopedNamesDiffer()
		{
			var text =
				"class Foo\r\n{\r\n\tvar owner : int;\r\n}\r\n\r\n" +
				"class Bar\r\n{\r\n\tvar owner : int;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			// Bare names collide; scoped names must not - this is what stops the
			// aligner from matching Foo's member against Bar's.
			Assert.Equal(new[] { "Foo::owner", "Bar::owner" }, doc.Units.Select(u => u.ScopedName));
		}

		[Fact]
		public void Extract_LocalVariablesInsideFunctionBodies_AreNotUnits()
		{
			var text =
				"class Foo\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\tvar local : int;\r\n\t\tlocal = 1;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			// The function body is consumed wholesale; the local var inside it must
			// never surface as its own unit.
			var unit = Assert.Single(doc.Units);
			Assert.Equal("Foo::A", unit.ScopedName);
		}

		[Fact]
		public void Extract_StateHeader_ScopeIncludesParentClass()
		{
			var text =
				"state Combat in CR4Player\r\n{\r\n\tvar phase : int;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			var unit = Assert.Single(doc.Units);
			// Same state name recurs across parent classes in real game scripts, so
			// the parent is part of the scope identity.
			Assert.Equal("Combat@CR4Player::phase", unit.ScopedName);
		}

		[Fact]
		public void Extract_StructMember_ScopedToStruct()
		{
			var text = "struct SPoint\r\n{\r\n\tvar x : float;\r\n\tvar y : float;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(new[] { "SPoint::x", "SPoint::y" }, doc.Units.Select(u => u.ScopedName));
		}

		[Fact]
		public void Extract_GlobalFunctionAfterClass_NotScopedToTheClass()
		{
			var text =
				"class Foo\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n\r\n" +
				"exec function E()\r\n{\r\n\treturn;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(new[] { "Foo::A", "E" }, doc.Units.Select(u => u.ScopedName));
		}

		[Fact]
		public void Extract_AddFieldUnit_StillWinsOverPlainMemberScanForItsOwnVarLine()
		{
			var text =
				"@addField(CR4Player)\r\nprivate var injected : bool;\r\n\r\n" +
				"@wrapMethod(CR4Player)\r\nfunction Wrapped()\r\n{\r\n\treturn;\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(2, doc.Units.Count);
			// The @addField's own var line must not double-extract as a plain member
			// declaration - the annotation-led Field unit claims it first.
			Assert.Equal(ScriptUnitKind.Field, doc.Units[0].Kind);
			Assert.Equal("injected", doc.Units[0].Name);
			Assert.Equal(ScriptUnitKind.Function, doc.Units[1].Kind);
		}

		[Fact]
		public void Extract_EnumDeclaration_IsOneUnitAndRoundTrips()
		{
			var text =
				"enum EColors\r\n{\r\n\tEC_Red,\r\n\tEC_Blue\r\n}\r\n\r\n" +
				"class Foo\r\n{\r\n\tfunction A()\r\n\t{\r\n\t\treturn;\r\n\t}\r\n}\r\n";

			var doc = ScriptUnitExtractor.Extract(text);

			// Whole enum = one unit, keyed distinctly from any same-named function -
			// a mod ADDING an enum member must go through per-unit resolution instead
			// of being silently reverted with vanilla's gap text ("I dont know any
			// 'HVS_Modcrab'", observed live).
			Assert.Equal(text, ScriptUnitExtractor.Reassemble(doc));
			Assert.Equal(new[] { "enum:EColors", "Foo::A" }, doc.Units.Select(u => u.ScopedName));
			Assert.Equal(ScriptUnitKind.EnumDeclaration, doc.Units[0].Kind);
			Assert.Contains("EC_Blue", doc.Units[0].FullText);
		}

		#endregion
	}
}

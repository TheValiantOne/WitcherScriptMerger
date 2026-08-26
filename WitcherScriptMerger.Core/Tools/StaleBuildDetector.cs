using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Tools
{
	// A single mod's script file that is missing declarations the CURRENT vanilla file
	// has - the "whole-file copy taken from an older game build" signature.
	public sealed class StaleBuildFinding
	{
		public string RelativePath { get; }
		public string ModName { get; }

		// ScriptUnitExtractor scoped names (e.g. "CR4Game::OnHDRChangedEvent"), the same
		// identity FunctionLevelMergeEngine's invariant reports, so a pre-flight warning
		// and the post-hoc violation name the same thing.
		public IReadOnlyList<string> MissingDeclarations { get; }

		public int VanillaDeclarationCount { get; }

		public StaleBuildFinding(string relativePath, string modName, IReadOnlyList<string> missing, int vanillaDeclarationCount)
		{
			RelativePath = relativePath;
			ModName = modName;
			MissingDeclarations = missing;
			VanillaDeclarationCount = vanillaDeclarationCount;
		}

		public string Describe()
		{
			var names = string.Join(", ", MissingDeclarations.Take(StaleBuildDetector.MaxNamesInDescription));
			if (MissingDeclarations.Count > StaleBuildDetector.MaxNamesInDescription)
				names += $", +{MissingDeclarations.Count - StaleBuildDetector.MaxNamesInDescription} more";

			// Deliberately does NOT claim the conflict can't be merged. This check runs
			// before any merge and can't know that: a small drift often merges fine (a
			// real install had a mod missing 1 of 224 declarations whose conflict
			// auto-solved every time), while a large one reliably trips the
			// vanilla-declaration invariant. Stating the fact and its usual consequence
			// keeps the warning honest at both ends of that range.
			return
				$"{ModName} ships a copy of {RelativePath} built against an older game version - " +
				$"it is missing {MissingDeclarations.Count} of the {VanillaDeclarationCount} declaration(s) " +
				$"the installed vanilla file has ({names}). Merging that mod's side can silently delete vanilla " +
				"code the game and other mods still call, which is the usual reason a conflict here needs manual " +
				"resolution. Update the mod to a build matching your game version, or disable it.";
		}

		public override string ToString() => Describe();
	}

	// Pre-flight counterpart to FunctionLevelMergeEngine's vanilla-declaration invariant.
	//
	// That invariant is the safety net: it fires AFTER a merge has been attempted and
	// produced output that would have dropped vanilla declarations, and its message
	// correctly guesses the cause ("...usually means that mod ships a whole-file copy
	// taken from an older game build"). But by then the user is looking at a "Skipped -
	// needs manual resolution" line, and the actionable fact - WHICH mod is out of date,
	// and that the remedy is to update/disable that mod rather than hand-merge a 2500-line
	// file - is buried inside a sentence about a DiffPlex bug.
	//
	// This runs the same comparison up front, straight off the files, with no merge
	// involved: every declaration the installed vanilla file has, that a given mod's copy
	// does not. It is deliberately a diagnostic, never a gate - a mod MAY legitimately
	// delete a vanilla function, and that case is rare enough (and interesting enough)
	// that reporting it and letting the merge proceed is the right trade. Nothing here
	// changes what does or doesn't merge.
	//
	// Verified against a real 350-mod, game-build-4.04 install: 136 mod-copy comparisons
	// across 44 conflicts produced exactly 3 findings, and those 3 were precisely the 3
	// conflicts the engine's invariant went on to decline (modFearlessRoach/exploration.ws,
	// modFastTravelFromAnywhere/mapMenu.ws, modAlwaysFullExp/r4Game.ws) - no false
	// positives, no misses.
	public static class StaleBuildDetector
	{
		public const int MaxNamesInDescription = 6;

		// Scoped names of every unit in `text`, or null if it doesn't scan cleanly.
		// Null (rather than an empty set) matters: an unscannable file must produce NO
		// finding, whereas an empty set would make every vanilla declaration look missing.
		static HashSet<string> ScopedNames(string text)
		{
			try
			{
				return new HashSet<string>(
					ScriptUnitExtractor.Extract(text).Units.Select(u => u.ScopedName),
					StringComparer.Ordinal);
			}
			catch (ScriptUnitExtractor.ExtractionException)
			{
				return null;
			}
		}

		// Declarations present in vanillaText but absent from modText. Empty when either
		// side can't be scanned - see ScopedNames.
		public static IReadOnlyList<string> FindMissingVanillaDeclarations(string vanillaText, string modText)
		{
			var vanilla = ScopedNames(vanillaText);
			var mod = ScopedNames(modText);
			if (vanilla == null || mod == null)
				return Array.Empty<string>();

			return vanilla.Where(name => !mod.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToList();
		}

		// One conflict's findings, one per source mod whose copy is missing vanilla
		// declarations. Only .ws conflicts are examined - ScriptUnitExtractor is
		// WitcherScript-specific and has no notion of XML or bundle content, exactly as
		// DiffPlexMergeEngine's own function-level rescue is gated.
		public static IReadOnlyList<StaleBuildFinding> Analyze(ModFile conflict)
		{
			if (conflict == null || conflict.Category != Categories.Script)
				return Array.Empty<StaleBuildFinding>();

			string vanillaText;
			int vanillaCount;
			try
			{
				var vanillaPath = conflict.GetVanillaFile();
				if (!File.Exists(vanillaPath))
					return Array.Empty<StaleBuildFinding>();

				vanillaText = FileEncoding.ReadAnyEncoding(vanillaPath);
				var vanillaNames = ScopedNames(vanillaText);
				if (vanillaNames == null)
					return Array.Empty<StaleBuildFinding>();
				vanillaCount = vanillaNames.Count;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				return Array.Empty<StaleBuildFinding>();
			}

			// The merged mod folder routinely shows up among a conflict's sources once a
			// file has been merged once. It is this tool's own output, not a source mod
			// shipped by anyone, so an "older game build" verdict on it would be both
			// meaningless and alarming.
			var mergedModName = Paths.RetrieveMergedModName();

			var findings = new List<StaleBuildFinding>();
			foreach (var mod in conflict.Mods)
			{
				if (mod?.Name == null || mod.Name.EqualsIgnoreCase(mergedModName))
					continue;

				try
				{
					var modPath = conflict.GetModFile(mod.Name);
					if (!File.Exists(modPath))
						continue;

					var missing = FindMissingVanillaDeclarations(vanillaText, FileEncoding.ReadAnyEncoding(modPath));
					if (missing.Count > 0)
						findings.Add(new StaleBuildFinding(conflict.RelativePath, mod.Name, missing, vanillaCount));
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{
					// A mod file that can't be read is not evidence of a stale build.
				}
			}

			return findings;
		}

		public static IReadOnlyList<StaleBuildFinding> Analyze(IEnumerable<ModFile> conflicts)
		{
			if (conflicts == null)
				return Array.Empty<StaleBuildFinding>();

			return conflicts.SelectMany(Analyze).ToList();
		}
	}
}

using System;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger
{
	// Split out of the host project's Extensions.cs during the Core/host project split -
	// these pure string helpers are used pervasively by domain code that now lives in
	// Core, while the rest of the original Extensions.cs (TreeNode/TreeView helpers,
	// Win32 P/Invoke) stayed in the host project since it's all WinForms-specific. Named
	// differently from the host's own `Extensions` class to avoid a duplicate-type
	// compile error across the two assemblies - the class name doesn't matter to call
	// sites either way, since these are extension methods resolved by namespace.
	public static class StringExtensions
	{
		public static string ReplaceIgnoreCase(this string s, string oldValue, string newValue)
		{
			return Regex.Replace(s, Regex.Escape(oldValue), newValue.Replace("$", "$$"), RegexOptions.IgnoreCase);
		}

		public static bool EqualsIgnoreCase(this string s, string otherString)
		{
			return s.Equals(otherString, StringComparison.InvariantCultureIgnoreCase);
		}

		public static int IndexOfIgnoreCase(this string s, string value, int startIndex = 0)
		{
			return s.IndexOf(value, startIndex, StringComparison.InvariantCultureIgnoreCase);
		}

		public static int LastIndexOfIgnoreCase(this string s, string value, int startIndex = -1)
		{
			if (startIndex == -1)
				startIndex = s.Length - 1;
			return s.LastIndexOf(value, startIndex, StringComparison.InvariantCultureIgnoreCase);
		}

		public static bool StartsWithIgnoreCase(this string s, string value)
		{
			return s.StartsWith(value, StringComparison.InvariantCultureIgnoreCase);
		}

		public static bool EndsWithIgnoreCase(this string s, string value)
		{
			return s.EndsWith(value, StringComparison.InvariantCultureIgnoreCase);
		}

		public static bool IsAlphaNumeric(this string s)
		{
			return new Regex("^[_a-zA-Z0-9]*$").IsMatch(s);
		}

		public static string GetPluralS(this int num)
		{
			return num == 1 ? "" : "s";
		}
	}
}

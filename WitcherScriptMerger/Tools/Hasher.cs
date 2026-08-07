using System;
using System.IO;
using System.IO.Hashing;

namespace WitcherScriptMerger.Tools
{
	static class Hasher
	{
		// xxHash32, seed 0 - matches the hand-ported implementation this replaced,
		// so hashes already recorded in existing MergeInventory.xml files stay valid.
		public static string ComputeHash(string filePath)
		{
			if (!File.Exists(filePath))
				throw new FileNotFoundException("Can't find file to hash:  " + filePath);

			var hasher = new XxHash32();

			using (var stream = File.OpenRead(filePath))
			{
				var buffer = new byte[81920];
				int bytesRead;
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					hasher.Append(buffer.AsSpan(0, bytesRead));
				}
			}

			return string.Format("{0:X}", hasher.GetCurrentHashAsUInt32());
		}
	}
}

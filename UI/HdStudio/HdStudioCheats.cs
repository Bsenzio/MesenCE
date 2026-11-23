using Mesen.Interop;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Level select cheats from the built-in cheat database (UI/Dependencies/Internal/CheatDb.Nes.json).
	/// HD Studio uses them to visit every level of a game without playing it: each cheat is applied and
	/// the game is reset, so the recorder sees the graphics of that level.
	/// </summary>
	internal static class HdStudioCheats
	{
		public sealed class Level
		{
			public string Description = "";
			public CheatType Type;
			public List<string> Codes = new();

			public string CodesText => string.Join(";", Codes);
			public bool HasCheat => Codes.Count > 0;
		}

		//"Start on stage 1-2", "Level select", "Start on World 3", "Go to level"...
		private static readonly Regex _levelPattern = new(@"(?i)\b(start (on|at|in)|level select|stage select|world select|warp|go to)\b", RegexOptions.Compiled);
		//...but it must name a stage/level/world, not lives or items
		private static readonly Regex _targetPattern = new(@"(?i)\b(stage|level|world|area|map)\b", RegexOptions.Compiled);
		private static readonly Regex _excludePattern = new(@"(?i)\b(ending|lives?|life|power|items?|coins?|flower|star|increments?)\b", RegexOptions.Compiled);
		//Raw codes write a value at a RAM address (e.g. Batman's "Start on stage 2-1" = 0400:06)
		private static readonly Regex _rawCode = new(@"^[0-9a-fA-F]{4}:[0-9a-fA-F]{2}(:[0-9a-fA-F]{2})?$", RegexOptions.Compiled);

		/// <summary>
		/// Returns one entry per level found in the cheat database, in a sensible order. The list is empty when
		/// the game has no level select codes (the caller then falls back to normal exploration).
		/// </summary>
		public static List<Level> FindLevelCheats(string romPath, Action<string> log)
		{
			List<Level> result = new();

			CheatDbGameEntry? game = FindGame(romPath, log);
			if(game == null) {
				log("Level select: this game is not in the cheat database.");
				return result;
			}

			foreach(CheatDbCheatEntry entry in game.Cheats) {
				string desc = entry.Desc ?? "";
				if(!_levelPattern.IsMatch(desc) || !_targetPattern.IsMatch(desc) || _excludePattern.IsMatch(desc)) {
					continue;
				}

				List<string> codes = entry.Code.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
				if(codes.Count == 0) {
					continue;
				}

				result.Add(new Level() {
					Description = desc,
					Codes = codes,
					Type = _rawCode.IsMatch(codes[0]) ? CheatType.NesCustom : CheatType.NesGameGenie
				});
			}

			//Raw codes that write to the same address (Batman: 0400:02, 0400:04...) are sorted by value,
			//which is the order of the stages in the game
			List<Level> raw = result.Where(l => l.Type == CheatType.NesCustom && l.Codes.Count == 1).ToList();
			if(raw.Count > 1 && raw.Select(l => l.Codes[0].Split(':')[0]).Distinct().Count() == 1) {
				result = result.OrderBy(l => l.Type == CheatType.NesCustom && l.Codes.Count == 1
					? Convert.ToInt32(l.Codes[0].Split(':')[1], 16)
					: int.MaxValue).ToList();
			}

			if(result.Count > 0) {
				log($"Level select: {result.Count} levels found in the cheat database ({game.Name}).");
			} else {
				log($"Level select: \"{game.Name}\" has no level select codes in the cheat database.");
			}
			return result;
		}

		private static CheatDbGameEntry? FindGame(string romPath, Action<string> log)
		{
			CheatDatabase db = LoadDatabase();
			if(db.Games == null || db.Games.Count == 0) {
				log("Level select: the cheat database could not be read.");
				return null;
			}

			try {
				string sha1 = EmuApi.GetRomHash(HashType.Sha1Cheat);
				CheatDbGameEntry? byHash = db.Games.FirstOrDefault(g => string.Equals(g.Sha1, sha1, StringComparison.OrdinalIgnoreCase));
				if(byHash != null) {
					log($"Level select: cheats for \"{byHash.Name}\" (the ROM matches the cheat database).");
					return byHash;
				}
			} catch {
				//The emulator may not be loaded (e.g. --reportonly): fall back to the file name
			}

			string name = Normalize(Path.GetFileNameWithoutExtension(romPath));
			CheatDbGameEntry? byName = db.Games.FirstOrDefault(g => Normalize(g.Name) == name);
			byName ??= db.Games.FirstOrDefault(g => Normalize(g.Name).StartsWith(name, StringComparison.Ordinal) || name.StartsWith(Normalize(g.Name), StringComparison.Ordinal));
			if(byName != null) {
				log($"Level select: cheats for \"{byName.Name}\", matched by name. This ROM is a different dump: some codes may not work.");
			}
			return byName;
		}

		private static CheatDatabase LoadDatabase()
		{
			try {
				string? json = DependencyHelper.GetFileContent("CheatDb.Nes.json");
				if(json != null) {
					return (CheatDatabase?)JsonSerializer.Deserialize(json, typeof(CheatDatabase), MesenCamelCaseSerializerContext.Default) ?? new CheatDatabase();
				}
			} catch {
			}
			return new CheatDatabase();
		}

		private static string Normalize(string name)
		{
			return new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
		}

		public static InteropCheatCode[] ToInteropCodes(Level level)
		{
			return level.Codes.Select(code => new InteropCheatCode(level.Type, code)).ToArray();
		}
	}
}

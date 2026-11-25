using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Rebuilds editable assets from live interactive capture observations.
	///
	/// The raw TSV files remain the source of truth. This pass performs the generalized
	/// reconstruction: hardware-sprite occurrences -> compositions -> shape identities ->
	/// temporal tracks -> animation sequences. It deliberately keeps palette variants on one
	/// asset instead of creating unrelated catalog entries, and never merges unrelated
	/// neighbours that merely happen to touch.
	/// </summary>
	internal static class HdStudioLiveAssets
	{
		private const int MaxMetasprites = 6000;
		private const int MaxBackgrounds = 4000;
		private const int MaxTracks = 800;

		private sealed class PatternInfo
		{
			public string ChrType = "";
			public int ChrAddress = -1;
			public string Data = "";
			public int Uses;
		}

		private sealed class LiveSprite
		{
			public int Frame;
			public int Oam;
			public int Part;
			public int X;
			public int Y;
			public int Tile;
			public string Palette = "";
			public bool FlipH;
			public bool FlipV;
			public bool Priority;
			public string Pattern = "";
			public string ChrType = "";
			public int ChrAddress = -1;
		}

		private sealed class Composition
		{
			public readonly List<LiveSprite> Sprites = new();
			public string Shape = "";
			public string Geometry = "";
			public int X;
			public int Y;
			public int Width;
			public int Height;
			public HashSet<string> Patterns = new(StringComparer.OrdinalIgnoreCase);
		}

		private sealed class Track
		{
			public int Id;
			public int LastFrame;
			public int LastX;
			public int LastY;
			public readonly List<(string Shape, int Frame)> Shapes = new();
			public readonly HashSet<string> ShapeSet = new(StringComparer.Ordinal);
			public Dictionary<string, Composition> ByShape = new(StringComparer.Ordinal);
		}

		public static void Import(string studio, string romKey, NesRomData rom, List<HdStudioAssets.Asset> assets, Action<string> log)
		{
			string live = Path.Combine(studio, "live");
			if(!Directory.Exists(live)) {
				return;
			}
			Dictionary<string, PatternInfo> patterns = ReadPatterns(live);
			if(patterns.Count == 0) {
				return;
			}

			Dictionary<string, HdStudioAssets.Asset> metasprites = new(StringComparer.Ordinal);
			Dictionary<string, List<string>> shapePalettes = new(StringComparer.Ordinal);
			Dictionary<string, Composition> compositions = new(StringComparer.Ordinal);
			int liveFrames = 0;
			int occurrenceCount = 0;

			foreach(List<LiveSprite> frameSprites in ReadSpriteFrames(Path.Combine(live, "live-sprites.tsv"))) {
				liveFrames++;
				occurrenceCount += frameSprites.Count;
				List<Composition> frameCompositions = Reconstruct(frameSprites);
				foreach(Composition composition in frameCompositions) {
					if(composition.Sprites.Count == 0 || composition.Sprites.Count > 32) {
						continue;
					}
					if(!compositions.ContainsKey(composition.Shape)) {
						if(compositions.Count >= MaxMetasprites) {
							continue;
						}
						compositions[composition.Shape] = composition;
					}
				}
			}

			foreach(var pair in compositions.OrderBy(p => p.Value.Sprites.Min(s => s.Frame)).ThenBy(p => p.Key)) {
				if(metasprites.Count >= MaxMetasprites) {
					break;
				}
				Composition composition = pair.Value;
				string id = "liveMetasprite:" + romKey + ":" + ShortHash(pair.Key);
				if(metasprites.ContainsKey(id)) {
					continue;
				}
				HdStudioAssets.Asset asset = BuildAsset(id, pair.Key, composition, patterns, rom);
				asset.Kind = "metasprite";
				asset.Name = "Live object " + (metasprites.Count + 1) + " (" + composition.Sprites.Count + " parts)";
				asset.Validation = "observed-live";
				metasprites[id] = asset;
				assets.Add(asset);
			}

			//Temporal grouping: rebuild short animation tracks from the same observations.
			//A track is a sequence of different shapes that appear in the same screen area and
			//share at least one pattern. This is evidence, not a semantic guarantee.
			List<Track> tracks = new();
			Dictionary<string, HdStudioAssets.Asset> framesByKey = new(StringComparer.Ordinal);
			int trackId = 0;
			foreach(List<LiveSprite> frameSprites in ReadSpriteFrames(Path.Combine(live, "live-sprites.tsv"))) {
				int frame = frameSprites.Count > 0 ? frameSprites[0].Frame : 0;
				foreach(Composition composition in Reconstruct(frameSprites)) {
					Track? best = null;
					int bestDistance = int.MaxValue;
					foreach(Track track in tracks.Where(t => frame - t.LastFrame <= 12)) {
						if(!SharesPattern(track, composition)) {
							continue;
						}
						int distance = Math.Abs(track.LastX - composition.X) + Math.Abs(track.LastY - composition.Y);
						if(distance < 48 && distance < bestDistance) {
							bestDistance = distance;
							best = track;
						}
					}
					if(best == null) {
						if(tracks.Count >= MaxTracks) {
							continue;
						}
						best = new Track() { Id = ++trackId };
						tracks.Add(best);
					}
					best.LastFrame = frame;
					best.LastX = composition.X;
					best.LastY = composition.Y;
					best.ByShape[composition.Shape] = composition;
					if(best.ShapeSet.Add(composition.Shape)) {
						best.Shapes.Add((composition.Shape, frame));
					}
				}
			}

			int sequences = 0;
			foreach(Track track in tracks.Where(t => t.ShapeSet.Count >= 3).Take(200)) {
				string sequenceId = "liveSequence:" + romKey + ":" + track.Id;
				HdStudioAssets.Asset sequence = new() {
					Id = sequenceId,
					Kind = "animationSequence",
					Name = "Live animation " + (++sequences) + " (" + track.ShapeSet.Count + " frames)",
					Source = "liveCapture",
					Provenance = $"live capture temporal track {track.Id}, {track.Shapes.Count} observations",
					Confidence = 0.35,
					Validation = "observed-live",
					AnimationId = sequenceId
				};
				sequence.ValidationNotes.Add("Grouped because the compositions appear consecutively in the same screen area and share patterns.");
				sequence.ValidationNotes.Add("Frame order is observation order, not a claim about the game's internal animation timing.");
				assets.Add(sequence);

				int index = 0;
				foreach((string shape, int frame) in track.Shapes) {
					if(!track.ByShape.TryGetValue(shape, out Composition? composition)) {
						continue;
					}
					string frameKey = sequenceId + ":" + index;
					if(framesByKey.ContainsKey(frameKey)) {
						index++;
						continue;
					}
					HdStudioAssets.Asset frameAsset = BuildAsset(frameKey, shape, composition, patterns, rom);
					frameAsset.Kind = "animationFrame";
					frameAsset.Name = "Live animation " + sequences + " frame " + (index + 1);
					frameAsset.ParentId = sequenceId;
					frameAsset.AnimationId = sequenceId;
					frameAsset.FrameIndex = index;
					frameAsset.Validation = "observed-live";
					frameAsset.Provenance = $"live frame {frame} in temporal track {track.Id}";
					framesByKey[frameKey] = frameAsset;
					assets.Add(frameAsset);
					index++;
				}
			}

			ReadBackgrounds(live, romKey, rom, assets, log);

			if(liveFrames > 0) {
				log($"Assets: live capture rebuilt {metasprites.Count} compositions from {liveFrames:N0} sampled frames / {occurrenceCount:N0} sprite occurrences, {sequences} animation track(s).");
			}
		}

		private static HdStudioAssets.Asset BuildAsset(string id, string shape, Composition composition, Dictionary<string, PatternInfo> patterns, NesRomData rom)
		{
			List<HdStudioAssets.TileRef> tiles = new();
			foreach(LiveSprite sprite in composition.Sprites.OrderBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Oam)) {
				patterns.TryGetValue(sprite.Pattern, out PatternInfo? info);
				string data = info?.Data ?? "";
				if(data.Length != 32 && sprite.Pattern.Length == 32) {
					data = sprite.Pattern;
				}
				tiles.Add(new HdStudioAssets.TileRef() {
					X = sprite.X - composition.X,
					Y = sprite.Y - composition.Y,
					Pattern = sprite.Pattern,
					TileData = data.Length == 32 ? data : null,
					Palette = sprite.Palette,
					FlipH = sprite.FlipH,
					FlipV = sprite.FlipV,
					Priority = sprite.Priority,
					ChrAddr = info?.ChrAddress ?? sprite.ChrAddress,
					ChrType = info?.ChrType ?? sprite.ChrType
				});
			}
			HdStudioAssets.Asset asset = new() {
				Id = id,
				Kind = "metasprite",
				Source = "liveCapture",
				Provenance = "live capture composition; mapper-resolved OAM and CHR",
				Confidence = 0.6,
				Validation = "observed-live",
				Width = composition.Width,
				Height = composition.Height,
				Tiles = tiles
			};
			foreach(string palette in composition.Sprites.Select(s => s.Palette).Distinct()) {
				asset.PaletteVariants.Add(palette);
			}
			asset.ValidationNotes.Add("Composition grouped from hardware sprites by overlap, OAM adjacency, palette compatibility and alignment.");
			asset.ValidationNotes.Add("Palette variants share one catalog record; exact pattern+palette keys remain separate in the pack.");
			return asset;
		}

		private static bool SharesPattern(Track track, Composition composition)
		{
			foreach(Composition existing in track.ByShape.Values) {
				if(existing.Patterns.Overlaps(composition.Patterns)) {
					return true;
				}
			}
			return false;
		}

		private static List<Composition> Reconstruct(List<LiveSprite> sprites)
		{
			List<Composition> result = new();
			if(sprites.Count == 0) {
				return result;
			}

			int count = sprites.Count;
			int[] parent = Enumerable.Range(0, count).ToArray();
			int[] size = Enumerable.Repeat(1, count).ToArray();
			int Find(int i)
			{
				while(parent[i] != i) {
					parent[i] = parent[parent[i]];
					i = parent[i];
				}
				return i;
			}
			void Union(int i, int j)
			{
				int a = Find(i);
				int b = Find(j);
				if(a == b || size[a] + size[b] > 16) {
					return;
				}
				parent[a] = b;
				size[b] += size[a];
			}

			for(int i = 0; i < count; i++) {
				for(int j = i + 1; j < count; j++) {
					LiveSprite a = sprites[i];
					LiveSprite b = sprites[j];
					if(a.Oam == b.Oam) {
						Union(i, j);
						continue;
					}
					int left = Math.Max(a.X, b.X);
					int right = Math.Min(a.X + 8, b.X + 8);
					int top = Math.Max(a.Y, b.Y);
					int bottom = Math.Min(a.Y + 8, b.Y + 8);
					int overlap = (right > left && bottom > top) ? (right - left) * (bottom - top) : 0;
					if(overlap >= 2) {
						Union(i, j);
						continue;
					}
					int dx = Math.Abs(a.X - b.X);
					int dy = Math.Abs(a.Y - b.Y);
					int oamDistance = Math.Abs(a.Oam - b.Oam);
					if(dx <= 9 && dy <= 9 && (dx <= 8 || dy <= 8) && a.Palette == b.Palette && oamDistance <= 6) {
						Union(i, j);
					}
				}
			}

			Dictionary<int, List<LiveSprite>> groups = new();
			for(int i = 0; i < count; i++) {
				int root = Find(i);
				if(!groups.TryGetValue(root, out List<LiveSprite>? list)) {
					groups[root] = list = new List<LiveSprite>();
				}
				list.Add(sprites[i]);
			}

			foreach(List<LiveSprite> group in groups.Values) {
				if(group.Count == 0 || group.Count > 32) {
					continue;
				}
				Composition composition = new() {
					X = group.Min(s => s.X),
					Y = group.Min(s => s.Y)
				};
				composition.Width = Math.Max(8, group.Max(s => s.X + 8) - composition.X);
				composition.Height = Math.Max(8, group.Max(s => s.Y + 8) - composition.Y);
				composition.Sprites.AddRange(group.OrderBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Oam));
				StringBuilder shape = new();
				StringBuilder geometry = new();
				foreach(LiveSprite sprite in composition.Sprites) {
					int flags = (sprite.FlipH ? 1 : 0) | (sprite.FlipV ? 2 : 0) | (sprite.Priority ? 4 : 0);
					shape.Append(sprite.X - composition.X).Append(',').Append(sprite.Y - composition.Y)
						.Append(',').Append(sprite.Pattern).Append(',').Append(flags).Append(';');
					geometry.Append(sprite.X - composition.X).Append(',').Append(sprite.Y - composition.Y).Append(';');
					composition.Patterns.Add(sprite.Pattern);
				}
				composition.Shape = shape.ToString();
				composition.Geometry = geometry.ToString();
				result.Add(composition);
			}
			return result;
		}

		private static Dictionary<string, PatternInfo> ReadPatterns(string live)
		{
			Dictionary<string, PatternInfo> result = new(StringComparer.OrdinalIgnoreCase);
			string path = Path.Combine(live, "live-patterns.tsv");
			if(!File.Exists(path)) {
				return result;
			}
			foreach(string line in File.ReadLines(path)) {
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] p = line.Split('\t');
				if(p.Length < 4 || p[0].Length == 0) {
					continue;
				}
				result[p[0].ToUpperInvariant()] = new PatternInfo() {
					ChrType = p[1],
					ChrAddress = ParseInt(p[2], -1),
					Data = p[3],
					Uses = p.Length > 5 ? ParseInt(p[5], 0) : 0
				};
			}
			return result;
		}

		private static IEnumerable<List<LiveSprite>> ReadSpriteFrames(string path)
		{
			if(!File.Exists(path)) {
				yield break;
			}
			List<LiveSprite> current = new();
			int currentFrame = -1;
			foreach(string line in File.ReadLines(path)) {
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] p = line.Split('\t');
				if(p.Length < 10 || !int.TryParse(p[0], out int frame)) {
					continue;
				}
				//A new capture session restarts frame numbering at 1; flush the old frame.
				if(frame < currentFrame && current.Count > 0) {
					yield return current;
					current = new List<LiveSprite>();
				}
				if(frame != currentFrame && current.Count > 0) {
					yield return current;
					current = new List<LiveSprite>();
				}
				currentFrame = frame;
				current.Add(new LiveSprite() {
					Frame = frame,
					Oam = ParseInt(p[1], 0),
					Part = ParseInt(p[2], 0),
					X = ParseInt(p[3], 0),
					Y = ParseInt(p[4], 0),
					Tile = ParseInt(p[5], 0),
					Palette = p[6].ToUpperInvariant(),
					FlipH = p[7] == "1",
					FlipV = p[8] == "1",
					Priority = p[9] == "1",
					Pattern = p[10],
					ChrType = p.Length > 11 ? p[11] : "",
					ChrAddress = p.Length > 12 ? ParseInt(p[12], -1) : -1
				});
			}
			if(current.Count > 0) {
				yield return current;
			}
		}

		private static void ReadBackgrounds(string live, string romKey, NesRomData rom, List<HdStudioAssets.Asset> assets, Action<string> log)
		{
			string path = Path.Combine(live, "live-backgrounds.tsv");
			if(!File.Exists(path)) {
				return;
			}
			Dictionary<string, HdStudioAssets.Asset> byShape = new(StringComparer.Ordinal);
			foreach(string line in File.ReadLines(path)) {
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] p = line.Split('\t');
				if(p.Length < 10) {
					continue;
				}
				string palette = p[0].ToUpperInvariant();
				if(palette.Length != 8) {
					continue;
				}
				string[] keys = { p[2], p[3], p[4], p[5] };
				string[] data = { p[6], p[7], p[8], p[9] };
				string shape = string.Join("|", keys.Select(k => k.ToUpperInvariant()));
				if(shape.Replace("|", "").Length == 0) {
					continue;
				}
				if(byShape.TryGetValue(shape, out HdStudioAssets.Asset? existing)) {
					if(!existing.PaletteVariants.Contains(palette)) {
						existing.PaletteVariants.Add(palette);
					}
					continue;
				}
				if(byShape.Count >= MaxBackgrounds) {
					continue;
				}
				int uses = ParseInt(p[1], 0);
				HdStudioAssets.Asset asset = new() {
					Id = "liveBackground:" + romKey + ":" + ShortHash(shape + "|" + palette),
					Kind = "backgroundBlock",
					Name = "Live background " + (byShape.Count + 1) + " (" + palette + ")",
					Source = "liveCapture",
					Provenance = $"live background observation, {uses} sample(s)",
					Confidence = 0.6,
					Validation = "observed-live",
					Width = 16,
					Height = 16
				};
				int[] posX = { 0, 8, 0, 8 };
				int[] posY = { 0, 0, 8, 8 };
				for(int i = 0; i < 4; i++) {
					if(data[i] == new string('0', 32)) {
						continue;
					}
					asset.Tiles.Add(new HdStudioAssets.TileRef() {
						X = posX[i],
						Y = posY[i],
						Pattern = keys[i].ToUpperInvariant(),
						TileData = data[i].Length == 32 ? data[i] : null,
						Palette = palette,
						ChrType = rom.HasChrRom ? "NesChrRom" : "NesChrRam"
					});
				}
				if(asset.Tiles.Count == 0) {
					continue;
				}
				asset.PaletteVariants.Add(palette);
				asset.ValidationNotes.Add("Observed 2x2 nametable block, scroll-aware and read through the mapper's mirroring.");
				asset.ValidationNotes.Add("Palette variants are merged into one record; exact keys remain separate in the pack.");
				byShape[shape] = asset;
				assets.Add(asset);
			}
			if(byShape.Count > 0) {
				log($"Assets: {byShape.Count} live background blocks with merged palette variants");
			}
		}

		private static int ParseInt(string value, int fallback)
		{
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
		}

		private static string ShortHash(string value)
		{
			byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
			return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
		}
	}
}

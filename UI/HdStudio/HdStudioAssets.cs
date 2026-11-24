using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesen.HdStudio
{
	/// <summary>
	/// HD Studio: versioned, game-independent asset representation.
	///
	/// The exporter keeps the raw observations (pattern, tile occurrence, hardware sprite,
	/// metasprite) separate from semantic labels. SMB3 names and player-form poses are
	/// profile annotations; the generic metasprites found in objects.txt are automatic
	/// discoveries and are marked as such.
	/// </summary>
	internal static class HdStudioAssets
	{
		public const string SchemaVersion = "1.0.0";

		private class TileRef
		{
			public int X;
			public int Y;
			public string Pattern = "";
			public string? TileData;
			public string Palette = "";
			public bool FlipH;
			public bool FlipV;
			public bool Priority;
			public int ChrAddr = -1;
			public string ChrType = "";
		}

		private class Asset
		{
			public string Id = "";
			public string Kind = "";
			public string Name = "";
			public string Label = "";
			public string Source = "automatic";
			public string Provenance = "";
			public double Confidence;
			public string Validation = "";
			public List<string> ValidationNotes = new();
			public int Width;
			public int Height;
			public int OriginX;
			public int OriginY;
			public string OriginalImage = "";
			public List<TileRef> Tiles = new();
			public List<string> PaletteVariants = new();
			public bool HasTransparency = true;
			public string? ParentId;
			public int? FrameIndex;
			public string? AnimationId;
		}

		public static int Export(string romPath, string outFolder, Action<string> log)
		{
			string studio = Path.Combine(outFolder, HdStudioReport.StudioFolderName);
			string assetsFolder = Path.Combine(studio, "assets");
			Directory.CreateDirectory(assetsFolder);
			NesRomData rom = NesRomData.Load(romPath);
			string romKey = rom.Sha1.Length >= 8 ? rom.Sha1.Substring(0, 8).ToLowerInvariant() : "unknown";

			List<Asset> assets = new();
			ReadDiscoveredMetasprites(studio, romKey, assets, log);
			ReadObjectAtlas(studio, romKey, assets, log);
			ReadPlayerAtlas(studio, romKey, assets, log);

			using(FileStream stream = File.Create(Path.Combine(assetsFolder, "assets.json"))) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = true });
				json.WriteStartObject();
				json.WriteString("schemaVersion", SchemaVersion);
				json.WriteString("generatedBy", "Mesen HD Studio");
				json.WriteString("generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
				json.WriteStartObject("game");
				json.WriteString("romSha1", rom.Sha1);
				json.WriteString("romSha1NoHeader", rom.Sha1NoHeader);
				json.WriteNumber("mapper", rom.Mapper);
				json.WriteNumber("chrRomBytes", rom.ChrSize);
				json.WriteBoolean("hasChrRom", rom.HasChrRom);
				json.WriteEndObject();

				json.WriteStartArray("assets");
				foreach(Asset asset in assets.OrderBy(a => a.Kind).ThenBy(a => a.Id)) {
					WriteAsset(json, asset);
				}
				json.WriteEndArray();

				//Replacement and condition records are populated by the importer; keeping the arrays
				//in the schema from the start makes the format stable.
				json.WriteStartArray("replacementAssets");
				json.WriteEndArray();
				json.WriteStartArray("replacementConditions");
				json.WriteEndArray();
				json.WriteEndObject();
			}

			log($"Assets: {assets.Count} records ({assets.Count(a => a.Kind == "metasprite")} generic metasprites, {assets.Count(a => a.Kind == "semanticObject")} semantic objects, {assets.Count(a => a.Kind == "animationFrame")} animation frames) -> {assetsFolder}");
			return 0;
		}

		private static void ReadDiscoveredMetasprites(string studio, string romKey, List<Asset> assets, Action<string> log)
		{
			string path = Path.Combine(studio, "objects.txt");
			if(!File.Exists(path)) {
				return;
			}

			Dictionary<string, Asset> byShape = new();
			foreach(string line in File.ReadLines(path)) {
				if(line.Length == 0 || line[0] == '#') {
					continue;
				}
				string[] parts = line.Split('\t');
				if(parts.Length < 5) {
					continue;
				}
				if(!int.TryParse(parts[0], out int frameCount) ||
					!int.TryParse(parts[1], out int firstFrame) ||
					!int.TryParse(parts[2], out int width) ||
					!int.TryParse(parts[3], out int height)) {
					continue;
				}

				List<TileRef> tiles = new();
				foreach(string spriteDef in parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries)) {
					string[] f = spriteDef.Split(',');
					if(f.Length < 5) {
						continue;
					}
					if(!int.TryParse(f[0], out int dx) || !int.TryParse(f[1], out int dy)) {
						continue;
					}
					string tile = f[2];
					string palette = f[3];
					int flags = ParseHex(f[4]);
					TileRef tileRef = new() {
						X = dx, Y = dy,
						Pattern = tile.Length <= 4 ? tile : "",
						TileData = tile.Length == 32 ? tile : null,
						Palette = palette,
						FlipH = (flags & 1) != 0,
						FlipV = (flags & 2) != 0,
						ChrType = tile.Length == 32 ? "NesChrRam" : "NesChrRom"
					};
					tiles.Add(tileRef);
				}
				if(tiles.Count == 0 || tiles.Count > 32) {
					//A full OAM wrap of blank sprites is not a semantic object.
					continue;
				}
				if(tiles.All(t => t.Pattern.Equals("00", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(t.TileData))) {
					continue;
				}

				string shapeKey = string.Join(";", tiles.Select(t => $"{t.X},{t.Y},{t.Pattern},{t.TileData},{(t.FlipH ? 1 : 0) | (t.FlipV ? 2 : 0)}"));
				string id = "metasprite:" + romKey + ":" + ShortHash(shapeKey);
				if(!byShape.TryGetValue(shapeKey, out Asset? asset)) {
					asset = new Asset() {
						Id = id,
						Kind = "metasprite",
						Name = "Discovered metasprite " + (byShape.Count + 1),
						Label = "",
						Source = "automatic",
						Provenance = $"objects.txt frame {firstFrame}, seen {frameCount} frames",
						Confidence = 0.5,
						Validation = "verified-composition",
						Width = width,
						Height = height,
						Tiles = tiles
					};
					asset.ValidationNotes.Add("OAM composition comes from the emulator's rendered frame.");
					asset.ValidationNotes.Add("Tile identity is mapper-resolved; semantic name is not known.");
					byShape[shapeKey] = asset;
					assets.Add(asset);
				}
				foreach(string palette in parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries)
					.Select(s => s.Split(','))
					.Where(p => p.Length >= 5)
					.Select(p => p[3])
					.Distinct()) {
					if(palette.Length > 0 && !asset.PaletteVariants.Contains(palette)) {
						asset.PaletteVariants.Add(palette);
					}
				}
			}

			if(byShape.Count > 0) {
				log($"Assets: {byShape.Count} automatic metasprites from objects.txt");
			}
		}

		private static void ReadObjectAtlas(string studio, string romKey, List<Asset> assets, Action<string> log)
		{
			string atlasPath = Path.Combine(studio, "atlas", "atlas.json");
			string spritesFolder = Path.Combine(studio, "sprites");
			if(!File.Exists(atlasPath)) {
				return;
			}

			Dictionary<int, List<TileRef>> atlasTiles = new();
			if(Directory.Exists(spritesFolder)) {
				foreach(string csv in Directory.GetFiles(spritesFolder, "*.csv")) {
					string[] lines = File.ReadAllLines(csv);
					if(lines.Length < 2 || !lines[0].Contains("chrAddr", StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					foreach(string line in lines.Skip(1)) {
						string[] p = line.Split(',');
						if(p.Length < 16 || !int.TryParse(p[0], out int objectId)) {
							continue;
						}
						int chrAddr = ParseHex(p[4]);
						string tileData = p[6];
						int pattern = ParseHex(p[7]);
						int attr = ParseHex(p[13]);
						TileRef tile = new() {
							X = ParseInt(p[9]), Y = ParseInt(p[10]),
							Pattern = pattern.ToString("X", CultureInfo.InvariantCulture),
							TileData = tileData.Length == 32 ? tileData : null,
							Palette = p[8],
							FlipH = (attr & 0x40) != 0,
							FlipV = (attr & 0x80) != 0,
							Priority = (attr & 0x20) != 0,
							ChrAddr = chrAddr,
							ChrType = p[5]
						};
						if(!atlasTiles.TryGetValue(objectId, out List<TileRef>? list)) {
							list = new List<TileRef>();
							atlasTiles[objectId] = list;
						}
						list.Add(tile);
					}
				}
			}

			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(atlasPath));
				if(!doc.RootElement.TryGetProperty("objects", out JsonElement list)) {
					return;
				}
				foreach(JsonElement obj in list.EnumerateArray()) {
					int id = obj.TryGetProperty("id", out JsonElement i) ? i.GetInt32() : 0;
					string name = obj.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
					string label = obj.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? "" : "";
					string png = obj.TryGetProperty("png", out JsonElement p) ? p.GetString() ?? "" : "";
					int width = obj.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
					int height = obj.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
					int sprites = obj.TryGetProperty("sprites", out JsonElement s) ? s.GetInt32() : 0;
					string status = obj.TryGetProperty("status", out JsonElement st) ? st.GetString() ?? "" : "";
					if(sprites == 0) {
						continue;
					}

					Asset asset = new() {
						Id = "semanticObject:" + romKey + ":atlas:" + id,
						Kind = "semanticObject",
						Name = name,
						Label = label,
						Source = string.IsNullOrEmpty(label) ? "automatic" : "gameProfile",
						Provenance = $"object atlas id {id}, forced game draw",
						Confidence = status.StartsWith("verified", StringComparison.Ordinal) ? 0.9 : 0.4,
						Validation = status,
						Width = width,
						Height = height,
						OriginalImage = png
					};

					if(atlasTiles.TryGetValue(id, out List<TileRef>? tiles)) {
						//Use the object's own OAM rows; never borrow a same-sized generic metasprite.
						int minX = tiles.Min(t => t.X);
						int minY = tiles.Min(t => t.Y);
						foreach(TileRef tile in tiles) {
							tile.X -= minX;
							tile.Y -= minY;
						}
						asset.Tiles = tiles;
						asset.OriginX = minX;
						asset.OriginY = minY;
						foreach(string palette in tiles.Select(t => t.Palette).Distinct()) {
							asset.PaletteVariants.Add(palette);
						}
					}

					assets.Add(asset);
				}
			} catch(Exception ex) {
				log("Assets: atlas could not be read: " + ex.Message);
			}
		}

		private static void ReadPlayerAtlas(string studio, string romKey, List<Asset> assets, Action<string> log)
		{
			string path = Path.Combine(studio, "playerforms", "playerforms.json");
			if(!File.Exists(path)) {
				return;
			}

			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				if(!doc.RootElement.TryGetProperty("forms", out JsonElement forms)) {
					return;
				}
				foreach(JsonElement form in forms.EnumerateArray()) {
					int formId = form.TryGetProperty("id", out JsonElement fid) ? fid.GetInt32() : 0;
					string formName = form.TryGetProperty("name", out JsonElement fn) ? fn.GetString() ?? "" : "";
					string sequenceId = "animationSequence:" + romKey + ":player:" + formId;
					Asset sequence = new() {
						Id = sequenceId,
						Kind = "animationSequence",
						Name = formName + " animation",
						Source = "gameProfile",
						Provenance = "playerforms.json / Player_Draw forced frames",
						Confidence = 0.9,
						Validation = "verified-composition",
						AnimationId = sequenceId
					};
					assets.Add(sequence);

					if(form.TryGetProperty("poses", out JsonElement poses)) {
						int index = 0;
						foreach(JsonElement pose in poses.EnumerateArray()) {
							int code = pose.TryGetProperty("code", out JsonElement pc) ? pc.GetInt32() : 0;
							string poseName = pose.TryGetProperty("name", out JsonElement pn) ? pn.GetString() ?? "" : "";
							string png = pose.TryGetProperty("png", out JsonElement pp) ? pp.GetString() ?? "" : "";
							int width = pose.TryGetProperty("width", out JsonElement pw) ? pw.GetInt32() : 0;
							int height = pose.TryGetProperty("height", out JsonElement ph) ? ph.GetInt32() : 0;
							int sprites = pose.TryGetProperty("sprites", out JsonElement ps) ? ps.GetInt32() : 0;
							Asset frame = new() {
								Id = "animationFrame:" + romKey + ":player:" + formId + ":" + code.ToString("X2", CultureInfo.InvariantCulture),
								Kind = "animationFrame",
								Name = formName + " " + poseName,
								Source = "gameProfile",
								Provenance = "playerforms.json pose " + code.ToString("X2", CultureInfo.InvariantCulture),
								Confidence = 0.9,
								Validation = "verified-composition",
								Width = width,
								Height = height,
								OriginalImage = png,
								ParentId = sequenceId,
								FrameIndex = index++,
								AnimationId = sequenceId
							};
							frame.ValidationNotes.Add($"Captured {sprites} OAM sprites through the game's own Player_Draw routine.");
							assets.Add(frame);
						}
					}
				}
				log("Assets: player forms exported as animation sequences and frames");
			} catch(Exception ex) {
				log("Assets: player forms could not be read: " + ex.Message);
			}
		}

		private static void WriteAsset(Utf8JsonWriter json, Asset asset)
		{
			json.WriteStartObject();
			json.WriteString("id", asset.Id);
			json.WriteString("kind", asset.Kind);
			json.WriteString("name", asset.Name);
			json.WriteString("label", asset.Label);
			json.WriteString("source", asset.Source);
			json.WriteString("provenance", asset.Provenance);
			json.WriteNumber("confidence", Math.Round(asset.Confidence, 2));
			json.WriteStartObject("validation");
			json.WriteString("status", asset.Validation);
			json.WriteStartArray("notes");
			foreach(string note in asset.ValidationNotes) {
				json.WriteStringValue(note);
			}
			json.WriteEndArray();
			json.WriteEndObject();

			json.WriteStartObject("dimensions");
			json.WriteNumber("width", asset.Width);
			json.WriteNumber("height", asset.Height);
			json.WriteNumber("originX", asset.OriginX);
			json.WriteNumber("originY", asset.OriginY);
			json.WriteEndObject();

			if(asset.Tiles.Count > 0) {
				int minX = asset.Tiles.Min(t => t.X);
				int minY = asset.Tiles.Min(t => t.Y);
				int maxX = asset.Tiles.Max(t => t.X + 8);
				int maxY = asset.Tiles.Max(t => t.Y + 8);
				json.WriteStartObject("boundingBox");
				json.WriteNumber("x", minX);
				json.WriteNumber("y", minY);
				json.WriteNumber("width", maxX - minX);
				json.WriteNumber("height", maxY - minY);
				json.WriteEndObject();

				json.WriteStartObject("anchor");
				json.WriteNumber("x", 0);
				json.WriteNumber("y", 0);
				json.WriteEndObject();
			}

			json.WriteStartObject("transparency");
			json.WriteBoolean("hasTransparency", asset.HasTransparency);
			json.WriteNumber("transparentColorIndex", 0);
			json.WriteEndObject();

			json.WriteStartArray("paletteVariants");
			foreach(string palette in asset.PaletteVariants.Distinct()) {
				json.WriteStringValue(palette);
			}
			json.WriteEndArray();

			json.WriteStartArray("tiles");
			foreach(TileRef tile in asset.Tiles) {
				json.WriteStartObject();
				json.WriteString("pattern", tile.Pattern);
				if(tile.TileData != null) {
					json.WriteString("tileData", tile.TileData);
				}
				json.WriteNumber("chrAddress", tile.ChrAddr);
				json.WriteString("chrType", tile.ChrType);
				json.WriteNumber("x", tile.X);
				json.WriteNumber("y", tile.Y);
				json.WriteBoolean("flipH", tile.FlipH);
				json.WriteBoolean("flipV", tile.FlipV);
				json.WriteBoolean("backgroundPriority", tile.Priority);
				json.WriteString("palette", tile.Palette);
				json.WriteEndObject();
			}
			json.WriteEndArray();

			if(asset.ParentId != null) {
				json.WriteString("parentId", asset.ParentId);
			}
			if(asset.FrameIndex != null) {
				json.WriteNumber("frameIndex", asset.FrameIndex.Value);
			}
			if(asset.AnimationId != null) {
				json.WriteString("animationId", asset.AnimationId);
			}
			json.WriteString("originalImage", asset.OriginalImage);
			json.WriteEndObject();
		}

		private static string ShortHash(string value)
		{
			byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
			return Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant();
		}

		private static int ParseHex(string value)
		{
			return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}

		private static int ParseInt(string value)
		{
			return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
		}
	}
}

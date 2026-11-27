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
	/// Persistent, versioned Graphical Evidence Graph (GEG).
	///
	/// The graph is the shared internal model of RetroArchaeology. It is deliberately
	/// separate from the editable asset catalog: the catalog remains the artist-facing
	/// projection, while the graph keeps source offsets, mapper assumptions, confidence,
	/// supporting/contradicting witnesses and unresolved gaps. A graph database server is
	/// not required; a sparse JSON document per ROM is sufficient at this scale.
	///
	/// Updates are transactional (temp file + replace + previous-version backup) and
	/// deterministic: node/edge ids are content-derived and never depend on timestamps.
	/// </summary>
	internal sealed class HdStudioEvidenceGraph
	{
		public const string SchemaVersion = "1.0.0";
		public const string EngineVersion = "0.9.0-archaeology";
		public const string AlgorithmVersion = "static-first-1";

		public string Schema = SchemaVersion;
		public string EngineVersionValue = EngineVersion;
		public string AlgorithmVersionValue = AlgorithmVersion;
		public string GeneratedAtUtc = "";
		public string UpdatedAtUtc = "";
		public string EngineFingerprint = "";
		public string RunFingerprint = "";
		public string RomSha256 = "";
		public string RomSha1 = "";
		public string RomFileName = "";
		public string RomPath = "";
		public int Mapper;
		public int Submapper;
		public string ChrMode = "";
		public long PrgBytes;
		public long ChrBytes;
		public bool Nes20;
		public bool HasTrainer;
		public int FileBytes;

		public readonly Dictionary<string, HdStudioEvidenceNode> Nodes = new(StringComparer.Ordinal);
		public readonly Dictionary<string, HdStudioEvidenceEdge> Edges = new(StringComparer.Ordinal);
		public readonly Dictionary<string, HdStudioGap> Gaps = new(StringComparer.Ordinal);
		public readonly List<string> Notes = new();

		public static string GraphFolder(string packFolder)
		{
			return Path.Combine(packFolder, HdStudioReport.StudioFolderName, "archaeology");
		}

		public static string GetPath(string packFolder)
		{
			return Path.Combine(GraphFolder(packFolder), "evidence-graph.json");
		}

		public static HdStudioEvidenceGraph Load(string path)
		{
			HdStudioEvidenceGraph graph = new();
			if(!File.Exists(path)) {
				return graph;
			}
			try {
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
				JsonElement root = doc.RootElement;
				graph.Schema = GetString(root, "schema", SchemaVersion);
				graph.EngineVersionValue = GetString(root, "engineVersion", EngineVersion);
				graph.AlgorithmVersionValue = GetString(root, "algorithmVersion", AlgorithmVersion);
				graph.GeneratedAtUtc = GetString(root, "generatedAtUtc");
				graph.UpdatedAtUtc = GetString(root, "updatedAtUtc");
				graph.EngineFingerprint = GetString(root, "engineFingerprint");
				graph.RunFingerprint = GetString(root, "runFingerprint");
				graph.RomSha256 = GetString(root, "romSha256");
				graph.RomSha1 = GetString(root, "romSha1");
				graph.RomFileName = GetString(root, "romFileName");
				graph.RomPath = GetString(root, "romPath");
				graph.Mapper = GetInt(root, "mapper");
				graph.Submapper = GetInt(root, "submapper");
				graph.ChrMode = GetString(root, "chrMode");
				graph.PrgBytes = GetLong(root, "prgBytes");
				graph.ChrBytes = GetLong(root, "chrBytes");
				graph.Nes20 = GetBool(root, "nes20");
				graph.HasTrainer = GetBool(root, "hasTrainer");
				graph.FileBytes = GetInt(root, "fileBytes");
				if(root.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement note in notes.EnumerateArray()) {
						if(note.ValueKind == JsonValueKind.String) {
							graph.Notes.Add(note.GetString() ?? "");
						}
					}
				}
				if(root.TryGetProperty("nodes", out JsonElement nodeList) && nodeList.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement element in nodeList.EnumerateArray()) {
						HdStudioEvidenceNode? node = HdStudioEvidenceNode.FromJson(element);
						if(node != null) {
							graph.Nodes[node.Id] = node;
						}
					}
				}
				if(root.TryGetProperty("edges", out JsonElement edgeList) && edgeList.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement element in edgeList.EnumerateArray()) {
						HdStudioEvidenceEdge? edge = HdStudioEvidenceEdge.FromJson(element);
						if(edge != null) {
							graph.Edges[edge.Id] = edge;
						}
					}
				}
				if(root.TryGetProperty("gaps", out JsonElement gapList) && gapList.ValueKind == JsonValueKind.Array) {
					foreach(JsonElement element in gapList.EnumerateArray()) {
						HdStudioGap? gap = HdStudioGap.FromJson(element);
						if(gap != null) {
							graph.Gaps[gap.Id] = gap;
						}
					}
				}
			} catch {
				graph.Notes.Add("Existing graph could not be parsed; a fresh graph will be written.");
				graph.Nodes.Clear();
				graph.Edges.Clear();
				graph.Gaps.Clear();
			}
			return graph;
		}

		public void Save(string path)
		{
			string folder = Path.GetDirectoryName(path) ?? ".";
			Directory.CreateDirectory(folder);
			UpdatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
			string temp = path + ".tmp";
			using(FileStream stream = File.Create(temp)) {
				using Utf8JsonWriter json = new(stream, new JsonWriterOptions() { Indented = false });
				json.WriteStartObject();
				json.WriteString("schema", Schema);
				json.WriteString("engineVersion", EngineVersionValue);
				json.WriteString("algorithmVersion", AlgorithmVersionValue);
				json.WriteString("generatedAtUtc", GeneratedAtUtc);
				json.WriteString("updatedAtUtc", UpdatedAtUtc);
				json.WriteString("engineFingerprint", EngineFingerprint);
				json.WriteString("runFingerprint", RunFingerprint);
				json.WriteString("romSha256", RomSha256);
				json.WriteString("romSha1", RomSha1);
				json.WriteString("romFileName", RomFileName);
				json.WriteString("romPath", RomPath);
				json.WriteNumber("mapper", Mapper);
				json.WriteNumber("submapper", Submapper);
				json.WriteString("chrMode", ChrMode);
				json.WriteNumber("prgBytes", PrgBytes);
				json.WriteNumber("chrBytes", ChrBytes);
				json.WriteBoolean("nes20", Nes20);
				json.WriteBoolean("hasTrainer", HasTrainer);
				json.WriteNumber("fileBytes", FileBytes);
				json.WriteStartArray("notes");
				foreach(string note in Notes) {
					json.WriteStringValue(note);
				}
				json.WriteEndArray();
				json.WriteStartArray("nodes");
				foreach(HdStudioEvidenceNode node in Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal)) {
					node.WriteJson(json);
				}
				json.WriteEndArray();
				json.WriteStartArray("edges");
				foreach(HdStudioEvidenceEdge edge in Edges.Values.OrderBy(e => e.Id, StringComparer.Ordinal)) {
					edge.WriteJson(json);
				}
				json.WriteEndArray();
				json.WriteStartArray("gaps");
				foreach(HdStudioGap gap in Gaps.Values.OrderBy(g => g.Priority).ThenBy(g => g.Id, StringComparer.Ordinal)) {
					gap.WriteJson(json);
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			if(File.Exists(path)) {
				string backup = path + ".prev";
				try {
					File.Replace(temp, path, backup, true);
				} catch {
					File.Copy(path, backup, true);
					File.Move(temp, path, true);
				}
			} else {
				File.Move(temp, path);
			}
		}

		/// <summary>
		/// Merges a newly computed graph into an existing one. Content-derived ids keep
		/// observations stable. Higher-value statuses (runtime observed, replay-ready,
		/// replacement-verified) are never downgraded by a later static pass.
		/// </summary>
		public void MergePrevious(HdStudioEvidenceGraph previous)
		{
			if(!string.Equals(previous.RomSha256, RomSha256, StringComparison.OrdinalIgnoreCase)) {
				return;
			}
			foreach(HdStudioEvidenceNode oldNode in previous.Nodes.Values) {
				if(!Nodes.TryGetValue(oldNode.Id, out HdStudioEvidenceNode? newNode)) {
					Nodes[oldNode.Id] = oldNode;
					continue;
				}
				if(StatusRank(oldNode.Status) > StatusRank(newNode.Status)) {
					newNode.Status = oldNode.Status;
					newNode.Confidence = oldNode.Confidence;
					newNode.ConfidenceKind = oldNode.ConfidenceKind;
				}
				foreach(KeyValuePair<string, string> property in oldNode.Properties) {
					if(!newNode.Properties.ContainsKey(property.Key)) {
						newNode.Properties[property.Key] = property.Value;
					}
				}
				foreach(string witness in oldNode.Supporting) {
					if(!newNode.Supporting.Contains(witness)) {
						newNode.Supporting.Add(witness);
					}
				}
				newNode.ObservedOccurrences = Math.Max(newNode.ObservedOccurrences, oldNode.ObservedOccurrences);
			}
			foreach(HdStudioEvidenceEdge oldEdge in previous.Edges.Values) {
				if(!Edges.ContainsKey(oldEdge.Id)) {
					Edges[oldEdge.Id] = oldEdge;
				}
			}
			foreach(HdStudioGap oldGap in previous.Gaps.Values) {
				if(!Gaps.ContainsKey(oldGap.Id)) {
					Gaps[oldGap.Id] = oldGap;
				} else if(oldGap.Status == "resolved" && Gaps[oldGap.Id].Status != "resolved") {
					Gaps[oldGap.Id] = oldGap;
				}
			}
			foreach(string note in previous.Notes) {
				if(!Notes.Contains(note)) {
					Notes.Add(note);
				}
			}
		}

		public static int StatusRank(string status)
		{
			return status switch {
				"replacement-verified" => 8,
				"replay-ready" => 7,
				"runtime-observed" => 6,
				"corroborated" => 5,
				"inferred" => 4,
				"raw" => 3,
				"unresolved" => 2,
				"rejected" => 1,
				_ => 0
			};
		}

		public HdStudioEvidenceNode AddNode(string type, string key, string name, string status = "raw", double confidence = 1.0, string confidenceKind = "certain", string provenance = "")
		{
			string id = type + ":" + StableHash(RomSha256 + "|" + type + "|" + key);
			if(Nodes.TryGetValue(id, out HdStudioEvidenceNode? existing)) {
				existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name;
				if(StatusRank(status) > StatusRank(existing.Status)) {
					existing.Status = status;
					existing.Confidence = confidence;
					existing.ConfidenceKind = confidenceKind;
				}
				if(!string.IsNullOrWhiteSpace(provenance) && !existing.Provenance.Contains(provenance, StringComparison.Ordinal)) {
					existing.Provenance = existing.Provenance.Length == 0 ? provenance : existing.Provenance + "; " + provenance;
				}
				return existing;
			}
			HdStudioEvidenceNode node = new() {
				Id = id,
				Type = type,
				Key = key,
				Name = name,
				Status = status,
				Confidence = Math.Round(confidence, 4),
				ConfidenceKind = confidenceKind,
				Provenance = provenance
			};
			Nodes[id] = node;
			return node;
		}

		public HdStudioEvidenceEdge AddEdge(string fromId, string toId, string relation, string status = "inferred", double confidence = 0.5, string provenance = "")
		{
			string id = "edge:" + StableHash(fromId + "|" + relation + "|" + toId);
			if(Edges.TryGetValue(id, out HdStudioEvidenceEdge? existing)) {
				existing.Observations++;
				return existing;
			}
			HdStudioEvidenceEdge edge = new() {
				Id = id,
				From = fromId,
				To = toId,
				Relation = relation,
				Status = status,
				Confidence = Math.Round(confidence, 4),
				Provenance = provenance
			};
			Edges[id] = edge;
			return edge;
		}

		public HdStudioGap AddGap(string kind, string description, string action, double expectedGain, double cost, IEnumerable<string> relatedNodes)
		{
			string related = string.Join("|", relatedNodes.OrderBy(n => n, StringComparer.Ordinal));
			string id = "gap:" + StableHash(RomSha256 + "|" + kind + "|" + description + "|" + related);
			if(Gaps.TryGetValue(id, out HdStudioGap? existing)) {
				return existing;
			}
			double priority = cost <= 0 ? expectedGain : Math.Round(expectedGain / Math.Max(0.05, cost), 4);
			HdStudioGap gap = new() {
				Id = id,
				Kind = kind,
				Description = description,
				Action = action,
				ExpectedGain = Math.Round(expectedGain, 4),
				Cost = Math.Round(cost, 4),
				Priority = priority,
				Status = "open"
			};
			foreach(string relatedNode in relatedNodes.Distinct(StringComparer.Ordinal)) {
				gap.RelatedNodes.Add(relatedNode);
			}
			Gaps[id] = gap;
			return gap;
		}

		public static string StableHash(string text)
		{
			byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
			return Convert.ToHexString(hash).Substring(0, 20).ToLowerInvariant();
		}

		public static string ComputeFingerprint(string options)
		{
			return StableHash(EngineVersion + "|" + AlgorithmVersion + "|" + options);
		}

		private static readonly Lazy<string> _engineFingerprint = new(() => {
			StringBuilder builder = new(EngineVersion + "|" + AlgorithmVersion + "|static-first-1");
			try {
				string folder = AppContext.BaseDirectory;
				foreach(string name in new[] { "Mesen.dll", "Mesen.exe", "MesenCore.dll" }) {
					string path = Path.Combine(folder, name);
					if(File.Exists(path)) {
						builder.Append('|').Append(name).Append('=').Append(ShortFileHash(path));
					}
				}
			} catch {
			}
			return StableHash(builder.ToString());
		});

		/// <summary>
		/// Engine binary/algorithm identity shared by every ROM in a batch. The
		/// per-run settings and evidence source are recorded separately as
		/// RunFingerprint so the engine fingerprint stays comparable across ROMs.
		/// </summary>
		public static string ComputeEngineFingerprint() => _engineFingerprint.Value;

		private static string ShortFileHash(string path)
		{
			using FileStream stream = File.OpenRead(path);
			byte[] hash = SHA256.HashData(stream);
			return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
		}

		public int CountNodes(string type) => Nodes.Values.Count(n => string.Equals(n.Type, type, StringComparison.Ordinal));
		public int CountStatus(string status) => Nodes.Values.Count(n => string.Equals(n.Status, status, StringComparison.Ordinal));

		internal static string GetString(JsonElement element, string name, string fallback = "")
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
		}

		internal static int GetInt(JsonElement element, string name, int fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
		}

		internal static long GetLong(JsonElement element, string name, long fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : fallback;
		}

		internal static double GetDouble(JsonElement element, string name, double fallback = 0)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : fallback;
		}

		internal static bool GetBool(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}
	}

	internal sealed class HdStudioEvidenceNode
	{
		public string Id = "";
		public string Type = "";
		public string Key = "";
		public string Name = "";
		public string Status = "raw";
		public double Confidence = 1.0;
		public string ConfidenceKind = "certain";
		public string Provenance = "";
		public int ObservedOccurrences;
		public readonly Dictionary<string, string> Properties = new(StringComparer.Ordinal);
		public readonly List<string> Supporting = new();
		public readonly List<string> Contradicting = new();

		public void WriteJson(Utf8JsonWriter json)
		{
			json.WriteStartObject();
			json.WriteString("id", Id);
			json.WriteString("type", Type);
			json.WriteString("key", Key);
			json.WriteString("name", Name);
			json.WriteString("status", Status);
			json.WriteNumber("confidence", Confidence);
			json.WriteString("confidenceKind", ConfidenceKind);
			json.WriteString("provenance", Provenance);
			json.WriteNumber("observedOccurrences", ObservedOccurrences);
			json.WriteStartObject("properties");
			foreach(KeyValuePair<string, string> property in Properties.OrderBy(p => p.Key, StringComparer.Ordinal)) {
				json.WriteString(property.Key, property.Value);
			}
			json.WriteEndObject();
			json.WriteStartArray("supporting");
			foreach(string witness in Supporting) {
				json.WriteStringValue(witness);
			}
			json.WriteEndArray();
			json.WriteStartArray("contradicting");
			foreach(string witness in Contradicting) {
				json.WriteStringValue(witness);
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		public static HdStudioEvidenceNode? FromJson(JsonElement element)
		{
			string id = HdStudioEvidenceGraph.GetString(element, "id");
			string type = HdStudioEvidenceGraph.GetString(element, "type");
			if(string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type)) {
				return null;
			}
			HdStudioEvidenceNode node = new() {
				Id = id,
				Type = type,
				Key = HdStudioEvidenceGraph.GetString(element, "key"),
				Name = HdStudioEvidenceGraph.GetString(element, "name"),
				Status = HdStudioEvidenceGraph.GetString(element, "status", "raw"),
				Confidence = HdStudioEvidenceGraph.GetDouble(element, "confidence", 1.0),
				ConfidenceKind = HdStudioEvidenceGraph.GetString(element, "confidenceKind", "certain"),
				Provenance = HdStudioEvidenceGraph.GetString(element, "provenance"),
				ObservedOccurrences = HdStudioEvidenceGraph.GetInt(element, "observedOccurrences")
			};
			if(element.TryGetProperty("properties", out JsonElement properties) && properties.ValueKind == JsonValueKind.Object) {
				foreach(JsonProperty property in properties.EnumerateObject()) {
					if(property.Value.ValueKind == JsonValueKind.String) {
						node.Properties[property.Name] = property.Value.GetString() ?? "";
					} else if(property.Value.ValueKind == JsonValueKind.Number) {
						node.Properties[property.Name] = property.Value.GetRawText();
					} else if(property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False) {
						node.Properties[property.Name] = property.Value.GetBoolean() ? "true" : "false";
					}
				}
			}
			if(element.TryGetProperty("supporting", out JsonElement supporting) && supporting.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in supporting.EnumerateArray()) {
					if(item.ValueKind == JsonValueKind.String) {
						node.Supporting.Add(item.GetString() ?? "");
					}
				}
			}
			if(element.TryGetProperty("contradicting", out JsonElement contradicting) && contradicting.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in contradicting.EnumerateArray()) {
					if(item.ValueKind == JsonValueKind.String) {
						node.Contradicting.Add(item.GetString() ?? "");
					}
				}
			}
			return node;
		}
	}

	internal sealed class HdStudioEvidenceEdge
	{
		public string Id = "";
		public string From = "";
		public string To = "";
		public string Relation = "";
		public string Status = "inferred";
		public double Confidence = 0.5;
		public string Provenance = "";
		public int Observations = 1;

		public void WriteJson(Utf8JsonWriter json)
		{
			json.WriteStartObject();
			json.WriteString("id", Id);
			json.WriteString("from", From);
			json.WriteString("to", To);
			json.WriteString("relation", Relation);
			json.WriteString("status", Status);
			json.WriteNumber("confidence", Confidence);
			json.WriteString("provenance", Provenance);
			json.WriteNumber("observations", Observations);
			json.WriteEndObject();
		}

		public static HdStudioEvidenceEdge? FromJson(JsonElement element)
		{
			string id = HdStudioEvidenceGraph.GetString(element, "id");
			string from = HdStudioEvidenceGraph.GetString(element, "from");
			string to = HdStudioEvidenceGraph.GetString(element, "to");
			string relation = HdStudioEvidenceGraph.GetString(element, "relation");
			if(string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(relation)) {
				return null;
			}
			return new HdStudioEvidenceEdge() {
				Id = id,
				From = from,
				To = to,
				Relation = relation,
				Status = HdStudioEvidenceGraph.GetString(element, "status", "inferred"),
				Confidence = HdStudioEvidenceGraph.GetDouble(element, "confidence", 0.5),
				Provenance = HdStudioEvidenceGraph.GetString(element, "provenance"),
				Observations = HdStudioEvidenceGraph.GetInt(element, "observations", 1)
			};
		}
	}

	internal sealed class HdStudioGap
	{
		public string Id = "";
		public string Kind = "";
		public string Description = "";
		public string Action = "";
		public double ExpectedGain;
		public double Cost;
		public double Priority;
		public string Status = "open";
		public string Resolution = "";
		public readonly List<string> RelatedNodes = new();

		public void WriteJson(Utf8JsonWriter json)
		{
			json.WriteStartObject();
			json.WriteString("id", Id);
			json.WriteString("kind", Kind);
			json.WriteString("description", Description);
			json.WriteString("action", Action);
			json.WriteNumber("expectedGain", ExpectedGain);
			json.WriteNumber("cost", Cost);
			json.WriteNumber("priority", Priority);
			json.WriteString("status", Status);
			json.WriteString("resolution", Resolution);
			json.WriteStartArray("relatedNodes");
			foreach(string node in RelatedNodes) {
				json.WriteStringValue(node);
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}

		public static HdStudioGap? FromJson(JsonElement element)
		{
			string id = HdStudioEvidenceGraph.GetString(element, "id");
			if(string.IsNullOrWhiteSpace(id)) {
				return null;
			}
			HdStudioGap gap = new() {
				Id = id,
				Kind = HdStudioEvidenceGraph.GetString(element, "kind"),
				Description = HdStudioEvidenceGraph.GetString(element, "description"),
				Action = HdStudioEvidenceGraph.GetString(element, "action"),
				ExpectedGain = HdStudioEvidenceGraph.GetDouble(element, "expectedGain"),
				Cost = HdStudioEvidenceGraph.GetDouble(element, "cost"),
				Priority = HdStudioEvidenceGraph.GetDouble(element, "priority"),
				Status = HdStudioEvidenceGraph.GetString(element, "status", "open"),
				Resolution = HdStudioEvidenceGraph.GetString(element, "resolution")
			};
			if(element.TryGetProperty("relatedNodes", out JsonElement related) && related.ValueKind == JsonValueKind.Array) {
				foreach(JsonElement item in related.EnumerateArray()) {
					if(item.ValueKind == JsonValueKind.String) {
						gap.RelatedNodes.Add(item.GetString() ?? "");
					}
				}
			}
			return gap;
		}
	}
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Internal context resolver for shared graphics.
	///
	/// Standard Mesen HD Packs can distinguish two uses of the same CHR pattern with
	/// global conditions (hmirror/vmirror/bgpriority/sprite palette) or custom condition
	/// tags. The Studio only proposes rules it can compile into standard syntax. Each
	/// proposal is measured against the captured occurrences, so a rule is never applied
	/// speculatively and unrelated objects are never silently replaced.
	/// </summary>
	internal static class HdStudioContextResolver
	{
		public sealed class Proposal
		{
			public string Rule = "";
			public string Title = "";
			public string Detail = "";
			public int SelectedOccurrences;
			public int AffectedOtherOccurrences;
			public double Confidence;
			public bool Supported = true;
			public bool IsShared;
			public bool IsPreserve;
		}

		internal static List<Proposal> Analyze(HdStudioCatalog catalog, HdStudioCatalogAsset asset)
		{
			List<Proposal> result = new();
			if(asset.Tiles.Count == 0) {
				result.Add(new Proposal() {
					Rule = "manual",
					Title = "No hardware tiles recorded",
					Detail = "This asset has no pattern references yet. Capture or pick the object again.",
					Supported = false
				});
				return result;
			}

			List<HdStudioCatalogAsset> shared = HdStudioCatalog.FindAssetsSharingKeys(catalog, asset.Id,
				asset.Tiles.Select(t => (HdStudioCatalog.GetTileKey(t), t.Palette)));
			HashSet<string> sharedKeys = new(asset.Tiles.Select(t => Key(t)).Where(k => k.Length > 0), StringComparer.OrdinalIgnoreCase);
			List<HdStudioCatalogTile> otherTiles = new();
			foreach(HdStudioCatalogAsset other in shared) {
				foreach(HdStudioCatalogTile tile in other.Tiles) {
					if(sharedKeys.Contains(Key(tile))) {
						otherTiles.Add(tile);
					}
				}
			}

			//Enumerate the small set of standard global conditions that can distinguish
			//orientation and background priority. Combined conditions use Mesen's '&'.
			string[] tokens = { "hmirror", "!hmirror", "vmirror", "!vmirror", "bgpriority", "!bgpriority" };
			List<(string Rule, int Count)> candidates = new();
			foreach(string first in tokens) {
				candidates.Add((first, 1));
			}
			string[][] pairs = {
				new[] { "hmirror", "vmirror" }, new[] { "hmirror", "!vmirror" },
				new[] { "!hmirror", "vmirror" }, new[] { "!hmirror", "!vmirror" },
				new[] { "hmirror", "bgpriority" }, new[] { "hmirror", "!bgpriority" },
				new[] { "!hmirror", "bgpriority" }, new[] { "!hmirror", "!bgpriority" },
				new[] { "vmirror", "bgpriority" }, new[] { "vmirror", "!bgpriority" },
				new[] { "!vmirror", "bgpriority" }, new[] { "!vmirror", "!bgpriority" }
			};
			foreach(string[] pair in pairs) {
				candidates.Add((pair[0] + "&" + pair[1], 2));
			}

			HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
			foreach((string rule, int count) in candidates.OrderBy(c => c.Count)) {
				if(!seen.Add(rule)) {
					continue;
				}
				string[] parts = rule.Split('&');
				bool selectedMatch = asset.Tiles.All(t => Matches(t, parts));
				if(!selectedMatch) {
					continue;
				}
				int affected = otherTiles.Count(t => Matches(t, parts));
				int unaffected = otherTiles.Count - affected;
				if(affected == 0 && otherTiles.Count > 0) {
					result.Add(new Proposal() {
						Rule = "[" + rule + "]",
						Title = "Context rule: " + rule,
						Detail = $"The selected object matches {rule}; all {otherTiles.Count} shared occurrence(s) keep their original graphics. Verified against the captured OAM/nametable data.",
						SelectedOccurrences = asset.Tiles.Count,
						AffectedOtherOccurrences = 0,
						Confidence = 0.92 - 0.04 * (count - 1)
					});
				} else if(unaffected > affected && affected > 0) {
					result.Add(new Proposal() {
						Rule = "[" + rule + "]",
						Title = "Context rule (partial): " + rule,
						Detail = $"The selected object matches {rule}, but {affected} shared occurrence(s) also match and would still change. {unaffected} would keep their original graphics.",
						SelectedOccurrences = asset.Tiles.Count,
						AffectedOtherOccurrences = affected,
						Confidence = 0.45,
						Supported = true
					});
				}
			}

			result.Add(new Proposal() {
				Rule = "",
				Title = "Shared replacement",
				Detail = $"Every occurrence of the same pattern+palette changes ({shared.Count} other asset(s), {otherTiles.Count} captured tile occurrence(s)). Intentional, but no context rule is applied.",
				SelectedOccurrences = asset.Tiles.Count,
				AffectedOtherOccurrences = otherTiles.Count,
				Confidence = 1.0,
				IsShared = true
			});
			result.Add(new Proposal() {
				Rule = "preserve",
				Title = "Keep the original graphics",
				Detail = "No replacement is written for this object. The captured asset and its hardware references stay available.",
				SelectedOccurrences = asset.Tiles.Count,
				AffectedOtherOccurrences = 0,
				Confidence = 1.0,
				IsPreserve = true
			});
			return result.OrderByDescending(p => p.Confidence).ToList();
		}

		private static bool Matches(HdStudioCatalogTile tile, string[] conditions)
		{
			foreach(string condition in conditions) {
				bool value = condition.StartsWith("!", StringComparison.Ordinal) ? false : true;
				string name = value ? condition : condition.Substring(1);
				bool actual = name switch {
					"hmirror" => tile.FlipH,
					"vmirror" => tile.FlipV,
					"bgpriority" => tile.BackgroundPriority,
					_ => false
				};
				if(actual != value) {
					return false;
				}
			}
			return true;
		}

		private static string Key(HdStudioCatalogTile tile)
		{
			return HdStudioCatalog.GetTileKey(tile).ToUpperInvariant();
		}
	}
}

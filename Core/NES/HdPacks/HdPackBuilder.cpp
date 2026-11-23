#include "pch.h"
#include <algorithm>
#include "NES/HdPacks/HdPackBuilder.h"
#include "NES/HdPacks/HdNesPack.h"
#include "NES/BaseMapper.h"
#include "NES/BaseNesPpu.h"
#include "NES/NesConstants.h"
#include "NES/HdPacks/HdPackLoader.h"
#include "NES/NesDefaultVideoFilter.h"
#include "Utilities/xBRZ/xbrz.h"
#include "Utilities/HQX/hqx.h"
#include "Utilities/Scale2x/scalebit.h"
#include "Utilities/KreedSaiEagle/SaiEagle.h"
#include "Utilities/FolderUtilities.h"
#include "Utilities/PNGHelper.h"
#include "Utilities/HexUtilities.h"
#include "Utilities/VirtualFile.h"
#include "Shared/Emulator.h"
#include "Shared/EmuSettings.h"

HdPackBuilder::HdPackBuilder(Emulator* emu, PpuModel ppuModel, bool isChrRam, HdPackBuilderOptions options)
{
	_emu = emu;
	_isChrRam = isChrRam;

	_saveFolder = options.SaveFolder;
	options.SaveFolder = nullptr;

	_options = options;

	NesDefaultVideoFilter::GetFullPalette(_palette, _emu->GetSettings()->GetNesConfig(), ppuModel);

	string existingPackDefinition = FolderUtilities::CombinePath(_saveFolder, "hires.txt");
	if(ifstream(existingPackDefinition)) {
		HdPackLoader::LoadHdNesPack(existingPackDefinition, _hdData);
		_hdData.LoadAsync();
		for(auto& tile : _hdData.Tiles) {
			tile->Init();
		}

		for(unique_ptr<HdPackTileInfo>& tile : _hdData.Tiles) {
			//Mark the tiles in the first PNGs as higher usage (preserves order when adding new tiles to an existing set)
			AddTile(tile.get(), 0xFFFFFFFF - tile->BitmapIndex);
		}

		if(_hdData.Scale != _options.Scale) {
			_options.FilterType = ScaleFilterType::Prescale;
		}
	} else {
		_hdData.Scale = _options.Scale;
	}

	_romName = FolderUtilities::GetFilename(_emu->GetRomInfo().RomFile.GetFileName(), false);

	LoadHdStudioData();
}

HdPackBuilder::~HdPackBuilder()
{
	SaveHdPack();
}

void HdPackBuilder::AddTile(HdPackTileInfo* tile, uint32_t usageCount)
{
	bool isTileBlank = _options.GroupBlankTiles ? tile->Blank : false;

	int chrBankId = isTileBlank ? 0xFFFFFFFF : tile->ChrBankId;
	int palette = isTileBlank ? _blankTilePalette : tile->PaletteColors;

	if(_tilesByChrBankByPalette.find(chrBankId) == _tilesByChrBankByPalette.end()) {
		_tilesByChrBankByPalette[chrBankId] = std::map<uint32_t, vector<HdPackTileInfo*>>();
	}

	std::map<uint32_t, vector<HdPackTileInfo*>>& paletteMap = _tilesByChrBankByPalette[chrBankId];
	if(paletteMap.find(palette) == paletteMap.end()) {
		paletteMap[palette] = vector<HdPackTileInfo*>(256, nullptr);
	}

	if(isTileBlank) {
		paletteMap[palette][_blankTileIndex] = tile;
		_blankTileIndex++;
		if(_blankTileIndex == _options.ChrRamBankSize / 16) {
			_blankTileIndex = 0;
			_blankTilePalette++;
		}
	} else {
		if(tile->TileIndex >= 0) {
			paletteMap[palette][tile->TileIndex % 256] = tile;
		} else {
			//FIXME: This will result in data loss if more than 256 tiles of the same palette exist in the hires.txt file
			//Currently this way to prevent issues when loading a CHR RAM HD pack into the recorder (because TileIndex is -1 in that case)
			for(int i = 0; i < 256; i++) {
				if(paletteMap[palette][i] == nullptr) {
					paletteMap[palette][i] = tile;
					break;
				}
			}
		}
	}

	_tilesByKey[tile->GetKey(false)] = tile;
	_tileUsageCount[tile->GetKey(false)] = usageCount;
}

HdStudioTileHandle HdPackBuilder::ProcessTile(uint32_t x, uint32_t y, uint16_t tileAddr, HdPpuTileInfo& tile, BaseMapper* mapper, bool isSprite, uint32_t chrBankHash, bool transparencyRequired)
{
	HdStudioTileHandle handle;
	if(_options.IgnoreOverscan) {
		OverscanDimensions overscan = _emu->GetSettings()->GetOverscan();
		if(x < overscan.Left || y < overscan.Top || (NesConstants::ScreenWidth - x - 1) < overscan.Right || (NesConstants::ScreenHeight - y - 1) < overscan.Bottom) {
			//Ignore tiles inside overscan
			return handle;
		}
	}

	uint32_t& studioUsage = _studioTileUsage[tile.GetKey(false)];
	studioUsage++;

	auto result = _tileUsageCount.find(tile.GetKey(false));
	if(result == _tileUsageCount.end()) {
		//Check to see if a default tile matches
		result = _tileUsageCount.find(tile.GetKey(true));
	}

	if(result == _tileUsageCount.end()) {
		//First time seeing this tile/palette combination, store it
		HdPackTileInfo* hdTile = new HdPackTileInfo();
		hdTile->PaletteColors = tile.PaletteColors;
		hdTile->TileIndex = tile.TileIndex;
		hdTile->DefaultTile = false;
		hdTile->IsChrRamTile = _isChrRam;
		hdTile->Brightness = 255;
		hdTile->ChrBankId = _isChrRam ? chrBankHash : (tileAddr / 16 / 256);
		hdTile->TransparencyRequired = transparencyRequired;

		memcpy(hdTile->TileData, tile.TileData, 16);

		_hdData.Tiles.push_back(unique_ptr<HdPackTileInfo>(hdTile));
		AddTile(hdTile, 1);
		result = _tileUsageCount.find(tile.GetKey(false));
	} else {
		if(transparencyRequired) {
			auto existingTile = _tilesByKey.find(tile.GetKey(false));
			if(existingTile != _tilesByKey.end()) {
				existingTile->second->TransparencyRequired = true;
			}
		}

		if(result->second < 0x7FFFFFFF) {
			//Increase usage count
			result->second++;
		}
	}

	//Pointers to unordered_map values stay valid when the map grows
	if(result != _tileUsageCount.end()) {
		handle.Usage = &result->second;
		handle.StudioUsage = &studioUsage;
	}
	return handle;
}

string HdPackBuilder::GetStudioTileKey(int32_t tileIndex, uint8_t* tileData)
{
	if(_isChrRam) {
		//CHR RAM tiles are identified by their content, like in hires.txt
		string key;
		key.reserve(32);
		for(int i = 0; i < 16; i++) {
			key += HexUtilities::ToHex(tileData[i]);
		}
		return key;
	} else {
		return HexUtilities::ToHex(tileIndex);
	}
}

void HdPackBuilder::SetChrRom(BaseMapper* mapper)
{
	uint32_t size = mapper->GetChrRomSize();
	_chrRom.resize(size);
	for(uint32_t addr = 0; addr + 16 <= size; addr += 16) {
		mapper->CopyChrTile(addr, _chrRom.data() + addr);
	}
}

void HdPackBuilder::SetPrgRom(BaseMapper* mapper)
{
	uint32_t size = mapper->GetPrgRomSize();
	if(size == 0) {
		return;
	}
	_prgRom.resize(size);
	mapper->CopyPrgRom(_prgRom.data());
	ScanPrgRomForPalettes();
}

namespace {
	bool IsPlausiblePaletteGroup(const uint8_t* group)
	{
		//NES color indexes go from $00 to $3F, and $0D ("blacker than black") is never stored in a table
		for(int i = 0; i < 4; i++) {
			if(group[i] > 0x3F || group[i] == 0x0D) {
				return false;
			}
		}
		//A row with 4 identical colors is filler (e.g. an empty/unused palette), not a real palette
		if(group[0] == group[1] && group[1] == group[2] && group[2] == group[3]) {
			return false;
		}
		//[a,b,a,b] is the pattern of 16-bit little-endian data, not of a palette
		return !(group[0] == group[2] && group[1] == group[3]);
	}

	int PaletteScore(uint32_t colors)
	{
		//Real NES palettes usually keep the same hue (low nibble) and increase the brightness for the
		//last 3 colors, e.g. $0F,$01,$11,$21 (black + one blue ramp). Palettes without that structure
		//are more likely to come from other data in the ROM.
		uint8_t c1 = (colors >> 16) & 0x3F;
		uint8_t c2 = (colors >> 8) & 0x3F;
		uint8_t c3 = colors & 0x3F;
		int score = 0;
		if((c1 & 0x0F) == (c2 & 0x0F) && (c2 & 0x0F) == (c3 & 0x0F)) {
			score += 2;
		}
		if(c1 < c2 && c2 < c3) {
			score++;
		}
		if(c1 == c2 || c2 == c3) {
			score--;
		}
		return score;
	}
}

void HdPackBuilder::ScanPrgRomForPalettes()
{
	if(_prgRom.size() < 16) {
		return;
	}

	//Palettes found during this scan: colors -> { uses, first offset }
	std::map<uint32_t, std::pair<uint32_t, uint32_t>> found;
	const uint8_t* prg = _prgRom.data();
	uint32_t size = (uint32_t)_prgRom.size();

	//NES palette tables are stored in the code as groups of 4 bytes (one 4-color palette per group),
	//usually 16 bytes long (4 background or 4 sprite palettes) or 32 bytes (both), with the same
	//backdrop color repeated at the start of each group (very often $0F, i.e. black).
	for(uint32_t pos = 0; pos + 16 <= size; ) {
		uint32_t groups = 0;
		while(groups < 8 && pos + (groups + 1) * 4 <= size) {
			const uint8_t* group = prg + pos + groups * 4;
			if(!IsPlausiblePaletteGroup(group) || (groups > 0 && group[0] != prg[pos])) {
				break;
			}
			groups++;
		}

		if(groups >= 4) {
			//Only whole tables: 16 or 32 bytes
			groups -= groups % 4;
			for(uint32_t g = 0; g < groups; g++) {
				const uint8_t* c = prg + pos + g * 4;
				uint32_t colors = ((uint32_t)c[0] << 24) | ((uint32_t)c[1] << 16) | ((uint32_t)c[2] << 8) | c[3];
				auto result = found.find(colors);
				if(result == found.end()) {
					found[colors] = { 1, pos + g * 4 };
				} else {
					result->second.first++;
				}
			}
			pos += groups * 4;
		} else {
			pos++;
		}
	}

	//The same ROM always gives the same list, so it is rebuilt from scratch on every session
	_studioPalettes.clear();
	_studioPalettes.reserve(found.size());
	for(auto& kvp : found) {
		HdStudioPrgPalette palette;
		palette.Colors = kvp.first;
		palette.Uses = kvp.second.first;
		palette.FirstOffset = kvp.second.second;
		_studioPalettes.push_back(palette);
	}

	//The most repeated palettes go first: they are the most likely to belong to the game's main areas.
	//Palettes with a clean color ramp go before palettes with repeated colors (more likely to be data).
	std::sort(_studioPalettes.begin(), _studioPalettes.end(), [](const HdStudioPrgPalette& a, const HdStudioPrgPalette& b) {
		int scoreA = PaletteScore(a.Colors);
		int scoreB = PaletteScore(b.Colors);
		if(scoreA != scoreB) {
			return scoreA > scoreB;
		}
		if(a.Uses != b.Uses) {
			return a.Uses > b.Uses;
		}
		return a.Colors < b.Colors;
	});
}

uint32_t HdPackBuilder::GetPrgPalette(bool spriteForm)
{
	if(_studioPalettes.empty()) {
		return 0;
	}

	//The list is sorted by how plausible the palette looks, then by usage
	uint32_t colors = _studioPalettes[0].Colors;
	if(spriteForm) {
		//Entry 0 of a sprite palette is transparent: it uses the same "alpha" bit as palette RAM
		return 0xFF000000 | (colors & 0x00FFFFFF);
	}
	return colors;
}

void HdPackBuilder::AddUnseenChrTiles()
{
	//Tiles that are already part of the pack (with any palette)
	std::map<int32_t, uint32_t> knownTiles; //tile index -> palette of its most used variant
	std::map<int32_t, uint32_t> knownUsage;
	std::map<uint32_t, uint64_t> paletteUsage;
	//Palettes seen in each 4 KB CHR bank, split by form (sprite / background)
	std::map<int32_t, std::pair<uint32_t, uint32_t>> bankForms; //bank -> { sprites, background }
	for(unique_ptr<HdPackTileInfo>& tile : _hdData.Tiles) {
		if(tile->IsChrRamTile || tile->TileIndex < 0) {
			continue;
		}
		auto usage = _studioTileUsage.find(tile->GetKey(false));
		uint32_t count = usage != _studioTileUsage.end() ? usage->second : 0;
		if(knownTiles.find(tile->TileIndex) == knownTiles.end() || count > knownUsage[tile->TileIndex]) {
			knownTiles[tile->TileIndex] = tile->PaletteColors;
			knownUsage[tile->TileIndex] = count;
		}
		paletteUsage[tile->PaletteColors] += count;

		std::pair<uint32_t, uint32_t>& forms = bankForms[tile->TileIndex / 256];
		if(tile->IsSpriteTile()) {
			forms.first++;
		} else {
			forms.second++;
		}
	}

	//Fallback palette when no tile of the same bank was seen: the most used background palette, or a gray scale
	uint32_t defaultPalette = 0x0F001030;
	uint64_t bestUsage = 0;
	for(auto& kvp : paletteUsage) {
		if((kvp.first >> 24) != 0xFF && kvp.second > bestUsage) {
			bestUsage = kvp.second;
			defaultPalette = kvp.first;
		}
	}

	int32_t tileCount = (int32_t)(_chrRom.size() / 16);
	uint8_t blankTile[16] = {};
	for(int32_t i = 0; i < tileCount; i++) {
		if(knownTiles.find(i) != knownTiles.end() || memcmp(&_chrRom[i * 16], blankTile, 16) == 0) {
			continue;
		}

		//Tiles of the same character/object are usually stored next to each other in CHR ROM:
		//use the palette of the closest tile (in the same 4 KB bank) that was seen during the recording
		uint32_t palette = defaultPalette;
		bool neighbourFound = false;
		int32_t bankStart = i - (i % 256);
		for(int32_t d = 1; d < 256; d++) {
			if(i - d >= bankStart) {
				auto result = knownTiles.find(i - d);
				if(result != knownTiles.end()) {
					palette = result->second;
					neighbourFound = true;
					break;
				}
			}
			if(i + d < bankStart + 256 && i + d < tileCount) {
				auto result = knownTiles.find(i + d);
				if(result != knownTiles.end()) {
					palette = result->second;
					neighbourFound = true;
					break;
				}
			}
		}

		if(neighbourFound) {
			_studioUnseenTilesWithNeighbourPalette++;
		} else {
			//Nothing of this bank was ever displayed: use a palette found in the game's code instead of
			//a gray scale. Sprites and backgrounds are not distinguished in the ROM, so guess the form
			//from the tiles of the same bank that were seen, or from the usual CHR layout: sprites in the
			//first 4 KB ($0000) and backgrounds in the second ($1000).
			int32_t bank = i / 256;
			auto forms = bankForms.find(bank);
			bool spriteForm = forms != bankForms.end() ? forms->second.first > forms->second.second : (bank % 2) == 0;
			uint32_t prgPalette = GetPrgPalette(spriteForm);
			if(prgPalette != 0) {
				palette = prgPalette;
				_studioUnseenTilesWithPrgPalette++;
			} else {
				//No palette could be guessed: the tile keeps the gray scale default
				_studioUnseenTilesWithDefaultPalette++;
			}
		}

		HdPackTileInfo* hdTile = new HdPackTileInfo();
		hdTile->PaletteColors = palette;
		hdTile->TileIndex = i;
		hdTile->DefaultTile = false;
		hdTile->IsChrRamTile = false;
		hdTile->Brightness = 255;
		hdTile->ChrBankId = i / 256;
		hdTile->TransparencyRequired = false;
		memcpy(hdTile->TileData, &_chrRom[i * 16], 16);

		_hdData.Tiles.push_back(unique_ptr<HdPackTileInfo>(hdTile));
		AddTile(hdTile, 0);
		_studioAddedChrTiles++;
	}

	//Tiles with identical graphics stored at another CHR address reuse the same HD image
	_hdData.OptionFlags |= (int)HdPackOptions::AutomaticFallbackTiles;
}

void HdPackBuilder::AddKnownLevelTiles()
{
	//--maps runs the game's own level loader and writes the exact (pattern, palette) pairs that
	//the levels place. Adding them here means every level background tile has the palette the
	//game really uses, even if the recording never showed that level (no guessed colors).
	string path = FolderUtilities::CombinePath(_saveFolder, "HdStudio/maps/tiles_exact.txt");
	ifstream file(path);
	if(!file) {
		return;
	}

	int32_t tileCount = (int32_t)(_chrRom.size() / 16);
	string line;
	while(std::getline(file, line)) {
		if(line.empty() || line[0] == '#') {
			continue;
		}

		size_t separator = line.find_first_of(" \t");
		if(separator == string::npos) {
			continue;
		}

		string tileToken = line.substr(0, separator);
		string paletteToken = line.substr(separator + 1);
		while(!paletteToken.empty() && (paletteToken.back() == '\r' || paletteToken.back() == ' ' || paletteToken.back() == '\t')) {
			paletteToken.pop_back();
		}

		try {
			int32_t tileIndex = (int32_t)std::stoul(tileToken, nullptr, 16);
			uint32_t palette = (uint32_t)std::stoul(paletteToken, nullptr, 16);
			if(tileIndex < 0 || tileIndex >= tileCount) {
				continue;
			}

			HdTileKey key = {};
			key.TileIndex = tileIndex;
			key.PaletteColors = palette;
			key.IsChrRamTile = false;
			if(_tilesByKey.find(key) != _tilesByKey.end()) {
				continue;
			}

			HdPackTileInfo* hdTile = new HdPackTileInfo();
			hdTile->PaletteColors = palette;
			hdTile->TileIndex = tileIndex;
			hdTile->DefaultTile = false;
			hdTile->IsChrRamTile = false;
			hdTile->Brightness = 255;
			hdTile->ChrBankId = tileIndex / 256;
			hdTile->TransparencyRequired = false;
			memcpy(hdTile->TileData, &_chrRom[tileIndex * 16], 16);

			_hdData.Tiles.push_back(unique_ptr<HdPackTileInfo>(hdTile));
			AddTile(hdTile, 0);
			_studioAddedLevelTiles++;
		} catch(...) {
			//Ignore malformed lines; the file is generated by our own map pass
		}
	}
}

void HdPackBuilder::ProcessSpriteFrame(vector<HdStudioSprite>& sprites)
{
	_studioFrameCount++;

	size_t count = sprites.size();
	if(count == 0) {
		return;
	}

	//Group sprites that overlap or touch each other (union-find)
	vector<size_t> parent(count);
	for(size_t i = 0; i < count; i++) {
		parent[i] = i;
	}

	auto findRoot = [&parent](size_t i) {
		while(parent[i] != i) {
			parent[i] = parent[parent[i]];
			i = parent[i];
		}
		return i;
	};

	for(size_t i = 0; i < count; i++) {
		for(size_t j = i + 1; j < count; j++) {
			HdStudioSprite& a = sprites[i];
			HdStudioSprite& b = sprites[j];
			//Sprites that touch belong to the same object when they use the same palette (an enemy or a shot
			//touching the player usually has another palette); sprites that overlap are always grouped
			//(characters drawn with 2 palettes layer one sprite over another)
			bool touching = a.X <= b.X + 8 && b.X <= a.X + 8 && a.Y <= b.Y + 8 && b.Y <= a.Y + 8;
			bool overlapping = a.X < b.X + 8 && b.X < a.X + 8 && a.Y < b.Y + 8 && b.Y < a.Y + 8;
			if(overlapping || (touching && a.PaletteColors == b.PaletteColors)) {
				parent[findRoot(i)] = findRoot(j);
			}
		}
	}

	std::map<size_t, vector<HdStudioSprite*>> groups;
	for(size_t i = 0; i < count; i++) {
		groups[findRoot(i)].push_back(&sprites[i]);
	}

	for(auto& group : groups) {
		vector<HdStudioSprite*>& members = group.second;

		int16_t minX = INT16_MAX, minY = INT16_MAX, maxX = INT16_MIN, maxY = INT16_MIN;
		for(HdStudioSprite* s : members) {
			minX = std::min(minX, s->X);
			minY = std::min(minY, s->Y);
			maxX = std::max(maxX, (int16_t)(s->X + 8));
			maxY = std::max(maxY, (int16_t)(s->Y + 8));
		}

		std::sort(members.begin(), members.end(), [](HdStudioSprite* a, HdStudioSprite* b) {
			if(a->Y != b->Y) {
				return a->Y < b->Y;
			}
			if(a->X != b->X) {
				return a->X < b->X;
			}
			return a->TileIndex < b->TileIndex;
		});

		//Signature: "dx,dy,tile,palette,flags;" for each sprite, relative to the group's top-left corner
		stringstream sig;
		for(HdStudioSprite* s : members) {
			sig << (s->X - minX) << "," << (s->Y - minY) << "," << GetStudioTileKey(s->TileIndex, s->TileData) << "," << HexUtilities::ToHex(s->PaletteColors, true) << "," << ((s->HorizontalMirror ? 1 : 0) | (s->VerticalMirror ? 2 : 0)) << ";";
		}

		string key = sig.str();
		auto result = _studioObjects.find(key);
		if(result == _studioObjects.end()) {
			if(_studioObjects.size() >= MaxStudioObjects) {
				continue;
			}
			HdStudioObjectStats stats;
			stats.FrameCount = 1;
			stats.FirstFrame = _studioFrameCount;
			stats.Width = (uint16_t)(maxX - minX);
			stats.Height = (uint16_t)(maxY - minY);
			_studioObjects[key] = stats;
		} else {
			result->second.FrameCount++;
		}
	}
}

void HdPackBuilder::LoadHdStudioData()
{
	//Merge the results of previous capture sessions into this one
	string studioFolder = FolderUtilities::CombinePath(_saveFolder, "HdStudio");

	ifstream captureFile(FolderUtilities::CombinePath(studioFolder, "capture.txt"));
	string line;
	while(std::getline(captureFile, line)) {
		if(line.rfind("frames=", 0) == 0) {
			_studioFrameCount = (uint32_t)std::stoul(line.substr(7));
		}
	}

	ifstream objectsFile(FolderUtilities::CombinePath(studioFolder, "objects.txt"));
	while(std::getline(objectsFile, line)) {
		if(line.empty() || line[0] == '#') {
			continue;
		}

		//frameCount \t firstFrame \t width \t height \t signature
		vector<string> parts;
		size_t start = 0;
		size_t pos;
		while((pos = line.find('\t', start)) != string::npos) {
			parts.push_back(line.substr(start, pos - start));
			start = pos + 1;
		}
		parts.push_back(line.substr(start));

		if(parts.size() == 5 && _studioObjects.size() < MaxStudioObjects) {
			HdStudioObjectStats stats;
			stats.FrameCount = (uint32_t)std::stoul(parts[0]);
			stats.FirstFrame = (uint32_t)std::stoul(parts[1]);
			stats.Width = (uint16_t)std::stoul(parts[2]);
			stats.Height = (uint16_t)std::stoul(parts[3]);
			_studioObjects[parts[4]] = stats;
		}
	}

	ifstream tilesFile(FolderUtilities::CombinePath(studioFolder, "tiles.txt"));
	while(std::getline(tilesFile, line)) {
		if(line.empty() || line[0] == '#') {
			continue;
		}

		//tile \t palette \t pixelCount
		size_t lastTab = line.rfind('\t');
		if(lastTab != string::npos && lastTab > 0) {
			_studioPreviousTileUsage[line.substr(0, lastTab)] += std::stoull(line.substr(lastTab + 1));
		}
	}

	//How the palettes of the tiles that were added without being seen were guessed (cumulative,
	//because the tiles stay in the pack and are never added again)
	ifstream unseenFile(FolderUtilities::CombinePath(studioFolder, "unseen.txt"));
	while(std::getline(unseenFile, line)) {
		size_t eq = line.find('=');
		if(eq == string::npos) {
			continue;
		}
		uint32_t value = (uint32_t)std::stoul(line.substr(eq + 1));
		string key = line.substr(0, eq);
		if(key == "prgPalette") {
			_studioUnseenTilesWithPrgPalette = value;
		} else if(key == "neighbourPalette") {
			_studioUnseenTilesWithNeighbourPalette = value;
		} else if(key == "defaultPalette") {
			_studioUnseenTilesWithDefaultPalette = value;
		}
	}
}
void HdPackBuilder::SaveHdStudioData()
{
	string studioFolder = FolderUtilities::CombinePath(_saveFolder, "HdStudio");
	FolderUtilities::CreateFolder(studioFolder);

	ofstream captureFile(FolderUtilities::CombinePath(studioFolder, "capture.txt"), ios::out);
	captureFile << "frames=" << _studioFrameCount << std::endl;
	captureFile << "scale=" << _hdData.Scale << std::endl;
	captureFile << "chrRam=" << (_isChrRam ? 1 : 0) << std::endl;
	captureFile << "rom=" << _romName << std::endl;
	captureFile << "addedChrTiles=" << _studioAddedChrTiles << std::endl;
	captureFile << "addedLevelTiles=" << _studioAddedLevelTiles << std::endl;
	captureFile << "prgPalettes=" << _studioPalettes.size() << std::endl;
	captureFile << "unseenTilesWithPrgPalette=" << _studioUnseenTilesWithPrgPalette << std::endl;
	captureFile << "unseenTilesWithNeighbourPalette=" << _studioUnseenTilesWithNeighbourPalette << std::endl;
	captureFile << "unseenTilesWithDefaultPalette=" << _studioUnseenTilesWithDefaultPalette << std::endl;
	captureFile.close();

	ofstream unseenFile(FolderUtilities::CombinePath(studioFolder, "unseen.txt"), ios::out);
	unseenFile << "prgPalette=" << _studioUnseenTilesWithPrgPalette << std::endl;
	unseenFile << "neighbourPalette=" << _studioUnseenTilesWithNeighbourPalette << std::endl;
	unseenFile << "defaultPalette=" << _studioUnseenTilesWithDefaultPalette << std::endl;
	unseenFile.close();

	//Palettes found in the game's code (PRG ROM), with how many times each one appears
	ofstream palettesFile(FolderUtilities::CombinePath(studioFolder, "palettes.txt"), ios::out);
	palettesFile << "#rom=" << _emu->GetRomInfo().RomFile.GetSha1Hash() << std::endl;
	palettesFile << "#palette\tuses\tfirstOffset" << std::endl;
	for(HdStudioPrgPalette& palette : _studioPalettes) {
		palettesFile << HexUtilities::ToHex(palette.Colors, true) << "\t" << palette.Uses << "\t" << HexUtilities::ToHex(palette.FirstOffset) << std::endl;
	}
	palettesFile.close();

	vector<std::pair<const string*, HdStudioObjectStats*>> objects;
	objects.reserve(_studioObjects.size());
	for(auto& kvp : _studioObjects) {
		objects.push_back({ &kvp.first, &kvp.second });
	}
	std::sort(objects.begin(), objects.end(), [](auto& a, auto& b) { return a.second->FrameCount > b.second->FrameCount; });

	ofstream objectsFile(FolderUtilities::CombinePath(studioFolder, "objects.txt"), ios::out);
	objectsFile << "#frameCount\tfirstFrame\twidth\theight\tsprites(dx,dy,tile,palette,flags;...)" << std::endl;
	for(auto& obj : objects) {
		objectsFile << obj.second->FrameCount << "\t" << obj.second->FirstFrame << "\t" << obj.second->Width << "\t" << obj.second->Height << "\t" << *obj.first << std::endl;
	}
	objectsFile.close();

	//Pixel usage of each tile+palette combination seen during this session
	ofstream tilesFile(FolderUtilities::CombinePath(studioFolder, "tiles.txt"), ios::out);
	std::map<string, uint64_t> tileUsage(_studioPreviousTileUsage.begin(), _studioPreviousTileUsage.end());
	for(auto& kvp : _studioTileUsage) {
		HdTileKey key = kvp.first;
		tileUsage[GetStudioTileKey(key.TileIndex, key.TileData) + "\t" + HexUtilities::ToHex(key.PaletteColors, true)] += kvp.second;
	}

	tilesFile << "#tile\tpalette\tpixelCount" << std::endl;
	for(auto& kvp : tileUsage) {
		tilesFile << kvp.first << "\t" << kvp.second << std::endl;
	}
	tilesFile.close();
}

void HdPackBuilder::GenerateHdTile(HdPackTileInfo* tile)
{
	uint32_t hdScale = _hdData.Scale;

	vector<uint32_t> originalTile = tile->ToRgb(_palette);
	vector<uint32_t> hdTile(8 * 8 * hdScale * hdScale, 0);

	switch(_options.FilterType) {
		case ScaleFilterType::HQX:
			hqx(hdScale, originalTile.data(), hdTile.data(), 8, 8);
			break;

		case ScaleFilterType::Prescale:
			hdTile.clear();
			for(uint8_t i = 0; i < 8 * hdScale; i++) {
				for(uint8_t j = 0; j < 8 * hdScale; j++) {
					hdTile.push_back(originalTile[i / hdScale * 8 + j / hdScale]);
				}
			}
			break;

		case ScaleFilterType::Scale2x:
			scale(hdScale, hdTile.data(), 8 * sizeof(uint32_t) * hdScale, originalTile.data(), 8 * sizeof(uint32_t), 4, 8, 8);
			break;

		case ScaleFilterType::_2xSai:
			twoxsai_generic_xrgb8888(8, 8, originalTile.data(), 8, hdTile.data(), 8 * hdScale);
			break;

		case ScaleFilterType::Super2xSai:
			supertwoxsai_generic_xrgb8888(8, 8, originalTile.data(), 8, hdTile.data(), 8 * hdScale);
			break;

		case ScaleFilterType::SuperEagle:
			supereagle_generic_xrgb8888(8, 8, originalTile.data(), 8, hdTile.data(), 8 * hdScale);
			break;

		case ScaleFilterType::xBRZ:
			xbrz::scale(hdScale, originalTile.data(), hdTile.data(), 8, 8, xbrz::ColorFormat::ARGB);
			break;
	}

	tile->HdTileData = hdTile;
}

void HdPackBuilder::DrawTile(HdPackTileInfo* tile, int tileNumber, uint32_t* pngBuffer, int pageNumber, bool containsSpritesOnly)
{
	if(tile->HdTileData.empty()) {
		GenerateHdTile(tile);
		tile->UpdateFlags();
	}

	if(containsSpritesOnly && _options.UseLargeSprites) {
		int row = tileNumber / 16;
		int column = tileNumber % 16;

		int newColumn = column / 2 + ((row & 1) ? 8 : 0);
		int newRow = (row & 0xFE) + ((column & 1) ? 1 : 0);

		tileNumber = newRow * 16 + newColumn;
	}

	tileNumber += pageNumber * (256 / (0x1000 / _options.ChrRamBankSize));

	int tileDimension = 8 * _hdData.Scale;
	int x = tileNumber % 16 * tileDimension;
	int y = tileNumber / 16 * tileDimension;

	tile->X = x;
	tile->Y = y;

	int pngWidth = 128 * _hdData.Scale;
	int pngPos = y * pngWidth + x;
	int tilePos = 0;
	for(uint8_t i = 0; i < tileDimension; i++) {
		for(uint8_t j = 0; j < tileDimension; j++) {
			pngBuffer[pngPos] = tile->HdTileData[tilePos++];
			pngPos++;
		}
		pngPos += pngWidth - tileDimension;
	}
}

void HdPackBuilder::SaveHdPack()
{
	FolderUtilities::CreateFolder(_saveFolder);

	if(!_isChrRam && !_chrRom.empty()) {
		//Exact level background combinations (written by --maps) before the guessed-palette pass,
		//so AddUnseenChrTiles sees those patterns as already covered.
		AddKnownLevelTiles();
	}
	if(_options.IncludeAllChrTiles && !_isChrRam && !_chrRom.empty()) {
		AddUnseenChrTiles();
	}

	stringstream pngRows;
	stringstream tileRows;
	stringstream ss;
	int pngIndex = 0;
	ss << "<ver>" << std::to_string(BaseHdNesPack::CurrentVersion) << std::endl;
	ss << "<scale>" << _hdData.Scale << std::endl;
	ss << "<supportedRom>" << _emu->GetRomInfo().RomFile.GetSha1Hash() << std::endl;
	if(_options.IgnoreOverscan) {
		OverscanDimensions overscan = _emu->GetSettings()->GetOverscan();
		ss << "<overscan>" << overscan.Top << "," << overscan.Right << "," << overscan.Bottom << "," << overscan.Left << std::endl;
	}

	int tileDimension = 8 * _hdData.Scale;
	int pngDimension = 16 * tileDimension;
	int pngBufferSize = pngDimension * pngDimension;
	uint32_t* pngBuffer = new uint32_t[pngBufferSize];

	int maxPageNumber = 0x1000 / _options.ChrRamBankSize;
	int pageNumber = 0;
	bool pngEmpty = true;
	int pngNumber = 0;

	for(int i = 0; i < pngBufferSize; i++) {
		pngBuffer[i] = 0xFFFF00FF;
	}

	auto savePng = [&tileRows, &pngRows, &ss, &pngBuffer, &pngDimension, &pngIndex, &pngBufferSize, &pngEmpty, &pngNumber, this](uint32_t chrBankId) {
		if(!pngEmpty) {
			string pngName;
			if(_isChrRam) {
				pngName = "Chr_" + std::to_string(pngNumber) + ".png";
			} else {
				pngName = "Chr_" + HexUtilities::ToHex(chrBankId) + "_" + std::to_string(pngNumber) + ".png";
			}

			tileRows << std::endl;
			tileRows << "#" << pngName << std::endl;
			tileRows << pngRows.str();
			pngRows = stringstream();

			ss << "<img>" << pngName << std::endl;
			PNGHelper::WritePNG(FolderUtilities::CombinePath(_saveFolder, pngName), pngBuffer, pngDimension, pngDimension, 32);
			pngNumber++;
			pngIndex++;

			for(int i = 0; i < pngBufferSize; i++) {
				pngBuffer[i] = 0xFFFF00FF;
			}
			pngEmpty = true;
		}
	};

	for(std::pair<const uint32_t, std::map<uint32_t, vector<HdPackTileInfo*>>>& kvp : _tilesByChrBankByPalette) {
		if(_options.SortByUsageFrequency) {
			for(int i = 0; i < 256; i++) {
				vector<std::pair<uint32_t, HdPackTileInfo*>> tiles;
				for(std::pair<const uint32_t, vector<HdPackTileInfo*>>& paletteMap : kvp.second) {
					if(paletteMap.second[i]) {
						tiles.push_back({ _tileUsageCount[paletteMap.second[i]->GetKey(false)], paletteMap.second[i] });
					}
				}
				std::sort(tiles.begin(), tiles.end(), [=](std::pair<uint32_t, HdPackTileInfo*>& a, std::pair<uint32_t, HdPackTileInfo*>& b) {
					return a.first > b.first;
				});

				size_t j = 0;
				for(std::pair<const uint32_t, vector<HdPackTileInfo*>>& paletteMap : kvp.second) {
					if(j < tiles.size()) {
						paletteMap.second[i] = tiles[j].second;
						j++;
					} else {
						paletteMap.second[i] = nullptr;
					}
				}
			}
		}

		if(!_isChrRam) {
			pngNumber = 0;
		}

		for(std::pair<const uint32_t, vector<HdPackTileInfo*>>& tileKvp : kvp.second) {
			bool pageEmpty = true;
			bool spritesOnly = true;
			for(HdPackTileInfo* tileInfo : tileKvp.second) {
				if(tileInfo && !tileInfo->IsSpriteTile()) {
					spritesOnly = false;
				}
			}

			for(int i = 0; i < 256; i++) {
				HdPackTileInfo* tileInfo = tileKvp.second[i];
				if(tileInfo) {
					DrawTile(tileInfo, i, pngBuffer, pageNumber, spritesOnly);

					pngRows << tileInfo->ToString(pngIndex) << std::endl;

					pageEmpty = false;
					pngEmpty = false;
				}
			}

			if(!pageEmpty) {
				pageNumber++;

				if(pageNumber == maxPageNumber) {
					savePng(kvp.first);
					pageNumber = 0;
				}
			}
		}
	}
	savePng(-1);

	for(unique_ptr<HdPackCondition>& condition : _hdData.Conditions) {
		if(!condition->IsExcludedFromFile()) {
			ss << condition->ToString() << std::endl;
		}
	}

	for(int i = 0; i < HdPackData::BgLayerCount; i++) {
		for(HdBackgroundInfo& bgInfo : _hdData.BackgroundsByPriority[i]) {
			ss << bgInfo.ToString() << std::endl;
		}
	}

	for(auto& bgmInfo : _hdData.BgmFilesById) {
		ss << "<bgm>" << std::to_string(bgmInfo.first >> 8) << "," << std::to_string(bgmInfo.first & 0xFF) << "," << VirtualFile(bgmInfo.second.Filename).GetFileName();
		if(bgmInfo.second.LoopPosition > 0) {
			ss << "," << std::to_string(bgmInfo.second.LoopPosition);
		}
		ss << std::endl;
	}

	for(auto& sfxInfo : _hdData.SfxFilesById) {
		ss << "<sfx>" << std::to_string(sfxInfo.first >> 8) << "," << std::to_string(sfxInfo.first & 0xFF) << "," << VirtualFile(sfxInfo.second).GetFileName() << std::endl;
	}

	for(auto& patchInfo : _hdData.PatchesByHash) {
		ss << "<patch>" << VirtualFile(patchInfo.second).GetFileName() << "," << patchInfo.first << std::endl;
	}

	if(_hdData.OptionFlags != 0) {
		ss << "<options>";
		if(_hdData.OptionFlags & (int)HdPackOptions::NoSpriteLimit) {
			ss << "disableSpriteLimit,";
		}
		if(_hdData.OptionFlags & (int)HdPackOptions::AlternateRegisterRange) {
			ss << "alternateRegisterRange,";
		}
		if(_hdData.OptionFlags & (int)HdPackOptions::DisableCache) {
			ss << "disableCache,";
		}
		if(_hdData.OptionFlags & (int)HdPackOptions::DontRenderOriginalTiles) {
			ss << "disableOriginalTiles,";
		}
		if(_hdData.OptionFlags & (int)HdPackOptions::AutomaticFallbackTiles) {
			ss << "automaticFallbackTiles,";
		}
	}

	ss << tileRows.str();

	ofstream hiresFile(FolderUtilities::CombinePath(_saveFolder, "hires.txt"), ios::out);
	hiresFile << ss.str();
	hiresFile.close();

	delete[] pngBuffer;

	SaveHdStudioData();
}
/*
void HdPackBuilder::GetChrBankList(uint32_t *banks)
{
	ConsolePauseHelper helper(_instance->_console.get());
	for(std::pair<const uint32_t, std::map<uint32_t, vector<HdPackTileInfo*>>> &kvp : _instance->_tilesByChrBankByPalette) {
		*banks = kvp.first;
		banks++;
	}
	*banks = -1;
}

void HdPackBuilder::GetBankPreview(uint32_t bankNumber, uint32_t pageNumber, uint32_t *rgbBuffer)
{
	ConsolePauseHelper helper(_instance->_console.get());

	for(uint32_t i = 0; i < 128 * 128 * _instance->_hdData.Scale*_instance->_hdData.Scale; i++) {
		rgbBuffer[i] = 0xFF666666;
	}

	auto result = _instance->_tilesByChrBankByPalette.find(bankNumber);
	if(result != _instance->_tilesByChrBankByPalette.end()) {
		std::map<uint32_t, vector<HdPackTileInfo*>> bankData = result->second;

		if(_instance->_flags & HdPackRecordFlags::SortByUsageFrequency) {
			for(int i = 0; i < 256; i++) {
				vector<std::pair<uint32_t, HdPackTileInfo*>> tiles;
				for(std::pair<const uint32_t, vector<HdPackTileInfo*>> &pageData : bankData) {
					if(pageData.second[i]) {
						tiles.push_back({ _instance->_tileUsageCount[pageData.second[i]->GetKey(false)], pageData.second[i] });
					}
				}

				std::sort(tiles.begin(), tiles.end(), [=](std::pair<uint32_t, HdPackTileInfo*> &a, std::pair<uint32_t, HdPackTileInfo*> &b) {
					return a.first > b.first;
				});

				size_t j = 0;
				for(std::pair<const uint32_t, vector<HdPackTileInfo*>> &pageData : bankData) {
					if(j < tiles.size()) {
						pageData.second[i] = tiles[j].second;
						j++;
					} else {
						pageData.second[i] = nullptr;
					}
				}
			}
		}

		bool spritesOnly = true;
		for(HdPackTileInfo* tileInfo : (*bankData.begin()).second) {
			if(tileInfo && !tileInfo->IsSpriteTile()) {
				spritesOnly = false;
			}
		}

		for(int i = 0; i < 256; i++) {
			HdPackTileInfo* tileInfo = (*bankData.begin()).second[i];
			if(tileInfo) {
				_instance->DrawTile(tileInfo, i, (uint32_t*)rgbBuffer, 0, spritesOnly);
			}
		}
	}
}
*/
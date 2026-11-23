#pragma once
#include "pch.h"
#include "NES/HdPacks/HdData.h"
#include "NES/NesTypes.h"
#include "Shared/SettingTypes.h"
#include <map>

class Emulator;
class BaseMapper;

struct HdPackBuilderOptions
{
	char* SaveFolder;
	ScaleFilterType FilterType;
	uint32_t Scale;
	uint32_t ChrRamBankSize;

	bool UseLargeSprites;
	bool SortByUsageFrequency;
	bool GroupBlankTiles;
	bool IgnoreOverscan;

	//HD Studio: also add every CHR-ROM tile that was not displayed during the recording (with a guessed palette)
	bool IncludeAllChrTiles;
};

//HD Studio: one 8x8 sprite tile as it appears on screen at the end of a frame
struct HdStudioSprite
{
	int16_t X;
	int16_t Y;
	int32_t TileIndex;
	uint8_t TileData[16];
	uint32_t PaletteColors;
	bool HorizontalMirror;
	bool VerticalMirror;
};

//HD Studio: direct access to the usage counters of the last processed tile, so the PPU can count the
//next pixels of the same tile without looking it up again (the lookup was done for each of the 8 pixels)
struct HdStudioTileHandle
{
	uint32_t* Usage = nullptr;
	uint32_t* StudioUsage = nullptr;
};

//HD Studio: statistics for a group of sprites drawn touching each other (a "metasprite")
struct HdStudioObjectStats
{
	uint32_t FrameCount = 0;
	uint32_t FirstFrame = 0;
	uint16_t Width = 0;
	uint16_t Height = 0;
};

//HD Studio: a 4-color palette found in the game's code (PRG ROM)
struct HdStudioPrgPalette
{
	//Same layout as HdPackTileInfo::PaletteColors, background form: [color0][color1][color2][color3]
	uint32_t Colors = 0;
	uint32_t Uses = 0;
	uint32_t FirstOffset = 0;
};

class HdPackBuilder
{
private:
	Emulator* _emu = nullptr;

	HdPackData _hdData;
	unordered_map<HdTileKey, uint32_t> _tileUsageCount;
	unordered_map<HdTileKey, HdPackTileInfo*> _tilesByKey;
	std::map<uint32_t, std::map<uint32_t, vector<HdPackTileInfo*>>> _tilesByChrBankByPalette;
	bool _isChrRam = false;
	string _saveFolder;
	string _romName;
	HdPackBuilderOptions _options = {};
	uint32_t _palette[512] = {};

	//Used to group blank tiles together
	uint32_t _blankTileIndex = 0;
	int _blankTilePalette = 0;

	//HD Studio capture data (written to the "HdStudio" subfolder of the pack)
	static constexpr size_t MaxStudioObjects = 100000;
	unordered_map<HdTileKey, uint32_t> _studioTileUsage;
	unordered_map<string, uint64_t> _studioPreviousTileUsage;
	unordered_map<string, HdStudioObjectStats> _studioObjects;
	uint32_t _studioFrameCount = 0;
	vector<uint8_t> _chrRom;
	vector<uint8_t> _prgRom;
	vector<HdStudioPrgPalette> _studioPalettes;
	uint32_t _studioAddedChrTiles = 0;
	uint32_t _studioAddedLevelTiles = 0;
	uint32_t _studioUnseenTilesWithPrgPalette = 0;
	uint32_t _studioUnseenTilesWithNeighbourPalette = 0;
	uint32_t _studioUnseenTilesWithDefaultPalette = 0;

	string GetStudioTileKey(int32_t tileIndex, uint8_t* tileData);
	uint32_t GetPrgPalette(bool spriteForm);
	void AddUnseenChrTiles();
	//HD Studio: adds the exact (CHR pattern, palette) combinations that --maps found in the levels
	void AddKnownLevelTiles();
	void AddKnownTileFile(const string& path);
	//HD Studio: one DefaultTile per CHR pattern, used when the exact palette was never seen
	void MarkDefaultTiles();
	void ScanPrgRomForPalettes();
	void LoadHdStudioData();
	void SaveHdStudioData();

	void AddTile(HdPackTileInfo* tile, uint32_t usageCount);
	void GenerateHdTile(HdPackTileInfo* tile);
	void DrawTile(HdPackTileInfo* tile, int tileIndex, uint32_t* pngBuffer, int pageNumber, bool containsSpritesOnly);

public:
	HdPackBuilder(Emulator* emu, PpuModel ppuModel, bool isChrRam, HdPackBuilderOptions options);
	~HdPackBuilder();

	HdStudioTileHandle ProcessTile(uint32_t x, uint32_t y, uint16_t tileAddr, HdPpuTileInfo& tile, BaseMapper* mapper, bool isSprite, uint32_t chrBankHash, bool transparencyRequired);

	__forceinline void RepeatTile(HdStudioTileHandle& handle)
	{
		if(*handle.Usage < 0x7FFFFFFF) {
			(*handle.Usage)++;
		}
		(*handle.StudioUsage)++;
	}
	void ProcessSpriteFrame(vector<HdStudioSprite>& sprites);
	void SetChrRom(BaseMapper* mapper);
	//HD Studio: keeps a copy of the PRG ROM and looks for the palette tables used by the game
	void SetPrgRom(BaseMapper* mapper);
	void SaveHdPack();

	//static void GetChrBankList(uint32_t *banks);
	//static void GetBankPreview(uint32_t bankNumber, uint32_t pageNumber, uint32_t *rgbBuffer);
};
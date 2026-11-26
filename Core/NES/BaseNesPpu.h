#pragma once
#include "pch.h"
#include "NES/INesMemoryHandler.h"
#include "Utilities/ISerializable.h"
#include "NES/NesTypes.h"
#include "NES/NesConstants.h"

enum class ConsoleRegion;

class Emulator;
class BaseMapper;
class SnesControlManager;
class NesConsole;
class EmuSettings;

//HD Studio: one sprite tile that actually contributed an opaque pixel to the frame that
//was displayed. This is deliberately not an end-of-frame OAM snapshot: it is produced by
//the renderer's sprite-selection path, so off-screen sprites, stale OAM entries and sprites
//whose pixels were hidden behind an opaque, higher-priority background never appear here.
struct NesRenderedSpriteInfo
{
	int16_t X;
	int16_t Y;
	int32_t TileIndex;
	uint32_t PaletteColors;
	uint8_t TileData[16];
	bool HorizontalMirror;
	bool VerticalMirror;
	bool BackgroundPriority;
	uint8_t RenderSlot;
	uint8_t Part;
};

//HD Studio v0.7: one background tile that was actually fetched and displayed by the
//renderer during the frame. Unlike the end-of-frame nametable snapshot, this records
//the pattern-table identity, palette, scroll and nametable cell at the moment the tile
//was drawn, so mid-frame CHR bank changes and mapper 4 pattern-table switches are
//captured correctly. X/Y are screen pixels; MapX/MapY are the 64x60 nametable-space
//coordinates (two nametables horizontally, two vertically).
struct NesRenderedBackgroundInfo
{
	int16_t X;
	int16_t Y;
	int32_t TileIndex;
	uint32_t PaletteColors;
	uint8_t TileData[16];
	int16_t ScrollX;
	int16_t ScrollY;
	uint16_t NameTableAddress;
	uint8_t MapX;
	uint8_t MapY;
	uint8_t Nametable;
	uint8_t SpriteCount;
};

class BaseNesPpu : public INesMemoryHandler, public ISerializable
{
public:
	BaseNesPpu()
	{
		for(int i = 0; i < 128; i++) {
			_renderedTileBySlotPart[i] = -1;
		}
		for(int i = 0; i < 1024; i++) {
			_renderedBgSignature[i] = 0xFFFFFFFF;
		}
	}

protected:
	uint64_t _masterClock = 0;
	uint32_t _cycle = 0;
	int16_t _scanline = 0;
	bool _emulatorBgEnabled = false;
	bool _emulatorSpritesEnabled = false;
	//16
	uint16_t _videoRamAddr = 0;
	uint16_t _tmpVideoRamAddr = 0;
	uint16_t _highBitShift = 0;
	uint16_t _lowBitShift = 0;
	uint8_t _masterClockDivider = 0;
	uint8_t _spriteRamAddr = 0;
	uint8_t _openBus = 0;
	uint8_t _xScroll = 0;
	bool _enableOamDecay = false;
	bool _needStateUpdate = false;
	bool _renderingEnabled = false;
	bool _prevRenderingEnabled = false;
	//32
	bool _sprite0Visible = false;
	uint8_t _spriteCount = 0;
	uint8_t _secondaryOamAddr = 0;
	uint8_t _oamCopybuffer = 0;
	bool _spriteInRange = false;
	bool _sprite0Added = false;
	uint8_t _overflowBugCounter = 0;
	bool _oamCopyDone = false;
	uint16_t _ppuBusAddress = 0;
	uint16_t _minimumDrawBgCycle = 0;
	uint16_t _minimumDrawSpriteCycle = 0;
	uint16_t _minimumDrawSpriteStandardCycle = 0;
	//48
	BaseMapper* _mapper = nullptr;
	uint16_t* _currentOutputBuffer = nullptr;
	////////////////////////
	//64 : end of cache line
	////////////////////////
	uint8_t _paletteRam[0x20] = {};
	uint8_t _secondarySpriteRam[0x20] = {};
	////////////////////////
	//128 : end of cache line
	////////////////////////
	TileInfo _tile = {};
	uint16_t _vblankEnd = 0;
	uint16_t _nmiScanline = 0;
	uint8_t _currentTilePalette = 0;
	uint8_t _previousTilePalette = 0;
	uint16_t _intensifyColorBits = 0;
	uint8_t _paletteRamMask = 0;
	uint8_t _updateVramAddrDelay = 0;
	//144
	uint32_t _spriteIndex = 0;
	int32_t _lastUpdatedPixel = 0;
	uint32_t _frameCount = 0;
	uint16_t _updateVramAddr = 0;
	bool _preventVblFlag = false;
	bool _writeToggle = false; //not used in rendering
	//160
	NesSpriteInfo* _lastSprite = nullptr; //used by HD ppu
	NesConsole* _console = nullptr;
	//176
	PpuControlFlags _control = {}; // 8 bytes
	PpuMaskFlags _mask = {}; // 8 bytes
	////////////////////////
	//192 : end of cache line
	////////////////////////
	uint8_t _spriteRam[0x100] = {};
	////////////////////////
	//448 : end of cache line
	////////////////////////
	NesSpriteInfo _spriteTiles[64] = {};

	//HD Studio: render-time sprite evidence. The current frame is filled by the concrete
	//PPU when a sprite pixel is actually selected for output; the last complete frame is
	//kept for the debugger/Lua API because SendFrame and the Lua endFrame callback happen
	//after the visible scanlines have ended.
	NesRenderedSpriteInfo _renderedSprites[128] = {};
	int _renderedSpriteCount = 0;
	NesRenderedSpriteInfo _lastRenderedSprites[128] = {};
	int _lastRenderedSpriteCount = 0;
	//HD Studio v0.7: render-time background evidence. One record per visible 8x8 cell
	//(a mid-frame pattern change appends a second record for the same cell). The
	//signature array is an O(1) de-duplication path and is reset at every frame.
	NesRenderedBackgroundInfo _renderedBackgrounds[1024] = {};
	int _renderedBackgroundCount = 0;
	NesRenderedBackgroundInfo _lastRenderedBackgrounds[1024] = {};
	int _lastRenderedBackgroundCount = 0;
	uint32_t _renderedBgSignature[1024] = {};
	bool _collectRenderedBackgrounds = false;
	//Source primary-OAM entry for each secondary-OAM shifter slot. This preserves the
	//hardware's documented OAM proximity evidence even though the PPU draws from
	//secondary OAM (which does not retain the original slot number).
	uint8_t _secondaryOamSource[8] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
	//O(1) de-duplication per render slot/part. The old linear scan of up to 128
	//entries could run on every visible sprite pixel.
	int32_t _renderedTileBySlotPart[128];
	bool _collectRenderedSprites = false;

	static constexpr int SpriteShifterDone = 0x8000;
	uint16_t _spriteShifterList[9] = { SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone, SpriteShifterDone }; //Ordered by X coordinate.
	uint8_t _nextSpriteShifter = 0;
	uint16_t _nextSpriteShifterCycle = 0;
	uint8_t _activeSpriteShifters = 0;
	uint8_t _countingSpriteShifters = 0;
	uint8_t _expiredSpriteShifters = 0;
	uint8_t _dotSkipped = 0;
	bool _processSprites = false;

	Emulator* _emu = nullptr;
	EmuSettings* _settings = nullptr;
	uint16_t* _outputBuffers[2] = {};

	ConsoleRegion _region = {};
	uint16_t _standardVblankEnd = 0;
	uint16_t _standardNmiScanline = 0;
	uint16_t _palSpriteEvalScanline = 0;

	bool _needVideoRamIncrement = false;
	bool _allowFullPpuAccess = false;

	uint8_t _ppuMemoryDataReadStateMachine = 0;
	uint8_t _ppuMemoryDataWriteStateMachine = 0;
	uint8_t _ppuMemoryDataWriteLatch = 0;
	uint8_t _memoryReadBuffer = 0;
	PPUStatusFlags _statusFlags = {};

	uint8_t _firstVisibleSpriteAddr = 0; //For extra sprites
	uint8_t _lastVisibleSpriteAddr = 0; //For extra sprites

	uint32_t _ignoreVramRead = 0;
	int32_t _openBusDecayStamp[8] = {};

	uint64_t _oamDecayCycles[0x20] = {};

	bool IsRenderingEnabled();
	void UpdateGrayscaleAndIntensifyBits();
	void UpdateColorBitMasks();
	void UpdateMinimumDrawCycles();

public:
	virtual void Reset(bool softReset) = 0;
	virtual void Run(uint64_t runTo) = 0;

	uint32_t GetFrameCount() { return _frameCount; }
	uint32_t GetCurrentCycle() { return _cycle; }
	int32_t GetCurrentScanline() { return _scanline; }

	//HD Studio live capture: a copy of the primary OAM used for the frame that just finished.
	void CopySpriteRam(uint8_t* dest) { memcpy(dest, _spriteRam, 0x100); }
	void CopyPaletteRam(uint8_t* dest) { memcpy(dest, _paletteRam, 0x20); }

	//HD Studio: called by the concrete PPU when a sprite contributes a visible pixel.
	//The base implementation records the pixel-time tile identity once per render slot,
	//part and tile; a mid-frame CHR bank change therefore records both patterns.
	void AddRenderedSprite(int16_t x, int16_t y, int32_t tileIndex, uint32_t paletteColors,
		const uint8_t* tileData, bool horizontalMirror, bool verticalMirror,
		bool backgroundPriority, uint8_t renderSlot, uint8_t part)
	{
		int key = ((renderSlot & 0x3F) << 1) | (part & 1);
		if(_renderedTileBySlotPart[key] == tileIndex) {
			return;
		}
		_renderedTileBySlotPart[key] = tileIndex;
		if(_renderedSpriteCount >= (int)(sizeof(_renderedSprites) / sizeof(_renderedSprites[0]))) {
			return;
		}
		NesRenderedSpriteInfo& info = _renderedSprites[_renderedSpriteCount++];
		info.X = x;
		info.Y = y;
		info.TileIndex = tileIndex;
		info.PaletteColors = paletteColors;
		memcpy(info.TileData, tileData, 16);
		info.HorizontalMirror = horizontalMirror;
		info.VerticalMirror = verticalMirror;
		info.BackgroundPriority = backgroundPriority;
		info.RenderSlot = renderSlot;
		info.Part = part;
	}

	void CommitRenderedSprites()
	{
		memcpy(_lastRenderedSprites, _renderedSprites, sizeof(NesRenderedSpriteInfo) * (size_t)_renderedSpriteCount);
		_lastRenderedSpriteCount = _renderedSpriteCount;
		_renderedSpriteCount = 0;
		for(int i = 0; i < 128; i++) {
			_renderedTileBySlotPart[i] = -1;
		}
	}

	void EnableRenderedSpriteCapture() { _collectRenderedSprites = true; }

	const NesRenderedSpriteInfo* GetLastRenderedSprites(int& count) const
	{
		count = _lastRenderedSpriteCount;
		return _lastRenderedSprites;
	}

	//HD Studio v0.7: record the background tile identity at the first display pixel of
	//each 8x8 cell. A different signature for the same cell (mid-frame CHR/pattern change)
	//is appended as another evidence record instead of overwriting the first one.
	void AddRenderedBackground(int16_t x, int16_t y, int32_t tileIndex, uint32_t paletteColors,
		const uint8_t* tileData, int16_t scrollX, int16_t scrollY, uint16_t nameTableAddress,
		uint8_t nametable, uint8_t spriteCount)
	{
		if(!_collectRenderedBackgrounds) {
			return;
		}
		if(x < 0 || x >= NesConstants::ScreenWidth || y < 0 || y >= NesConstants::ScreenHeight) {
			return;
		}
		int cell = ((y >> 3) * 32) + (x >> 3);
		if(cell < 0 || cell >= 1024) {
			return;
		}
		uint32_t signature = ((uint32_t)tileIndex * 2654435761u) ^ paletteColors ^
			((uint32_t)nameTableAddress << 16) ^ ((uint32_t)(uint16_t)scrollX << 8) ^ (uint32_t)(uint16_t)scrollY;
		if(tileData != nullptr) {
			//CHR-RAM content is part of the identity (the pattern index alone is not stable).
			for(int i = 0; i < 16; i += 4) {
				signature ^= ((uint32_t)tileData[i] << 24) | ((uint32_t)tileData[i + 1] << 16) |
					((uint32_t)tileData[i + 2] << 8) | tileData[i + 3];
			}
		}
		if(_renderedBgSignature[cell] == signature) {
			return;
		}
		_renderedBgSignature[cell] = signature;
		if(_renderedBackgroundCount >= (int)(sizeof(_renderedBackgrounds) / sizeof(_renderedBackgrounds[0]))) {
			return;
		}
		NesRenderedBackgroundInfo& info = _renderedBackgrounds[_renderedBackgroundCount++];
		info.X = x;
		info.Y = y;
		info.TileIndex = tileIndex;
		info.PaletteColors = paletteColors;
		if(tileData != nullptr) {
			memcpy(info.TileData, tileData, 16);
		} else {
			memset(info.TileData, 0, 16);
		}
		info.NameTableAddress = nameTableAddress;
		info.Nametable = nametable & 0x03;
		info.MapX = (uint8_t)(((nametable & 0x01) * 32) + (nameTableAddress & 0x1F));
		info.MapY = (uint8_t)((((nametable >> 1) & 0x01) * 30) + ((nameTableAddress >> 5) & 0x1F));
		//Derive the scroll from the actual nametable cell being displayed, not from the
		//loopy v register (which points at the next fetch and increments during the line).
		info.ScrollX = (int16_t)((((int)info.MapX * 8) - x) & 0x1FF);
		info.ScrollY = (int16_t)((((int)info.MapY * 8) - y) & 0x1FF);
		info.SpriteCount = spriteCount;
	}

	void CommitRenderedBackgrounds()
	{
		memcpy(_lastRenderedBackgrounds, _renderedBackgrounds, sizeof(NesRenderedBackgroundInfo) * (size_t)_renderedBackgroundCount);
		_lastRenderedBackgroundCount = _renderedBackgroundCount;
		_renderedBackgroundCount = 0;
		for(int i = 0; i < 1024; i++) {
			_renderedBgSignature[i] = 0xFFFFFFFF;
		}
	}

	void EnableRenderedBackgroundCapture() { _collectRenderedBackgrounds = true; }
	bool IsRenderedBackgroundCaptureEnabled() const { return _collectRenderedBackgrounds; }

	const NesRenderedBackgroundInfo* GetLastRenderedBackgrounds(int& count) const
	{
		count = _lastRenderedBackgroundCount;
		return _lastRenderedBackgrounds;
	}

	uint8_t GetSecondaryOamSource(uint32_t shifterSlot) const
	{
		//0xFF means the emulator could not attribute a primary-OAM slot to this
		//shifter (extra/corrupted sprites). The shifter ordinal is still a stable
		//per-frame identity; never return the sentinel, which would merge everything.
		uint8_t source = shifterSlot < 8 ? _secondaryOamSource[shifterSlot] : (uint8_t)(shifterSlot & 0x3F);
		return source == 0xFF ? (uint8_t)(shifterSlot & 0x3F) : source;
	}

	//Hook called from GetPixelColor only when a sprite pixel wins priority and is visible.
	virtual void NotifySpritePixelRendered(uint32_t spriteIndex) {}
	virtual bool SupportsRenderedSpriteCapture() { return false; }

	//HD Studio: the current $2000/$2001 flags, so capture scripts can resolve the real
	//sprite size and pattern-table base instead of assuming SMB3's 8x16 mode.
	PpuControlFlags GetControlFlags() { return _control; }
	PpuMaskFlags GetMaskFlags() { return _mask; }

	//HD Studio: the horizontal scroll as the game has set it (fine scroll + nametable), so a
	//script can check whether a RAM write really moved the camera
	uint32_t GetHorizontalScroll()
	{
		uint16_t addr = _tmpVideoRamAddr ? _tmpVideoRamAddr : _videoRamAddr;
		return (((addr & 0x1F) << 3) | _xScroll | ((addr & 0x400) ? 0x100 : 0)) & 0x1FF;
	}

	//HD Studio: the vertical scroll (coarse Y + fine Y + nametable Y bit) of the frame that
	//the PPU is drawing. Live capture uses it together with the nametable RAM to identify
	//the exact visible background window instead of assuming scrollY == 0.
	uint32_t GetVerticalScroll()
	{
		uint16_t addr = _tmpVideoRamAddr ? _tmpVideoRamAddr : _videoRamAddr;
		return ((((addr >> 5) & 0x1F) << 3) | ((addr >> 12) & 0x07) | ((addr & 0x800) ? 0x100 : 0)) & 0x1FF;
	}
	int32_t GetScanlineCount() { return _vblankEnd + 2; }
	uint32_t GetFrameCycle() { return ((_scanline + 1) * 341) + _cycle; }

	virtual uint16_t* GetScreenBuffer(bool previousBuffer, bool processGrayscaleEmphasisBits = false) = 0;
	virtual void UpdateTimings(ConsoleRegion region, bool overclockAllowed = true) = 0;

	void GetState(NesPpuState& state);
	void SetState(NesPpuState& state);

	uint16_t GetCurrentBgColor();

	uint8_t ReadPaletteRam(uint16_t addr);
	void WritePaletteRam(uint16_t addr, uint8_t value);

	void DebugSendFrame();

	virtual PpuModel GetPpuModel() = 0;
	virtual uint32_t GetPixelBrightness(uint8_t x, uint8_t y) = 0;

	virtual void GetMemoryRanges(MemoryRanges& ranges) override {}
	virtual uint8_t ReadRam(uint16_t addr) override { return 0; }
	virtual void WriteRam(uint16_t addr, uint8_t value) override {}

	virtual void Serialize(Serializer& s) override {}
};

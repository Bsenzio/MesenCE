#pragma once
#include "pch.h"
#include "NES/HdPacks/HdNesPpu.h"
#include "NES/HdPacks/HdNesPack.h"
#include "NES/HdPacks/HdPackBuilder.h"
#include "Shared/Video/VideoDecoder.h"
#include "Shared/RewindManager.h"
#include "Utilities/Serializer.h"

class HdBuilderPpu final : public NesPpu<HdBuilderPpu>
{
private:
	HdPackBuilder* _hdPackBuilder = nullptr;
	bool _needChrHash = false;
	uint32_t _chrRamBankSize = 0;
	uint32_t _chrRamIndexMask = 0;
	vector<uint32_t> _bankHashes;

	//HD Studio: the builder used to look up the same tile for each of its 8 pixels on a scanline.
	//The last tile processed for the background and for sprites is cached and only its counters are increased.
	//The cache is invalidated by any VRAM/palette write and when a state is loaded.
	struct TileCache
	{
		int32_t Addr = -1;
		uint16_t Palette = 0;
		int16_t Scanline = -1;
		uint32_t Version = 0;
		bool Valid = false;
		HdStudioTileHandle Handle;
	};
	TileCache _bgCache;
	TileCache _spriteCache;
	uint32_t _cacheVersion = 1;

	__forceinline bool IsCached(TileCache& cache, int32_t addr, uint16_t palette)
	{
		return cache.Valid && cache.Addr == addr && cache.Palette == palette && cache.Scanline == _scanline && cache.Version == _cacheVersion;
	}

	__forceinline void UpdateCache(TileCache& cache, int32_t addr, uint16_t palette, HdStudioTileHandle handle, bool allowCache)
	{
		cache.Addr = addr;
		cache.Palette = palette;
		cache.Scanline = (int16_t)_scanline;
		cache.Version = _cacheVersion;
		cache.Handle = handle;
		cache.Valid = allowCache && handle.Usage != nullptr;
	}

	NesSpriteInfoEx _exSpriteInfo[64] = {};
	NesTileInfoEx _previousTileEx = {};
	NesTileInfoEx _currentTileEx = {};
	NesTileInfoEx _nextTileEx = {};
	//HD Studio v0.7: render-time background evidence uses the same structure as the
	//HD PPU. The builder has no HdScreenInfo, so the tile identity is taken directly
	//from the mapper-resolved fetch state below.
	uint16_t _backgroundNameTableAddress = 0;
	uint8_t _backgroundPalette = 0;

public:
	__forceinline bool RemoveSpriteLimit() { return _console->GetNesConfig().RemoveSpriteLimit; }
	__forceinline bool UseAdaptiveSpriteLimit() { return _console->GetNesConfig().AdaptiveSpriteLimit; }
	void* OnBeforeSendFrame()
	{
		CaptureSpriteFrame();
		CommitRenderedBackgrounds();
		return nullptr;
	}

	//HD Studio: a sprite pixel actually reached the visible output. Capture the exact
	//mapper-resolved pattern and palette that were used at that scanline. Sprite-limit
	//behaviour, flicker, off-screen OAM entries and background-priority-hidden sprites are
	//therefore resolved by the renderer itself, not reconstructed from end-of-frame OAM.
	bool SupportsRenderedSpriteCapture() override { return true; }

	void NotifySpritePixelRendered(uint32_t spriteIndex) override
	{
		if(spriteIndex >= 64) {
			return;
		}
		NesSpriteInfoEx& ex = _exSpriteInfo[spriteIndex];
		NesSpriteInfo& sprite = _spriteTiles[spriteIndex];
		if(ex.AbsoluteTileAddr < 0) {
			return;
		}
		BaseMapper* mapper = _console->GetMapper();
		bool isChrRam = !mapper->HasChrRom();
		int32_t tileIndex = (isChrRam ? (ex.TileAddr & _chrRamIndexMask) : (uint32_t)ex.AbsoluteTileAddr) / 16;
		uint32_t paletteOffset = sprite.PaletteOffset;
		uint32_t paletteColors = ReadPaletteRam(paletteOffset + 3) | (ReadPaletteRam(paletteOffset + 2) << 8) | (ReadPaletteRam(paletteOffset + 1) << 16) | 0xFF000000;
		uint8_t data[16] = {};
		mapper->CopyChrTile((uint32_t)ex.AbsoluteTileAddr & 0xFFFFFFF0, data);
		//OffsetY is the *source* row. Vertical flip reverses it, so convert to the
		//display row before deriving the 8x16 half and the on-screen top-left.
		int sizeMask = _control.LargeSprites ? 15 : 7;
		int displayRow = ex.VerticalMirror ? (sizeMask - ex.OffsetY) : ex.OffsetY;
		int part = displayRow >= 8 ? 1 : 0;
		int16_t y = (int16_t)(_scanline - displayRow + part * 8);
		AddRenderedSprite(sprite.SpriteX, y, tileIndex, paletteColors, data,
			ex.HorizontalMirror, ex.VerticalMirror, sprite.BackgroundPriority,
			GetSecondaryOamSource(spriteIndex), (uint8_t)part);
	}

	//HD Studio: send the sprites that actually contributed visible pixels this frame to
	//the composition builder. The end-of-frame OAM is no longer used for this evidence.
	void CaptureSpriteFrame()
	{
		vector<HdStudioSprite> sprites;
		sprites.reserve(128);
		for(int i = 0; i < _renderedSpriteCount; i++) {
			NesRenderedSpriteInfo& rendered = _renderedSprites[i];
			HdStudioSprite sprite = {};
			sprite.X = rendered.X;
			sprite.Y = rendered.Y;
			sprite.TileIndex = rendered.TileIndex;
			memcpy(sprite.TileData, rendered.TileData, sizeof(sprite.TileData));
			sprite.PaletteColors = rendered.PaletteColors;
			sprite.HorizontalMirror = rendered.HorizontalMirror;
			sprite.VerticalMirror = rendered.VerticalMirror;
			sprite.OamIndex = rendered.RenderSlot;
			sprite.OamPart = rendered.Part;
			sprites.push_back(sprite);
		}
		_hdPackBuilder->ProcessSpriteFrame(sprites);
		CommitRenderedSprites();
	}

	__forceinline void StoreSpriteInformation(bool horizontalMirror, bool verticalMirror, uint16_t tileAddr, uint8_t lineOffset, NesSpriteInfo& sprite)
	{
		NesSpriteInfoEx& info = _exSpriteInfo[_spriteIndex];
		info.TileAddr = tileAddr;
		info.AbsoluteTileAddr = _mapper->GetPpuAbsoluteAddress(info.TileAddr).Address;
		info.HorizontalMirror = horizontalMirror;
		info.VerticalMirror = verticalMirror;
		info.OffsetY = lineOffset;
		info.LowByte = sprite.LowByte;
		info.HighByte = sprite.HighByte;
	}

	__forceinline void PushTileInformation()
	{
		_previousTileEx = _currentTileEx;
		_currentTileEx = _nextTileEx;
	}

	__forceinline void StoreTileInformation()
	{
		uint8_t tileIndex = _mapper->DebugReadVram(GetNameTableAddr());
		uint16_t tileAddr = (tileIndex << 4) | (_videoRamAddr >> 12) | _control.BackgroundPatternAddr;

		_nextTileEx.OffsetY = _videoRamAddr >> 12;
		_nextTileEx.NameTableAddress = GetNameTableAddr();
		_nextTileEx.TileAddr = tileAddr;
		_nextTileEx.AbsoluteTileAddr = _mapper->GetPpuAbsoluteAddress(tileAddr).Address;
	}

	__forceinline void ProcessScanline()
	{
		ProcessScanlineImpl();
	}

	void DrawPixel()
	{
		if(IsRenderingEnabled() || ((_videoRamAddr & 0x3F00) != 0x3F00)) {
			BaseMapper* mapper = _console->GetMapper();
			bool isChrRam = !mapper->HasChrRom();

			_lastSprite = nullptr;
			uint32_t color = GetPixelColor();
			_currentOutputBuffer[(_scanline << 8) + _cycle - 1] = _paletteRam[color & 0x03 ? color : 0];
			uint32_t backgroundColor = 0;
			if(_mask.BackgroundEnabled && _cycle > _minimumDrawBgCycle) {
				backgroundColor = (((_lowBitShift << _xScroll) & 0x8000) >> 15) | (((_highBitShift << _xScroll) & 0x8000) >> 14);
			}

			if(_needChrHash) {
				uint16_t addr = 0;
				_bankHashes.clear();
				while(addr < 0x2000) {
					uint32_t hash = 0;
					for(uint16_t i = 0; i < _chrRamBankSize; i++) {
						hash += mapper->DebugReadVram(i + addr);
						hash = (hash << 1) | (hash >> 31);
					}
					_bankHashes.push_back(hash);
					addr += _chrRamBankSize;
				}
				_needChrHash = false;
			}

			bool hasBgSprite = false;
			if(_lastSprite && _mask.SpritesEnabled) {
				uint8_t spriteIndex = (uint8_t)(_lastSprite - _spriteTiles);
				NesSpriteInfoEx& spriteInfoEx = _exSpriteInfo[spriteIndex];

				if(backgroundColor == 0) {
					for(uint8_t i = 0; i < _spriteCount; i++) {
						if(_spriteTiles[i].BackgroundPriority) {
							hasBgSprite = true;
							break;
						}
					}
				}

				if(spriteInfoEx.AbsoluteTileAddr >= 0 && IsCached(_spriteCache, spriteInfoEx.AbsoluteTileAddr, _lastSprite->PaletteOffset)) {
					_hdPackBuilder->RepeatTile(_spriteCache.Handle);
				} else if(spriteInfoEx.AbsoluteTileAddr >= 0) {
					HdPpuTileInfo sprite = {};
					sprite.TileIndex = (isChrRam ? (spriteInfoEx.TileAddr & _chrRamIndexMask) : spriteInfoEx.AbsoluteTileAddr) / 16;
					sprite.PaletteColors = ReadPaletteRam(_lastSprite->PaletteOffset + 3) | (ReadPaletteRam(_lastSprite->PaletteOffset + 2) << 8) | (ReadPaletteRam(_lastSprite->PaletteOffset + 1) << 16) | 0xFF000000;
					sprite.IsChrRamTile = isChrRam;
					mapper->CopyChrTile(spriteInfoEx.AbsoluteTileAddr & 0xFFFFFFF0, sprite.TileData);

					HdStudioTileHandle handle = _hdPackBuilder->ProcessTile(_cycle - 1, _scanline, spriteInfoEx.AbsoluteTileAddr, sprite, mapper, false, _bankHashes[spriteInfoEx.TileAddr / _chrRamBankSize], false);
					UpdateCache(_spriteCache, spriteInfoEx.AbsoluteTileAddr, _lastSprite->PaletteOffset, handle, true);
				}
			}

			if(_mask.BackgroundEnabled) {
				bool usePrev = (_xScroll + ((_cycle - 1) & 0x07) < 8);
				uint8_t tilePalette = usePrev ? _previousTilePalette : _currentTilePalette;
				NesTileInfoEx& lastTileEx = usePrev ? _previousTileEx : _currentTileEx;
				//TileInfo* lastTile = &((_xScroll + ((_cycle - 1) & 0x07) < 8) ? _previousTile : _currentTile);
				if(lastTileEx.AbsoluteTileAddr >= 0 && !hasBgSprite && IsCached(_bgCache, lastTileEx.AbsoluteTileAddr, tilePalette)) {
					_hdPackBuilder->RepeatTile(_bgCache.Handle);
				} else if(lastTileEx.AbsoluteTileAddr >= 0) {
					HdPpuTileInfo tile = {};
					tile.TileIndex = (isChrRam ? (lastTileEx.TileAddr & _chrRamIndexMask) : lastTileEx.AbsoluteTileAddr) / 16;
					tile.PaletteColors = ReadPaletteRam(tilePalette + 3) | (ReadPaletteRam(tilePalette + 2) << 8) | (ReadPaletteRam(tilePalette + 1) << 16) | (ReadPaletteRam(0) << 24);
					tile.IsChrRamTile = isChrRam;
					mapper->CopyChrTile(lastTileEx.AbsoluteTileAddr & 0xFFFFFFF0, tile.TileData);

					HdStudioTileHandle handle = _hdPackBuilder->ProcessTile(_cycle - 1, _scanline, lastTileEx.AbsoluteTileAddr, tile, mapper, false, _bankHashes[lastTileEx.TileAddr / _chrRamBankSize], hasBgSprite);
					//Pixels where transparency is required update the tile flags: never cache them
					UpdateCache(_bgCache, lastTileEx.AbsoluteTileAddr, tilePalette, handle, !hasBgSprite);
				}

				if(_collectRenderedBackgrounds && ((_cycle - 1) & 0x07) == 0 && (_scanline & 0x07) == 0) {
					uint8_t tileData[16] = {};
					mapper->CopyChrTile(lastTileEx.AbsoluteTileAddr & 0xFFFFFFF0, tileData);
					uint32_t paletteColors = ReadPaletteRam(tilePalette + 3) | (ReadPaletteRam(tilePalette + 2) << 8) |
						(ReadPaletteRam(tilePalette + 1) << 16) | (ReadPaletteRam(0) << 24);
					AddRenderedBackground((int16_t)(_cycle - 1), _scanline, (int32_t)(lastTileEx.AbsoluteTileAddr / 16),
						paletteColors, tileData, (int16_t)GetHorizontalScroll(), (int16_t)GetVerticalScroll(),
						lastTileEx.NameTableAddress, (uint8_t)((lastTileEx.NameTableAddress >> 10) & 0x03),
						hasBgSprite ? 1 : 0);
				}
			}
		} else {
			//"If the current VRAM address points in the range $3F00-$3FFF during forced blanking, the color indicated by this palette location will be shown on screen instead of the backdrop color."
			_currentOutputBuffer[(_scanline << 8) + _cycle - 1] = _paletteRam[_videoRamAddr & 0x1F];
		}
	}

	void WriteRAM(uint16_t addr, uint8_t value)
	{
		if(GetRegisterID(addr) == PpuRegisters::VideoMemoryData) {
			if(_videoRamAddr < 0x2000) {
				_needChrHash = true;
			}
			//CHR RAM or palette contents may change: invalidate the tile caches
			_cacheVersion++;
		}
		NesPpu::WriteRam(addr, value);
	}

	void Serialize(Serializer& s)
	{
		NesPpu::Serialize(s);
		if(!s.IsSaving()) {
			_needChrHash = true;
			_cacheVersion++;
		}
	}

public:
	HdBuilderPpu(NesConsole* console, HdPackBuilder* hdPackBuilder, uint32_t chrRamBankSize) : NesPpu(console)
	{
		_hdPackBuilder = hdPackBuilder;
		_chrRamBankSize = chrRamBankSize;
		_chrRamIndexMask = chrRamBankSize - 1;
		_needChrHash = true;
		EnableRenderedSpriteCapture();
		EnableRenderedBackgroundCapture();
	}
};
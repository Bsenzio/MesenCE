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
	/// Physical NES cartridge image used by the static-first archaeology engine.
	///
	/// This is deliberately separate from <see cref="NesRomData"/>: the report/importer
	/// class only needs CHR bytes, while archaeology needs the exact iNES/NES 2.0 header
	/// fields, trainer/PRG/CHR boundaries, physical bank offsets and mapper mapping
	/// assumptions. Nothing here executes or modifies the ROM.
	/// </summary>
	internal sealed class HdStudioRomImage
	{
		public string FilePath = "";
		public string FileName = "";
		public string Sha256 = "";
		public string Sha1 = "";
		public string Sha256NoHeader = "";
		public long FileSize;
		public bool Valid;
		public string Error = "";

		public bool Nes20;
		public bool HasTrainer;
		public int Mapper;
		public int Submapper;
		public int Mirroring;
		public bool Battery;
		public bool FourScreen;
		public int ConsoleType;
		public int PrgSize;
		public int ChrSize;
		public int PrgRamSize;
		public int ChrRamSize;
		public int PrgOffset;
		public int ChrOffset;
		public int TrailingBytes;

		public byte[] Bytes = Array.Empty<byte>();
		public byte[] Prg = Array.Empty<byte>();
		public byte[] Chr = Array.Empty<byte>();

		public bool HasChrRom => ChrSize > 0 && Chr.Length > 0;
		public string ChrMode => HasChrRom ? "CHR-ROM" : "CHR-RAM";
		public int ChrTileCount => Chr.Length / 16;
		public int ChrBankCount => Chr.Length / 8192;
		public int PrgBank16Count => Prg.Length / 16384;

		public static HdStudioRomImage Load(string path)
		{
			HdStudioRomImage rom = new() {
				FilePath = Path.GetFullPath(path),
				FileName = Path.GetFileName(path)
			};
			try {
				byte[] data = File.ReadAllBytes(path);
				rom.Bytes = data;
				rom.FileSize = data.Length;
				if(data.Length < 16) {
					rom.Error = "file is shorter than an iNES header";
					return rom;
				}
				rom.Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
				rom.Sha1 = Convert.ToHexString(SHA1.HashData(data)).ToUpperInvariant();
				if(data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A) {
					rom.Error = "not an iNES/NES 2.0 image (FDS, UNIF and raw dumps need a separate loader)";
					return rom;
				}

				rom.Nes20 = (data[7] & 0x0C) == 0x08;
				rom.HasTrainer = (data[6] & 0x04) != 0;
				rom.Battery = (data[6] & 0x02) != 0;
				rom.Mirroring = data[6] & 0x01;
				rom.FourScreen = (data[6] & 0x08) != 0;
				rom.ConsoleType = data[7] & 0x03;

				int prgUnits = data[4];
				int chrUnits = data[5];
				rom.Mapper = (data[6] >> 4) | (data[7] & 0xF0);
				if(rom.Nes20) {
					prgUnits |= (data[9] & 0x0F) << 8;
					chrUnits |= (data[9] & 0xF0) << 4;
					rom.Mapper |= (data[8] & 0x0F) << 8;
					rom.Submapper = (data[8] >> 4) & 0x0F;
					rom.PrgRamSize = data[10] == 0 ? 0 : (data[10] & 0x0F) * 8192;
					rom.ChrRamSize = data[10] == 0 ? 0 : ((data[10] >> 4) & 0x0F) * 8192;
				}

				rom.PrgOffset = 16 + (rom.HasTrainer ? 512 : 0);
				rom.PrgSize = Math.Max(0, prgUnits * 16384);
				rom.ChrSize = Math.Max(0, chrUnits * 8192);
				if(rom.Nes20) {
					//NES 2.0 exponent-multiplier size encoding: when the MSB nibble
					//is 0xF, the LSB is 2^E * (2*M+1) bytes rather than a count of
					//16 KB/8 KB units. Galaxian Rev 1 is a real example (8 KB PRG).
					if((data[9] & 0x0F) == 0x0F) {
						int exponent = data[4] >> 2;
						int multiplier = data[4] & 0x03;
						rom.PrgSize = (1 << exponent) * (multiplier * 2 + 1);
					}
					if((data[9] & 0xF0) == 0xF0) {
						int exponent = data[5] >> 2;
						int multiplier = data[5] & 0x03;
						rom.ChrSize = (1 << exponent) * (multiplier * 2 + 1);
					}
				}
				if(rom.PrgSize == 0) {
					rom.Error = "iNES header declares zero PRG-ROM";
					return rom;
				}
				if(rom.PrgOffset >= data.Length) {
					rom.Error = "PRG-ROM offset is beyond the end of the file";
					return rom;
				}

				int availablePrg = Math.Min(rom.PrgSize, data.Length - rom.PrgOffset);
				rom.Prg = new byte[availablePrg];
				Array.Copy(data, rom.PrgOffset, rom.Prg, 0, availablePrg);

				rom.ChrOffset = Math.Clamp(rom.PrgOffset + rom.PrgSize, 0, data.Length);
				int availableChr = Math.Max(0, Math.Min(rom.ChrSize, data.Length - rom.ChrOffset));
				rom.Chr = new byte[availableChr];
				if(availableChr > 0) {
					Array.Copy(data, rom.ChrOffset, rom.Chr, 0, availableChr);
				}
				rom.TrailingBytes = Math.Max(0, data.Length - (rom.ChrOffset + availableChr));
				rom.Sha256NoHeader = Convert.ToHexString(SHA256.HashData(data.AsSpan(rom.PrgOffset))).ToLowerInvariant();

				if(availablePrg < rom.PrgSize) {
					rom.Error = "PRG-ROM is truncated (header " + rom.PrgSize.ToString(CultureInfo.InvariantCulture) + " bytes, file has " + availablePrg.ToString(CultureInfo.InvariantCulture) + ")";
					return rom;
				}
				rom.Valid = true;
			} catch(Exception ex) {
				rom.Error = ex.Message;
				rom.Valid = false;
			}
			return rom;
		}

		public byte[] GetChrTile(int index)
		{
			byte[] tile = new byte[16];
			if(index < 0 || index * 16 + 16 > Chr.Length) {
				return tile;
			}
			Array.Copy(Chr, index * 16, tile, 0, 16);
			return tile;
		}

		public bool IsBlankChrTile(int index)
		{
			if(index < 0 || index * 16 + 16 > Chr.Length) {
				return true;
			}
			for(int i = index * 16; i < index * 16 + 16; i++) {
				if(Chr[i] != 0) {
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Physical CHR pages. A 1 KB page is the smallest standard PPU pattern-table
		/// unit; an 8 KB CHR-ROM bank is the usual mapper unit. Both are recorded so a
		/// later mapper-aware stage never equates a banked address with physical identity.
		/// </summary>
		public List<HdStudioChrPage> GetChrPages()
		{
			List<HdStudioChrPage> pages = new();
			int pageCount = Chr.Length / 1024;
			for(int i = 0; i < pageCount; i++) {
				pages.Add(new HdStudioChrPage() {
					Index = i,
					BankIndex = i / 8,
					PageInBank = i % 8,
					PhysicalOffset = i * 1024,
					Tiles = 64
				});
			}
			return pages;
		}

		/// <summary>
		/// Conservative CPU mapping assumptions. These are hypotheses used to seed a
		/// bounded control-flow walk; they are not a claim that every switchable bank is
		/// mapped at that address at the same time.
		/// </summary>
		public List<HdStudioPrgWindow> GetPrgWindows()
		{
			List<HdStudioPrgWindow> windows = new();
			if(Prg.Length == 0) {
				return windows;
			}
			bool eightKb = Mapper == 4 || Mapper == 9 || Mapper == 10 || Mapper == 12 || Mapper == 37 || Mapper == 44 || Mapper == 47 || Mapper == 49 || Mapper == 52 || Mapper == 74 || Mapper == 118 || Mapper == 119 || Mapper == 154 || Mapper == 165 || Mapper == 191 || Mapper == 192 || Mapper == 194 || Mapper == 195 || Mapper == 198 || Mapper == 209 || Mapper == 210 || Mapper == 215 || Mapper == 228 || Mapper == 232;
			int bankSize = eightKb ? 8192 : 16384;
			int count = Prg.Length / bankSize;
			for(int i = 0; i < count; i++) {
				int baseAddress;
				bool isFixed;
				string mapping;
				if(eightKb) {
					if(i == count - 1) {
						baseAddress = 0xE000;
						isFixed = true;
						mapping = "last 8 KB fixed at $E000 (MMC3-family assumption)";
					} else if(i == count - 2) {
						baseAddress = 0xC000;
						isFixed = true;
						mapping = "second-last 8 KB at $C000 (MMC3-family assumption)";
					} else {
						baseAddress = 0x8000;
						isFixed = false;
						mapping = "switchable 8 KB bank, assumed at $8000 for analysis";
					}
				} else {
					if(i == count - 1) {
						baseAddress = 0xC000;
						isFixed = true;
						mapping = "last 16 KB fixed at $C000";
					} else if(Mapper == 0 || Mapper == 3 || Mapper == 7 || Mapper == 11 || Mapper == 34 || Mapper == 66 || Mapper == 71) {
						baseAddress = 0x8000 + i * 0x4000;
						if(baseAddress >= 0x10000) {
							baseAddress = 0x8000;
						}
						isFixed = Mapper == 0 || Mapper == 3;
						mapping = isFixed ? "NROM/CNROM window at $8000" : "32 KB switchable window, assumed at $8000";
					} else {
						baseAddress = 0x8000;
						isFixed = false;
						mapping = "switchable 16 KB bank, assumed at $8000";
					}
				}
				windows.Add(new HdStudioPrgWindow() {
					Index = i,
					PhysicalOffset = i * bankSize,
					Length = bankSize,
					CpuBase = baseAddress,
					Fixed = isFixed,
					Description = mapping
				});
			}
			return windows;
		}

		/// <summary>
		/// Reset/NMI/IRQ vectors from the last physical PRG bank. Returns the CPU address,
		/// the physical file offset inside PRG and the physical bank index.
		/// </summary>
		public List<HdStudioVector> GetVectors()
		{
			List<HdStudioVector> vectors = new();
			List<HdStudioPrgWindow> windows = GetPrgWindows();
			HdStudioPrgWindow? vectorWindow = windows.LastOrDefault(w => w.Fixed && w.CpuBase + w.Length >= 0x10000);
			if(vectorWindow == null) {
				vectorWindow = windows.LastOrDefault();
			}
			if(vectorWindow == null) {
				return vectors;
			}
			int vectorOffsetInWindow = vectorWindow.Length - 6;
			if(vectorOffsetInWindow < 0 || vectorWindow.PhysicalOffset + vectorOffsetInWindow + 6 > Prg.Length) {
				return vectors;
			}
			int vectorCpuBase = 0x10000 - 6;
			string[] names = { "NMI", "RESET", "IRQ/BRK" };
			for(int i = 0; i < 3; i++) {
				int offset = vectorWindow.PhysicalOffset + vectorOffsetInWindow + i * 2;
				int target = Prg[offset] | (Prg[offset + 1] << 8);
				vectors.Add(new HdStudioVector() {
					Name = names[i],
					CpuAddress = vectorCpuBase + i * 2,
					PhysicalOffset = offset,
					Target = target,
					BankIndex = vectorWindow.Index
				});
			}
			return vectors;
		}

		public static string DescribeMapper(int mapper, int submapper)
		{
			return mapper.ToString(CultureInfo.InvariantCulture) + (submapper > 0 ? "." + submapper.ToString(CultureInfo.InvariantCulture) : "");
		}
	}

	internal sealed class HdStudioChrPage
	{
		public int Index;
		public int BankIndex;
		public int PageInBank;
		public int PhysicalOffset;
		public int Tiles;
	}

	internal sealed class HdStudioPrgWindow
	{
		public int Index;
		public int PhysicalOffset;
		public int Length;
		public int CpuBase;
		public bool Fixed;
		public string Description = "";
		public int CpuEnd => CpuBase + Length;
	}

	internal sealed class HdStudioVector
	{
		public string Name = "";
		public int CpuAddress;
		public int PhysicalOffset;
		public int Target;
		public int BankIndex;
	}
}

using System;
using System.Globalization;

namespace Mesen.HdStudio
{
	internal enum HdStudioAddressMode
	{
		Implied,
		Accumulator,
		Immediate,
		ZeroPage,
		ZeroPageX,
		ZeroPageY,
		Absolute,
		AbsoluteX,
		AbsoluteY,
		Indirect,
		IndexedIndirect,
		IndirectIndexed,
		Relative
	}

	internal enum HdStudioCpuFlow
	{
		Next,
		Branch,
		Jump,
		JumpIndirect,
		Call,
		Return,
		Interrupt
	}

	internal sealed class HdStudioOpcode
	{
		public byte Code;
		public string Mnemonic = "";
		public HdStudioAddressMode Mode;
		public HdStudioCpuFlow Flow;
		public int Length;
		public bool Documented = true;
	}

	internal sealed class HdStudioInstruction
	{
		public int BankIndex;
		public int PhysicalOffset;
		public int CpuAddress;
		public byte Opcode;
		public string Mnemonic = "";
		public HdStudioAddressMode Mode;
		public HdStudioCpuFlow Flow;
		public int Length;
		public int? Immediate;
		public int? AbsoluteAddress;
		public int? BranchTarget;
		public int? IndirectPointer;
		public string Text = "";
	}

	/// <summary>
	/// Conservative NMOS 6502 decoder. Only documented opcodes are decoded. An
	/// undocumented byte ends the basic block and is reported as an unresolved byte
	/// instead of being guessed, so the structural walk never invents control flow.
	/// </summary>
	internal static class HdStudioCpu6502
	{
		private static readonly HdStudioOpcode[] _opcodes = Build();

		public static HdStudioOpcode Get(byte code) => _opcodes[code];

		public static bool IsBranch(byte code) => _opcodes[code].Flow == HdStudioCpuFlow.Branch;

		public static bool TryDecode(byte[] code, int offset, int cpuAddress, int bankIndex, out HdStudioInstruction instruction)
		{
			instruction = new HdStudioInstruction() {
				BankIndex = bankIndex,
				PhysicalOffset = offset,
				CpuAddress = cpuAddress
			};
			if(offset < 0 || offset >= code.Length) {
				return false;
			}
			HdStudioOpcode info = _opcodes[code[offset]];
			if(!info.Documented || info.Length <= 0) {
				instruction.Opcode = code[offset];
				instruction.Mnemonic = ".byte";
				instruction.Length = 1;
				instruction.Flow = HdStudioCpuFlow.Return;
				instruction.Text = "$" + code[offset].ToString("X2", CultureInfo.InvariantCulture);
				return true;
			}
			if(offset + info.Length > code.Length) {
				return false;
			}

			int b1 = info.Length >= 2 ? code[offset + 1] : 0;
			int b2 = info.Length >= 3 ? code[offset + 2] : 0;
			int abs = b1 | (b2 << 8);
			instruction.Opcode = code[offset];
			instruction.Mnemonic = info.Mnemonic;
			instruction.Mode = info.Mode;
			instruction.Flow = info.Flow;
			instruction.Length = info.Length;

			switch(info.Mode) {
				case HdStudioAddressMode.Immediate:
					instruction.Immediate = b1;
					instruction.Text = info.Mnemonic + " #$" + b1.ToString("X2", CultureInfo.InvariantCulture);
					break;
				case HdStudioAddressMode.Relative:
					instruction.BranchTarget = (cpuAddress + 2 + (sbyte)b1) & 0xFFFF;
					instruction.Text = info.Mnemonic + " $" + instruction.BranchTarget.Value.ToString("X4", CultureInfo.InvariantCulture);
					break;
				case HdStudioAddressMode.ZeroPage:
					instruction.AbsoluteAddress = b1;
					instruction.Text = info.Mnemonic + " $" + b1.ToString("X2", CultureInfo.InvariantCulture);
					break;
				case HdStudioAddressMode.ZeroPageX:
					instruction.AbsoluteAddress = b1;
					instruction.Text = info.Mnemonic + " $" + b1.ToString("X2", CultureInfo.InvariantCulture) + ",X";
					break;
				case HdStudioAddressMode.ZeroPageY:
					instruction.AbsoluteAddress = b1;
					instruction.Text = info.Mnemonic + " $" + b1.ToString("X2", CultureInfo.InvariantCulture) + ",Y";
					break;
				case HdStudioAddressMode.Absolute:
					instruction.AbsoluteAddress = abs;
					instruction.Text = info.Mnemonic + " $" + abs.ToString("X4", CultureInfo.InvariantCulture);
					break;
				case HdStudioAddressMode.AbsoluteX:
					instruction.AbsoluteAddress = abs;
					instruction.Text = info.Mnemonic + " $" + abs.ToString("X4", CultureInfo.InvariantCulture) + ",X";
					break;
				case HdStudioAddressMode.AbsoluteY:
					instruction.AbsoluteAddress = abs;
					instruction.Text = info.Mnemonic + " $" + abs.ToString("X4", CultureInfo.InvariantCulture) + ",Y";
					break;
				case HdStudioAddressMode.Indirect:
					instruction.IndirectPointer = abs;
					instruction.Text = info.Mnemonic + " ($" + abs.ToString("X4", CultureInfo.InvariantCulture) + ")";
					break;
				case HdStudioAddressMode.IndexedIndirect:
					instruction.AbsoluteAddress = b1;
					instruction.Text = info.Mnemonic + " ($" + b1.ToString("X2", CultureInfo.InvariantCulture) + ",X)";
					break;
				case HdStudioAddressMode.IndirectIndexed:
					instruction.AbsoluteAddress = b1;
					instruction.Text = info.Mnemonic + " ($" + b1.ToString("X2", CultureInfo.InvariantCulture) + "),Y";
					break;
				case HdStudioAddressMode.Accumulator:
					instruction.Text = info.Mnemonic + " A";
					break;
				default:
					instruction.Text = info.Mnemonic;
					break;
			}
			return true;
		}

		private static HdStudioOpcode[] Build()
		{
			HdStudioOpcode[] table = new HdStudioOpcode[256];
			for(int i = 0; i < table.Length; i++) {
				table[i] = new HdStudioOpcode() {
					Code = (byte)i,
					Mnemonic = "???",
					Mode = HdStudioAddressMode.Implied,
					Flow = HdStudioCpuFlow.Return,
					Length = 1,
					Documented = false
				};
			}

			void Add(byte code, string mnemonic, HdStudioAddressMode mode, HdStudioCpuFlow flow = HdStudioCpuFlow.Next)
			{
				int length = mode switch {
					HdStudioAddressMode.Implied or HdStudioAddressMode.Accumulator => 1,
					HdStudioAddressMode.Absolute or HdStudioAddressMode.AbsoluteX or HdStudioAddressMode.AbsoluteY or HdStudioAddressMode.Indirect => 3,
					_ => 2
				};
				table[code] = new HdStudioOpcode() {
					Code = code,
					Mnemonic = mnemonic,
					Mode = mode,
					Flow = flow,
					Length = length,
					Documented = true
				};
			}

			//Row 0x00-0x0F
			Add(0x00, "BRK", HdStudioAddressMode.Implied, HdStudioCpuFlow.Interrupt);
			Add(0x01, "ORA", HdStudioAddressMode.IndexedIndirect);
			Add(0x05, "ORA", HdStudioAddressMode.ZeroPage);
			Add(0x06, "ASL", HdStudioAddressMode.ZeroPage);
			Add(0x08, "PHP", HdStudioAddressMode.Implied);
			Add(0x09, "ORA", HdStudioAddressMode.Immediate);
			Add(0x0A, "ASL", HdStudioAddressMode.Accumulator);
			Add(0x0D, "ORA", HdStudioAddressMode.Absolute);
			Add(0x0E, "ASL", HdStudioAddressMode.Absolute);

			//Row 0x10-0x1F
			Add(0x10, "BPL", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0x11, "ORA", HdStudioAddressMode.IndirectIndexed);
			Add(0x15, "ORA", HdStudioAddressMode.ZeroPageX);
			Add(0x16, "ASL", HdStudioAddressMode.ZeroPageX);
			Add(0x18, "CLC", HdStudioAddressMode.Implied);
			Add(0x19, "ORA", HdStudioAddressMode.AbsoluteY);
			Add(0x1D, "ORA", HdStudioAddressMode.AbsoluteX);
			Add(0x1E, "ASL", HdStudioAddressMode.AbsoluteX);

			//Row 0x20-0x2F
			Add(0x20, "JSR", HdStudioAddressMode.Absolute, HdStudioCpuFlow.Call);
			Add(0x21, "AND", HdStudioAddressMode.IndexedIndirect);
			Add(0x24, "BIT", HdStudioAddressMode.ZeroPage);
			Add(0x25, "AND", HdStudioAddressMode.ZeroPage);
			Add(0x26, "ROL", HdStudioAddressMode.ZeroPage);
			Add(0x28, "PLP", HdStudioAddressMode.Implied);
			Add(0x29, "AND", HdStudioAddressMode.Immediate);
			Add(0x2A, "ROL", HdStudioAddressMode.Accumulator);
			Add(0x2C, "BIT", HdStudioAddressMode.Absolute);
			Add(0x2D, "AND", HdStudioAddressMode.Absolute);
			Add(0x2E, "ROL", HdStudioAddressMode.Absolute);

			//Row 0x30-0x3F
			Add(0x30, "BMI", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0x31, "AND", HdStudioAddressMode.IndirectIndexed);
			Add(0x35, "AND", HdStudioAddressMode.ZeroPageX);
			Add(0x36, "ROL", HdStudioAddressMode.ZeroPageX);
			Add(0x38, "SEC", HdStudioAddressMode.Implied);
			Add(0x39, "AND", HdStudioAddressMode.AbsoluteY);
			Add(0x3D, "AND", HdStudioAddressMode.AbsoluteX);
			Add(0x3E, "ROL", HdStudioAddressMode.AbsoluteX);

			//Row 0x40-0x4F
			Add(0x40, "RTI", HdStudioAddressMode.Implied, HdStudioCpuFlow.Return);
			Add(0x41, "EOR", HdStudioAddressMode.IndexedIndirect);
			Add(0x45, "EOR", HdStudioAddressMode.ZeroPage);
			Add(0x46, "LSR", HdStudioAddressMode.ZeroPage);
			Add(0x48, "PHA", HdStudioAddressMode.Implied);
			Add(0x49, "EOR", HdStudioAddressMode.Immediate);
			Add(0x4A, "LSR", HdStudioAddressMode.Accumulator);
			Add(0x4C, "JMP", HdStudioAddressMode.Absolute, HdStudioCpuFlow.Jump);
			Add(0x4D, "EOR", HdStudioAddressMode.Absolute);
			Add(0x4E, "LSR", HdStudioAddressMode.Absolute);

			//Row 0x50-0x5F
			Add(0x50, "BVC", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0x51, "EOR", HdStudioAddressMode.IndirectIndexed);
			Add(0x55, "EOR", HdStudioAddressMode.ZeroPageX);
			Add(0x56, "LSR", HdStudioAddressMode.ZeroPageX);
			Add(0x58, "CLI", HdStudioAddressMode.Implied);
			Add(0x59, "EOR", HdStudioAddressMode.AbsoluteY);
			Add(0x5D, "EOR", HdStudioAddressMode.AbsoluteX);
			Add(0x5E, "LSR", HdStudioAddressMode.AbsoluteX);

			//Row 0x60-0x6F
			Add(0x60, "RTS", HdStudioAddressMode.Implied, HdStudioCpuFlow.Return);
			Add(0x61, "ADC", HdStudioAddressMode.IndexedIndirect);
			Add(0x65, "ADC", HdStudioAddressMode.ZeroPage);
			Add(0x66, "ROR", HdStudioAddressMode.ZeroPage);
			Add(0x68, "PLA", HdStudioAddressMode.Implied);
			Add(0x69, "ADC", HdStudioAddressMode.Immediate);
			Add(0x6A, "ROR", HdStudioAddressMode.Accumulator);
			Add(0x6C, "JMP", HdStudioAddressMode.Indirect, HdStudioCpuFlow.JumpIndirect);
			Add(0x6D, "ADC", HdStudioAddressMode.Absolute);
			Add(0x6E, "ROR", HdStudioAddressMode.Absolute);

			//Row 0x70-0x7F
			Add(0x70, "BVS", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0x71, "ADC", HdStudioAddressMode.IndirectIndexed);
			Add(0x75, "ADC", HdStudioAddressMode.ZeroPageX);
			Add(0x76, "ROR", HdStudioAddressMode.ZeroPageX);
			Add(0x78, "SEI", HdStudioAddressMode.Implied);
			Add(0x79, "ADC", HdStudioAddressMode.AbsoluteY);
			Add(0x7D, "ADC", HdStudioAddressMode.AbsoluteX);
			Add(0x7E, "ROR", HdStudioAddressMode.AbsoluteX);

			//Row 0x80-0x8F
			Add(0x81, "STA", HdStudioAddressMode.IndexedIndirect);
			Add(0x84, "STY", HdStudioAddressMode.ZeroPage);
			Add(0x85, "STA", HdStudioAddressMode.ZeroPage);
			Add(0x86, "STX", HdStudioAddressMode.ZeroPage);
			Add(0x88, "DEY", HdStudioAddressMode.Implied);
			Add(0x8A, "TXA", HdStudioAddressMode.Implied);
			Add(0x8C, "STY", HdStudioAddressMode.Absolute);
			Add(0x8D, "STA", HdStudioAddressMode.Absolute);
			Add(0x8E, "STX", HdStudioAddressMode.Absolute);

			//Row 0x90-0x9F
			Add(0x90, "BCC", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0x91, "STA", HdStudioAddressMode.IndirectIndexed);
			Add(0x94, "STY", HdStudioAddressMode.ZeroPageX);
			Add(0x95, "STA", HdStudioAddressMode.ZeroPageX);
			Add(0x96, "STX", HdStudioAddressMode.ZeroPageY);
			Add(0x98, "TYA", HdStudioAddressMode.Implied);
			Add(0x99, "STA", HdStudioAddressMode.AbsoluteY);
			Add(0x9A, "TXS", HdStudioAddressMode.Implied);
			Add(0x9D, "STA", HdStudioAddressMode.AbsoluteX);

			//Row 0xA0-0xAF
			Add(0xA0, "LDY", HdStudioAddressMode.Immediate);
			Add(0xA1, "LDA", HdStudioAddressMode.IndexedIndirect);
			Add(0xA2, "LDX", HdStudioAddressMode.Immediate);
			Add(0xA4, "LDY", HdStudioAddressMode.ZeroPage);
			Add(0xA5, "LDA", HdStudioAddressMode.ZeroPage);
			Add(0xA6, "LDX", HdStudioAddressMode.ZeroPage);
			Add(0xA8, "TAY", HdStudioAddressMode.Implied);
			Add(0xA9, "LDA", HdStudioAddressMode.Immediate);
			Add(0xAA, "TAX", HdStudioAddressMode.Implied);
			Add(0xAC, "LDY", HdStudioAddressMode.Absolute);
			Add(0xAD, "LDA", HdStudioAddressMode.Absolute);
			Add(0xAE, "LDX", HdStudioAddressMode.Absolute);

			//Row 0xB0-0xBF
			Add(0xB0, "BCS", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0xB1, "LDA", HdStudioAddressMode.IndirectIndexed);
			Add(0xB4, "LDY", HdStudioAddressMode.ZeroPageX);
			Add(0xB5, "LDA", HdStudioAddressMode.ZeroPageX);
			Add(0xB6, "LDX", HdStudioAddressMode.ZeroPageY);
			Add(0xB8, "CLV", HdStudioAddressMode.Implied);
			Add(0xB9, "LDA", HdStudioAddressMode.AbsoluteY);
			Add(0xBA, "TSX", HdStudioAddressMode.Implied);
			Add(0xBC, "LDY", HdStudioAddressMode.AbsoluteX);
			Add(0xBD, "LDA", HdStudioAddressMode.AbsoluteX);
			Add(0xBE, "LDX", HdStudioAddressMode.AbsoluteY);

			//Row 0xC0-0xCF
			Add(0xC0, "CPY", HdStudioAddressMode.Immediate);
			Add(0xC1, "CMP", HdStudioAddressMode.IndexedIndirect);
			Add(0xC4, "CPY", HdStudioAddressMode.ZeroPage);
			Add(0xC5, "CMP", HdStudioAddressMode.ZeroPage);
			Add(0xC6, "DEC", HdStudioAddressMode.ZeroPage);
			Add(0xC8, "INY", HdStudioAddressMode.Implied);
			Add(0xC9, "CMP", HdStudioAddressMode.Immediate);
			Add(0xCA, "DEX", HdStudioAddressMode.Implied);
			Add(0xCC, "CPY", HdStudioAddressMode.Absolute);
			Add(0xCD, "CMP", HdStudioAddressMode.Absolute);
			Add(0xCE, "DEC", HdStudioAddressMode.Absolute);

			//Row 0xD0-0xDF
			Add(0xD0, "BNE", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0xD1, "CMP", HdStudioAddressMode.IndirectIndexed);
			Add(0xD5, "CMP", HdStudioAddressMode.ZeroPageX);
			Add(0xD6, "DEC", HdStudioAddressMode.ZeroPageX);
			Add(0xD8, "CLD", HdStudioAddressMode.Implied);
			Add(0xD9, "CMP", HdStudioAddressMode.AbsoluteY);
			Add(0xDD, "CMP", HdStudioAddressMode.AbsoluteX);
			Add(0xDE, "DEC", HdStudioAddressMode.AbsoluteX);

			//Row 0xE0-0xEF
			Add(0xE0, "CPX", HdStudioAddressMode.Immediate);
			Add(0xE1, "SBC", HdStudioAddressMode.IndexedIndirect);
			Add(0xE4, "CPX", HdStudioAddressMode.ZeroPage);
			Add(0xE5, "SBC", HdStudioAddressMode.ZeroPage);
			Add(0xE6, "INC", HdStudioAddressMode.ZeroPage);
			Add(0xE8, "INX", HdStudioAddressMode.Implied);
			Add(0xE9, "SBC", HdStudioAddressMode.Immediate);
			Add(0xEA, "NOP", HdStudioAddressMode.Implied);
			Add(0xEC, "CPX", HdStudioAddressMode.Absolute);
			Add(0xED, "SBC", HdStudioAddressMode.Absolute);
			Add(0xEE, "INC", HdStudioAddressMode.Absolute);

			//Row 0xF0-0xFF
			Add(0xF0, "BEQ", HdStudioAddressMode.Relative, HdStudioCpuFlow.Branch);
			Add(0xF1, "SBC", HdStudioAddressMode.IndirectIndexed);
			Add(0xF5, "SBC", HdStudioAddressMode.ZeroPageX);
			Add(0xF6, "INC", HdStudioAddressMode.ZeroPageX);
			Add(0xF8, "SED", HdStudioAddressMode.Implied);
			Add(0xF9, "SBC", HdStudioAddressMode.AbsoluteY);
			Add(0xFD, "SBC", HdStudioAddressMode.AbsoluteX);
			Add(0xFE, "INC", HdStudioAddressMode.AbsoluteX);

			return table;
		}
	}
}

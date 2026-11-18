using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Mesen.HdStudio
{
	/// <summary>
	/// Minimal PNG encoder (32-bit RGBA) so HD Studio can write images without any UI framework.
	/// </summary>
	internal static class HdStudioPng
	{
		private static readonly uint[] _crcTable = BuildCrcTable();

		/// <param name="argb">Pixels in 0xAARRGGBB format, row by row</param>
		public static void Write(string path, uint[] argb, int width, int height)
		{
			using MemoryStream raw = new();
			for(int y = 0; y < height; y++) {
				raw.WriteByte(0); //Filter type: none
				for(int x = 0; x < width; x++) {
					uint c = argb[y * width + x];
					raw.WriteByte((byte)(c >> 16));
					raw.WriteByte((byte)(c >> 8));
					raw.WriteByte((byte)c);
					raw.WriteByte((byte)(c >> 24));
				}
			}

			using MemoryStream compressed = new();
			using(ZLibStream zlib = new(compressed, CompressionLevel.Optimal, true)) {
				raw.Position = 0;
				raw.CopyTo(zlib);
			}

			using FileStream file = File.Create(path);
			file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

			byte[] header = new byte[13];
			WriteUInt32(header, 0, (uint)width);
			WriteUInt32(header, 4, (uint)height);
			header[8] = 8; //Bit depth
			header[9] = 6; //Color type: RGBA
			WriteChunk(file, "IHDR", header);
			WriteChunk(file, "IDAT", compressed.ToArray());
			WriteChunk(file, "IEND", Array.Empty<byte>());
		}

		/// <summary>Nearest-neighbor upscale (keeps the pixel art crisp)</summary>
		public static uint[] Upscale(uint[] argb, int width, int height, int scale)
		{
			uint[] result = new uint[width * scale * height * scale];
			int newWidth = width * scale;
			for(int y = 0; y < height * scale; y++) {
				for(int x = 0; x < newWidth; x++) {
					result[y * newWidth + x] = argb[(y / scale) * width + (x / scale)];
				}
			}
			return result;
		}

		private static void WriteChunk(Stream stream, string type, byte[] data)
		{
			byte[] length = new byte[4];
			WriteUInt32(length, 0, (uint)data.Length);
			stream.Write(length);

			byte[] typeBytes = Encoding.ASCII.GetBytes(type);
			stream.Write(typeBytes);
			stream.Write(data);

			uint crc = 0xFFFFFFFF;
			crc = UpdateCrc(crc, typeBytes);
			crc = UpdateCrc(crc, data);
			byte[] crcBytes = new byte[4];
			WriteUInt32(crcBytes, 0, crc ^ 0xFFFFFFFF);
			stream.Write(crcBytes);
		}

		private static void WriteUInt32(byte[] buffer, int offset, uint value)
		{
			buffer[offset] = (byte)(value >> 24);
			buffer[offset + 1] = (byte)(value >> 16);
			buffer[offset + 2] = (byte)(value >> 8);
			buffer[offset + 3] = (byte)value;
		}

		private static uint UpdateCrc(uint crc, byte[] data)
		{
			foreach(byte b in data) {
				crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
			}
			return crc;
		}

		private static uint[] BuildCrcTable()
		{
			uint[] table = new uint[256];
			for(uint n = 0; n < 256; n++) {
				uint c = n;
				for(int k = 0; k < 8; k++) {
					c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
				}
				table[n] = c;
			}
			return table;
		}
	}
}

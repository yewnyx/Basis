using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup.Tests.Validation
{
    /// <summary>
    /// Builds real PNGs in code (stored-deflate zlib with Adler-32 and per-chunk CRC), so they decode in Unity too.
    /// No binary fixtures are committed: glTFast would import a committed .glb as an asset.
    /// </summary>
    internal static class BasisGlbTestPng
    {
        internal struct Chunk
        {
            public string Type;
            public byte[] Data;

            public Chunk(string type, byte[] data)
            {
                Type = type;
                Data = data;
            }
        }

        internal static byte[] Create(int width, int height, int colorType = 6, int bitDepth = 8, params Chunk[] beforeIdat)
        {
            return Create(width, height, colorType, bitDepth, beforeIdat, null);
        }

        internal static byte[] Create(int width, int height, int colorType, int bitDepth, Chunk[] beforeIdat, Chunk[] afterIdat)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                WriteBigEndian(ihdr, 0, (uint)width);
                WriteBigEndian(ihdr, 4, (uint)height);
                ihdr[8] = (byte)bitDepth;
                ihdr[9] = (byte)colorType;
                WriteChunk(stream, "IHDR", ihdr);
                if (colorType == 3) WriteChunk(stream, "PLTE", new byte[] { 255, 0, 0, 0, 255, 0 });
                if (beforeIdat != null)
                {
                    foreach (Chunk chunk in beforeIdat) WriteChunk(stream, chunk.Type, chunk.Data);
                }
                WriteChunk(stream, "IDAT", Zlib(RawScanlines(width, height, colorType, bitDepth)));
                if (afterIdat != null)
                {
                    foreach (Chunk chunk in afterIdat) WriteChunk(stream, chunk.Type, chunk.Data);
                }
                WriteChunk(stream, "IEND", new byte[0]);
                return stream.ToArray();
            }
        }

        /// <summary>A PNG split into raw chunks (signature excluded), for structural mutation tests.</summary>
        internal static List<Chunk> Split(byte[] png)
        {
            var chunks = new List<Chunk>();
            int pos = 8;
            while (pos < png.Length)
            {
                int length = (int)ReadBigEndian(png, pos);
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                var data = new byte[length];
                Buffer.BlockCopy(png, pos + 8, data, 0, length);
                chunks.Add(new Chunk(type, data));
                pos += length + 12;
            }
            return chunks;
        }

        internal static byte[] Join(List<Chunk> chunks)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                foreach (Chunk chunk in chunks) WriteChunk(stream, chunk.Type, chunk.Data);
                return stream.ToArray();
            }
        }

        /// <summary>SOI + APP0 + SOF0 + EOI: enough for signature and header checks, not a decodable image.</summary>
        internal static byte[] CreateJpegHeader(int width, int height)
        {
            return new byte[]
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
                0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
                0xFF, 0xD9,
            };
        }

        internal static byte[] Gif()
        {
            return Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00\x00\x00\x00;");
        }

        internal static byte[] Webp()
        {
            return Encoding.ASCII.GetBytes("RIFF\x10\x00\x00\x00WEBPVP8 \x04\x00\x00\x00\x00\x00\x00\x00");
        }

        private static byte[] RawScanlines(int width, int height, int colorType, int bitDepth)
        {
            int channels = colorType == 6 ? 4 : colorType == 2 ? 3 : colorType == 4 ? 2 : 1;
            long bitsPerRow = (long)width * channels * bitDepth;
            int rowBytes = (int)((bitsPerRow + 7) / 8);
            var raw = new byte[(long)(rowBytes + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * (rowBytes + 1);
                raw[row] = 0;
                for (int x = 0; x < rowBytes; x++) raw[row + 1 + x] = (byte)((x * 31 + y * 17) & 0xFF);
            }
            return raw;
        }

        private static byte[] Zlib(byte[] data)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x78);
                stream.WriteByte(0x01);
                int pos = 0;
                do
                {
                    int block = Math.Min(65535, data.Length - pos);
                    bool final = pos + block >= data.Length;
                    stream.WriteByte((byte)(final ? 1 : 0));
                    stream.WriteByte((byte)block);
                    stream.WriteByte((byte)(block >> 8));
                    stream.WriteByte((byte)~block);
                    stream.WriteByte((byte)(~block >> 8));
                    stream.Write(data, pos, block);
                    pos += block;
                }
                while (pos < data.Length);
                uint a = 1, b = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    a = (a + data[i]) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                stream.WriteByte((byte)(adler >> 24));
                stream.WriteByte((byte)(adler >> 16));
                stream.WriteByte((byte)(adler >> 8));
                stream.WriteByte((byte)adler);
                return stream.ToArray();
            }
        }

        internal static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[8];
            WriteBigEndian(header, 0, (uint)data.Length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            Buffer.BlockCopy(typeBytes, 0, header, 4, 4);
            stream.Write(header, 0, 8);
            stream.Write(data, 0, data.Length);
            var crcInput = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
            Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
            var crc = new byte[4];
            WriteBigEndian(crc, 0, BasisGlbPng.Crc32(crcInput));
            stream.Write(crc, 0, 4);
        }

        internal static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        internal static uint ReadBigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
        }
    }
}

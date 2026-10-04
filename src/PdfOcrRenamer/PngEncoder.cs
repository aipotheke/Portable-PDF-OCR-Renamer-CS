using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PdfOcrRenamer
{
    public static class PngEncoder
    {
        public static byte[] EncodeBgraAsPng(byte[] bgra, int width, int height)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteInt32BigEndian(ihdr, 0, width);
                WriteInt32BigEndian(ihdr, 4, height);
                ihdr[8] = 8;
                ihdr[9] = 6;
                ihdr[10] = 0;
                ihdr[11] = 0;
                ihdr[12] = 0;
                WriteChunk(ms, "IHDR", ihdr);

                var stride = width * 4;
                var raw = new byte[(stride + 1) * height];
                for (var y = 0; y < height; y++)
                {
                    raw[y * (stride + 1)] = 0;
                    Buffer.BlockCopy(bgra, y * stride, raw, y * (stride + 1) + 1, stride);
                    var rowStart = y * (stride + 1) + 1;
                    for (var x = 0; x < stride; x += 4)
                    {
                        var b = raw[rowStart + x];
                        raw[rowStart + x] = raw[rowStart + x + 2];
                        raw[rowStart + x + 2] = b;
                    }
                }

                byte[] withHeader;
                using (var zms = new MemoryStream())
                {
                    zms.WriteByte(0x78);
                    zms.WriteByte(0x9C);
                    using (var deflate = new DeflateStream(zms, CompressionLevel.Optimal, true))
                    {
                        deflate.Write(raw, 0, raw.Length);
                    }
                    WriteUInt32BigEndianTail(zms, Adler32(raw));
                    withHeader = zms.ToArray();
                }
                WriteChunk(ms, "IDAT", withHeader);
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void WriteInt32BigEndian(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BigEndianTail(Stream s, uint value)
        {
            s.WriteByte((byte)(value >> 24));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)value);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var d in data)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteInt32BigEndian(len, 0, data.Length);
            s.Write(len, 0, 4);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, typeBytes.Length);
            s.Write(data, 0, data.Length);
            var crcInput = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
            var crc = Crc32(crcInput);
            var crcBytes = new byte[4];
            WriteUInt32BigEndian(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc >> 1) ^ (0xEDB88320 & (0 - (crc & 1)));
            }
            return crc ^ 0xFFFFFFFF;
        }
    }
}

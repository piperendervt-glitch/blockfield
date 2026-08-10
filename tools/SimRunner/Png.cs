using System;
using System.IO;
using System.IO.Compression;

namespace SimRunner
{
    /// <summary>
    /// 最小限の PNG エンコーダ。
    ///
    /// 【なぜ自前で書くか】
    /// - System.Drawing は Windows 専用で .NET 6 以降は非推奨。将来 Linux の
    ///   リモートマシンで回す可能性がある（docs/remote_work.md）
    /// - ImageSharp 等の NuGet を足すと、このツールを動かすのに
    ///   パッケージ復元が要る。リモートで回線が細いときに詰まる
    /// PNG の必要最小限（RGB8・フィルタなし・zlib 圧縮）は50行程度で書ける。
    /// </summary>
    public static class Png
    {
        /// <summary>RGB のバイト列（w*h*3）を PNG として書き出す。</summary>
        public static void Write(string path, int width, int height, byte[] rgb)
        {
            using var fs = File.Create(path);
            fs.Write(Encode(width, height, rgb));
        }

        public static byte[] Encode(int width, int height, byte[] rgb)
        {
            if (rgb.Length != width * height * 3)
            {
                throw new ArgumentException($"rgb の長さが {width}x{height}x3 と合わない");
            }

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // シグネチャ

            // IHDR: 幅・高さ・ビット深度8・カラータイプ2(RGB)・圧縮0・フィルタ0・インタレース0
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 2;
            WriteChunk(ms, "IHDR", ihdr);

            // 各行の先頭にフィルタ種別(0=None)を付ける
            var raw = new byte[height * (1 + width * 3)];
            for (int y = 0; y < height; y++)
            {
                int src = y * width * 3;
                int dst = y * (1 + width * 3);
                raw[dst] = 0;
                Buffer.BlockCopy(rgb, src, raw, dst + 1, width * 3);
            }

            WriteChunk(ms, "IDAT", ZlibCompress(raw));
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        /// <summary>
        /// zlib ストリーム = 2バイトヘッダ + raw deflate + Adler-32。
        /// DeflateStream は raw deflate しか吐かないので、包みは自分で付ける。
        /// </summary>
        static byte[] ZlibCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); // CM=8 (deflate), CINFO=7 (32KB窓)
            ms.WriteByte(0x9C); // FLEVEL=2, FCHECK が (0x78*256+0x9C) % 31 == 0 を満たす
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                ds.Write(data, 0, data.Length);
            }
            WriteBE32(ms, Adler32(data));
            return ms.ToArray();
        }

        static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            foreach (byte t in data)
            {
                a = (a + t) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                typeBytes[i] = (byte)type[i];
            }

            var lenBuf = new byte[4];
            WriteBE(lenBuf, 0, data.Length);
            s.Write(lenBuf);
            s.Write(typeBytes);
            s.Write(data);

            var crcInput = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
            Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
            WriteBE32(s, Crc32(crcInput));
        }

        static readonly uint[] s_CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte t in data)
            {
                c = s_CrcTable[(c ^ t) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }

        static void WriteBE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        static void WriteBE32(Stream s, uint value)
        {
            s.WriteByte((byte)(value >> 24));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)value);
        }
    }
}

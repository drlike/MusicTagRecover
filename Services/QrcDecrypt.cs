using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MusicTagRecover.Services;

/// <summary>
/// QRC歌词解密工具 - 3DES解密 + zlib解压
/// </summary>
internal static class QrcDecrypt
{
    private static readonly byte[] QRC_KEY = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");
    private static readonly byte[] QRC_MAGIC = { 0x98, 0x25, 0xB0, 0xAC, 0xE3, 0x02, 0x83, 0x68, 0xE8, 0xFC, 0x6C };

    private static readonly int[][] SBOX = {
        new[] {14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13},
        new[] {15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9},
        new[] {10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12},
        new[] {7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14},
        new[] {2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3},
        new[] {12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13},
        new[] {4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12},
        new[] {13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11}
    };

    private static int Bitnum(byte[] a, int b, int c)
    {
        return (((a[(b / 32) * 4 + 3 - (b % 32) / 8]) & 0xFF) >> (7 - b % 8) & 1) << c;
    }

    private static int BitnumIntr(int a, int b, int c)
    {
        return ((a >> (31 - b)) & 1) << c;
    }

    private static int BitnumIntl(int a, int b, int c)
    {
        return (int)(((long)(a << b) & 0x80000000L) >> c) & unchecked((int)0xFFFFFFFF);
    }

    private static int SboxBit(int a)
    {
        return (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4);
    }

    private static (int s0, int s1) InitialPermutation(byte[] input)
    {
        int s0 = Bitnum(input, 57, 31) | Bitnum(input, 49, 30) | Bitnum(input, 41, 29) | Bitnum(input, 33, 28) |
                 Bitnum(input, 25, 27) | Bitnum(input, 17, 26) | Bitnum(input, 9, 25) | Bitnum(input, 1, 24) |
                 Bitnum(input, 59, 23) | Bitnum(input, 51, 22) | Bitnum(input, 43, 21) | Bitnum(input, 35, 20) |
                 Bitnum(input, 27, 19) | Bitnum(input, 19, 18) | Bitnum(input, 11, 17) | Bitnum(input, 3, 16) |
                 Bitnum(input, 61, 15) | Bitnum(input, 53, 14) | Bitnum(input, 45, 13) | Bitnum(input, 37, 12) |
                 Bitnum(input, 29, 11) | Bitnum(input, 21, 10) | Bitnum(input, 13, 9) | Bitnum(input, 5, 8) |
                 Bitnum(input, 63, 7) | Bitnum(input, 55, 6) | Bitnum(input, 47, 5) | Bitnum(input, 39, 4) |
                 Bitnum(input, 31, 3) | Bitnum(input, 23, 2) | Bitnum(input, 15, 1) | Bitnum(input, 7, 0);

        int s1 = Bitnum(input, 56, 31) | Bitnum(input, 48, 30) | Bitnum(input, 40, 29) | Bitnum(input, 32, 28) |
                 Bitnum(input, 24, 27) | Bitnum(input, 16, 26) | Bitnum(input, 8, 25) | Bitnum(input, 0, 24) |
                 Bitnum(input, 58, 23) | Bitnum(input, 50, 22) | Bitnum(input, 42, 21) | Bitnum(input, 34, 20) |
                 Bitnum(input, 26, 19) | Bitnum(input, 18, 18) | Bitnum(input, 10, 17) | Bitnum(input, 2, 16) |
                 Bitnum(input, 60, 15) | Bitnum(input, 52, 14) | Bitnum(input, 44, 13) | Bitnum(input, 36, 12) |
                 Bitnum(input, 28, 11) | Bitnum(input, 20, 10) | Bitnum(input, 12, 9) | Bitnum(input, 4, 8) |
                 Bitnum(input, 62, 7) | Bitnum(input, 54, 6) | Bitnum(input, 46, 5) | Bitnum(input, 38, 4) |
                 Bitnum(input, 30, 3) | Bitnum(input, 22, 2) | Bitnum(input, 14, 1) | Bitnum(input, 6, 0);

        return (s0, s1);
    }

    private static byte[] InversePermutation(int s0, int s1)
    {
        var data = new byte[8];
        data[3] = (byte)(BitnumIntr(s1, 7, 7) | BitnumIntr(s0, 7, 6) | BitnumIntr(s1, 15, 5) | BitnumIntr(s0, 15, 4) |
                         BitnumIntr(s1, 23, 3) | BitnumIntr(s0, 23, 2) | BitnumIntr(s1, 31, 1) | BitnumIntr(s0, 31, 0));
        data[2] = (byte)(BitnumIntr(s1, 6, 7) | BitnumIntr(s0, 6, 6) | BitnumIntr(s1, 14, 5) | BitnumIntr(s0, 14, 4) |
                         BitnumIntr(s1, 22, 3) | BitnumIntr(s0, 22, 2) | BitnumIntr(s1, 30, 1) | BitnumIntr(s0, 30, 0));
        data[1] = (byte)(BitnumIntr(s1, 5, 7) | BitnumIntr(s0, 5, 6) | BitnumIntr(s1, 13, 5) | BitnumIntr(s0, 13, 4) |
                         BitnumIntr(s1, 21, 3) | BitnumIntr(s0, 21, 2) | BitnumIntr(s1, 29, 1) | BitnumIntr(s0, 29, 0));
        data[0] = (byte)(BitnumIntr(s1, 4, 7) | BitnumIntr(s0, 4, 6) | BitnumIntr(s1, 12, 5) | BitnumIntr(s0, 12, 4) |
                         BitnumIntr(s1, 20, 3) | BitnumIntr(s0, 20, 2) | BitnumIntr(s1, 28, 1) | BitnumIntr(s0, 28, 0));
        data[7] = (byte)(BitnumIntr(s1, 3, 7) | BitnumIntr(s0, 3, 6) | BitnumIntr(s1, 11, 5) | BitnumIntr(s0, 11, 4) |
                         BitnumIntr(s1, 19, 3) | BitnumIntr(s0, 19, 2) | BitnumIntr(s1, 27, 1) | BitnumIntr(s0, 27, 0));
        data[6] = (byte)(BitnumIntr(s1, 2, 7) | BitnumIntr(s0, 2, 6) | BitnumIntr(s1, 10, 5) | BitnumIntr(s0, 10, 4) |
                         BitnumIntr(s1, 18, 3) | BitnumIntr(s0, 18, 2) | BitnumIntr(s1, 26, 1) | BitnumIntr(s0, 26, 0));
        data[5] = (byte)(BitnumIntr(s1, 1, 7) | BitnumIntr(s0, 1, 6) | BitnumIntr(s1, 9, 5) | BitnumIntr(s0, 9, 4) |
                         BitnumIntr(s1, 17, 3) | BitnumIntr(s0, 17, 2) | BitnumIntr(s1, 25, 1) | BitnumIntr(s0, 25, 0));
        data[4] = (byte)(BitnumIntr(s1, 0, 7) | BitnumIntr(s0, 0, 6) | BitnumIntr(s1, 8, 5) | BitnumIntr(s0, 8, 4) |
                         BitnumIntr(s1, 16, 3) | BitnumIntr(s0, 16, 2) | BitnumIntr(s1, 24, 1) | BitnumIntr(s0, 24, 0));
        return data;
    }

    private static int DesF(int state, byte[] key)
    {
        int t1 = unchecked((int)(
            (uint)BitnumIntl(state, 31, 0) |
            (uint)((uint)(state & unchecked((int)0xF0000000)) >> 1) |
            (uint)BitnumIntl(state, 4, 5) |
            (uint)BitnumIntl(state, 3, 6) |
            (uint)((uint)(state & 0x0F000000) >> 3) |
            (uint)BitnumIntl(state, 8, 11) |
            (uint)BitnumIntl(state, 7, 12) |
            (uint)((uint)(state & 0x00F00000) >> 5) |
            (uint)BitnumIntl(state, 12, 17) |
            (uint)BitnumIntl(state, 11, 18) |
            (uint)((uint)(state & 0x000F0000) >> 7) |
            (uint)BitnumIntl(state, 16, 23)
        ));

        int t2 = unchecked((int)(
            (uint)BitnumIntl(state, 15, 0) |
            (uint)((uint)(state & 0x0000F000) << 15) |
            (uint)BitnumIntl(state, 20, 5) |
            (uint)BitnumIntl(state, 19, 6) |
            (uint)((uint)(state & 0x00000F00) << 13) |
            (uint)BitnumIntl(state, 24, 11) |
            (uint)BitnumIntl(state, 23, 12) |
            (uint)((uint)(state & 0x000000F0) << 11) |
            (uint)BitnumIntl(state, 28, 17) |
            (uint)BitnumIntl(state, 27, 18) |
            (uint)((uint)(state & 0x0000000F) << 9) |
            (uint)BitnumIntl(state, 0, 23)
        ));

        var lrgstate = new int[6];
        lrgstate[0] = (t1 >> 24) & 0xFF;
        lrgstate[1] = (t1 >> 16) & 0xFF;
        lrgstate[2] = (t1 >> 8) & 0xFF;
        lrgstate[3] = (t2 >> 24) & 0xFF;
        lrgstate[4] = (t2 >> 16) & 0xFF;
        lrgstate[5] = (t2 >> 8) & 0xFF;

        for (int i = 0; i < 6; i++)
            lrgstate[i] ^= key[i];

        int newState = unchecked((int)(
            (uint)(SBOX[0][SboxBit(lrgstate[0] >> 2)] << 28) |
            (uint)(SBOX[1][SboxBit(((lrgstate[0] & 0x03) << 4) | (lrgstate[1] >> 4))] << 24) |
            (uint)(SBOX[2][SboxBit(((lrgstate[1] & 0x0F) << 2) | (lrgstate[2] >> 6))] << 20) |
            (uint)(SBOX[3][SboxBit(lrgstate[2] & 0x3F)] << 16) |
            (uint)(SBOX[4][SboxBit(lrgstate[3] >> 2)] << 12) |
            (uint)(SBOX[5][SboxBit(((lrgstate[3] & 0x03) << 4) | (lrgstate[4] >> 4))] << 8) |
            (uint)(SBOX[6][SboxBit(((lrgstate[4] & 0x0F) << 2) | (lrgstate[5] >> 6))] << 4) |
            (uint)SBOX[7][SboxBit(lrgstate[5] & 0x3F)]
        ));

        return unchecked((int)(
            (uint)BitnumIntl(newState, 15, 0) | (uint)BitnumIntl(newState, 6, 1) |
            (uint)BitnumIntl(newState, 19, 2) | (uint)BitnumIntl(newState, 20, 3) |
            (uint)BitnumIntl(newState, 28, 4) | (uint)BitnumIntl(newState, 11, 5) |
            (uint)BitnumIntl(newState, 27, 6) | (uint)BitnumIntl(newState, 16, 7) |
            (uint)BitnumIntl(newState, 0, 8) | (uint)BitnumIntl(newState, 14, 9) |
            (uint)BitnumIntl(newState, 22, 10) | (uint)BitnumIntl(newState, 25, 11) |
            (uint)BitnumIntl(newState, 4, 12) | (uint)BitnumIntl(newState, 17, 13) |
            (uint)BitnumIntl(newState, 30, 14) | (uint)BitnumIntl(newState, 9, 15) |
            (uint)BitnumIntl(newState, 1, 16) | (uint)BitnumIntl(newState, 7, 17) |
            (uint)BitnumIntl(newState, 23, 18) | (uint)BitnumIntl(newState, 13, 19) |
            (uint)BitnumIntl(newState, 31, 20) | (uint)BitnumIntl(newState, 26, 21) |
            (uint)BitnumIntl(newState, 2, 22) | (uint)BitnumIntl(newState, 8, 23) |
            (uint)BitnumIntl(newState, 18, 24) | (uint)BitnumIntl(newState, 12, 25) |
            (uint)BitnumIntl(newState, 29, 26) | (uint)BitnumIntl(newState, 5, 27) |
            (uint)BitnumIntl(newState, 21, 28) | (uint)BitnumIntl(newState, 10, 29) |
            (uint)BitnumIntl(newState, 3, 30) | (uint)BitnumIntl(newState, 24, 31)
        ));
    }

    private static byte[] DesCrypt(byte[] input, byte[][] key)
    {
        var (s0, s1) = InitialPermutation(input);
        for (int idx = 0; idx < 15; idx++)
        {
            int prevS1 = s1;
            s1 = (int)((uint)DesF(s1, key[idx]) ^ (uint)s0);
            s0 = prevS1;
        }
        s0 = (int)((uint)DesF(s1, key[15]) ^ (uint)s0);
        return InversePermutation(s0, s1);
    }

    private static byte[][] KeySchedule(byte[] key, bool isDecrypt)
    {
        var schedule = new byte[16][];
        for (int i = 0; i < 16; i++) schedule[i] = new byte[6];

        int[] keyRndShift = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };
        int[] keyPermC = { 56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35 };
        int[] keyPermD = { 62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3 };
        int[] keyCompression = { 13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31 };

        int c = 0, d = 0;
        for (int i = 0; i < 28; i++)
        {
            c += Bitnum(key, keyPermC[i], 31 - i);
            d += Bitnum(key, keyPermD[i], 31 - i);
        }

        for (int i = 0; i < 16; i++)
        {
            c = unchecked((int)(((uint)c << keyRndShift[i] | (uint)c >> (28 - keyRndShift[i])) & 0xFFFFFFF0));
            d = unchecked((int)(((uint)d << keyRndShift[i] | (uint)d >> (28 - keyRndShift[i])) & 0xFFFFFFF0));

            int togen = isDecrypt ? (15 - i) : i;

            for (int j = 0; j < 6; j++)
                schedule[togen][j] = 0;

            for (int j = 0; j < 24; j++)
                schedule[togen][j / 8] = (byte)((uint)schedule[togen][j / 8] | (uint)BitnumIntr(c, keyCompression[j], 7 - (j % 8)));

            for (int j = 24; j < 48; j++)
                schedule[togen][j / 8] = (byte)((uint)schedule[togen][j / 8] | (uint)BitnumIntr(d, keyCompression[j] - 27, 7 - (j % 8)));
        }

        return schedule;
    }

    private static byte[][][] TripleDesKeySetup(byte[] key, bool isEncrypt)
    {
        if (isEncrypt)
        {
            return new[] {
                KeySchedule(key.AsSpan(0, 8).ToArray(), false),
                KeySchedule(key.AsSpan(8, 8).ToArray(), true),
                KeySchedule(key.AsSpan(16, 8).ToArray(), false)
            };
        }
        else
        {
            return new[] {
                KeySchedule(key.AsSpan(16, 8).ToArray(), true),
                KeySchedule(key.AsSpan(8, 8).ToArray(), false),
                KeySchedule(key.AsSpan(0, 8).ToArray(), true)
            };
        }
    }

    private static byte[] TripleDesCrypt(byte[] data, byte[][][] key)
    {
        byte[] result = data;
        for (int i = 0; i < 3; i++)
            result = DesCrypt(result, key[i]);
        return result;
    }

    /// <summary>
    /// 解密QRC加密歌词
    /// </summary>
    public static string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            // 兼容 .NET 461 的 hex 解析
            var encryptedBytes = new byte[encrypted.Length / 2];
            for (int k = 0; k < encryptedBytes.Length; k++)
            {
                encryptedBytes[k] = Convert.ToByte(encrypted.Substring(k * 2, 2), 16);
            }
            var schedule = TripleDesKeySetup(QRC_KEY, false);
            var outBytes = new MemoryStream();

            int i = 0;
            while (i < encryptedBytes.Length)
            {
                var block = new byte[8];
                int len = Math.Min(8, encryptedBytes.Length - i);
                Array.Copy(encryptedBytes, i, block, 0, len);
                var dec = TripleDesCrypt(block, schedule);
                outBytes.Write(dec, 0, dec.Length);
                i += 8;
            }

            var decryptedBytes = outBytes.ToArray();

            // 跳过magic header
            if (decryptedBytes.Length >= QRC_MAGIC.Length)
            {
                bool isMagic = true;
                for (int j = 0; j < QRC_MAGIC.Length; j++)
                {
                    if (decryptedBytes[j] != QRC_MAGIC[j]) { isMagic = false; break; }
                }
                if (isMagic)
                {
                    var temp = new byte[decryptedBytes.Length - QRC_MAGIC.Length];
                    Array.Copy(decryptedBytes, QRC_MAGIC.Length, temp, 0, temp.Length);
                    decryptedBytes = temp;
                }
            }

            // zlib解压
            try
            {
                return DecompressZlib(decryptedBytes);
            }
            catch
            {
                try
                {
                    return DecompressDeflate(decryptedBytes);
                }
                catch
                {
                    return null;
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static string DecompressZlib(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        ds.CopyTo(result);
        return Encoding.UTF8.GetString(result.ToArray());
    }

    private static string DecompressDeflate(byte[] data)
    {
        // 跳过zlib header (2 bytes)
        using var ms = new MemoryStream(data, 2, data.Length - 2);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        ds.CopyTo(result);
        return Encoding.UTF8.GetString(result.ToArray());
    }
}

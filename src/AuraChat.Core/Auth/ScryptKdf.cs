using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AuraChat.Core.Auth;

/// <summary>
/// scrypt（RFC 7914）——只为验证旧 Node 版 <c>crypto.scryptSync(pw, salt, 64)</c> 生成的密码哈希，
/// 好让迁移过来的账号用老密码直接登录。新密码仍然用 PBKDF2 存。
/// </summary>
internal static class ScryptKdf
{
    /// <summary>旧版 Node 的默认参数：N=16384, r=8, p=1, dkLen=64。</summary>
    public const int LegacyN = 16384;
    public const int LegacyR = 8;
    public const int LegacyP = 1;
    public const int LegacyKeyLength = 64;

    public static byte[] Hash(byte[] password, byte[] salt, int n, int r, int p, int dkLen)
    {
        if (n <= 1 || (n & (n - 1)) != 0) throw new ArgumentException("N 必须是 2 的幂且大于 1", nameof(n));
        if (r <= 0 || p <= 0) throw new ArgumentException("r / p 必须为正数");

        var blockSize = 128 * r;
        var blocks = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockSize);

        var v = new byte[(long)n * blockSize];
        var x = new byte[blockSize];
        var y = new byte[blockSize];
        var scratch = new byte[blockSize];

        for (var i = 0; i < p; i++)
        {
            var offset = i * blockSize;
            Buffer.BlockCopy(blocks, offset, x, 0, blockSize);

            // 第一轮：把每个 BlockMix 结果存进 V
            for (var j = 0; j < n; j++)
            {
                Buffer.BlockCopy(x, 0, v, j * blockSize, blockSize);
                BlockMix(x, y, scratch, r);
                (x, y) = (y, x);
            }

            // 第二轮：按 Integerify(X) 随机取 V[j] 异或
            for (var j = 0; j < n; j++)
            {
                var index = (long)(Integerify(x, r) % (ulong)n);
                for (var k = 0; k < blockSize; k++) x[k] ^= v[index * blockSize + k];
                BlockMix(x, y, scratch, r);
                (x, y) = (y, x);
            }

            Buffer.BlockCopy(x, 0, blocks, offset, blockSize);
        }

        return Rfc2898DeriveBytes.Pbkdf2(password, blocks, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static ulong Integerify(byte[] block, int r)
    {
        var lastBlock = (2 * r - 1) * 64;
        return BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(lastBlock, 8));
    }

    /// <summary>B' = BlockMix(B)：2r 个 64 字节块，输出顺序是偶数块在前、奇数块在后。</summary>
    private static void BlockMix(byte[] src, byte[] dst, byte[] scratch, int r)
    {
        var t = new byte[64];
        Buffer.BlockCopy(src, (2 * r - 1) * 64, t, 0, 64);
        var limit = 2 * r;
        for (var i = 0; i < limit; i++)
        {
            var srcOffset = i * 64;
            for (var k = 0; k < 64; k++) t[k] ^= src[srcOffset + k];
            Salsa208(t);
            var dstIndex = (i & 1) == 0 ? i / 2 : r + (i - 1) / 2;
            Buffer.BlockCopy(t, 0, dst, dstIndex * 64, 64);
        }
        _ = scratch;
    }

    /// <summary>Salsa20/8 核心：对 64 字节块原地做 8 轮（4 次双轮）。</summary>
    private static void Salsa208(byte[] block)
    {
        Span<uint> x = stackalloc uint[16];
        Span<uint> original = stackalloc uint[16];
        for (var i = 0; i < 16; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4, 4));
            x[i] = value;
            original[i] = value;
        }

        for (var round = 0; round < 4; round++)
        {
            // 列轮
            x[4] ^= Rotl(x[0] + x[12], 7); x[8] ^= Rotl(x[4] + x[0], 9);
            x[12] ^= Rotl(x[8] + x[4], 13); x[0] ^= Rotl(x[12] + x[8], 18);
            x[9] ^= Rotl(x[5] + x[1], 7); x[13] ^= Rotl(x[9] + x[5], 9);
            x[1] ^= Rotl(x[13] + x[9], 13); x[5] ^= Rotl(x[1] + x[13], 18);
            x[14] ^= Rotl(x[10] + x[6], 7); x[2] ^= Rotl(x[14] + x[10], 9);
            x[6] ^= Rotl(x[2] + x[14], 13); x[10] ^= Rotl(x[6] + x[2], 18);
            x[3] ^= Rotl(x[15] + x[11], 7); x[7] ^= Rotl(x[3] + x[15], 9);
            x[11] ^= Rotl(x[7] + x[3], 13); x[15] ^= Rotl(x[11] + x[7], 18);
            // 行轮
            x[1] ^= Rotl(x[0] + x[3], 7); x[2] ^= Rotl(x[1] + x[0], 9);
            x[3] ^= Rotl(x[2] + x[1], 13); x[0] ^= Rotl(x[3] + x[2], 18);
            x[6] ^= Rotl(x[5] + x[4], 7); x[7] ^= Rotl(x[6] + x[5], 9);
            x[4] ^= Rotl(x[7] + x[6], 13); x[5] ^= Rotl(x[4] + x[7], 18);
            x[11] ^= Rotl(x[10] + x[9], 7); x[8] ^= Rotl(x[11] + x[10], 9);
            x[9] ^= Rotl(x[8] + x[11], 13); x[10] ^= Rotl(x[9] + x[8], 18);
            x[12] ^= Rotl(x[15] + x[14], 7); x[13] ^= Rotl(x[12] + x[15], 9);
            x[14] ^= Rotl(x[13] + x[12], 13); x[15] ^= Rotl(x[14] + x[13], 18);
        }

        for (var i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(i * 4, 4), x[i] + original[i]);
    }

    private static uint Rotl(uint value, int shift) => (value << shift) | (value >> (32 - shift));
}

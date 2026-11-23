using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SolidBrew.Core.Hashing
{
    /// <summary>SHA-256 содержимого в виде hex-строки в нижнем регистре.</summary>
    public static class Sha256Hex
    {
        private const int BufferSize = 1024 * 1024;

        /// <summary>Хеш потока от текущей позиции до конца.</summary>
        public static string Compute(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        /// <summary>Хеш файла. Файл читается с <see cref="FileShare.ReadWrite"/> (может быть открыт в CAD).</summary>
        public static async Task<string> ComputeFileAsync(string path, CancellationToken ct = default)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    sha.TransformBlock(buffer, 0, read, null, 0);
                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash!);
            }
        }

        /// <summary>Строка — корректный хеш: ровно 64 символа [0-9a-f].</summary>
        public static bool IsValid(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }
}

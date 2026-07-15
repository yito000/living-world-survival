using System;
using System.Security.Cryptography;

namespace SurvivalWorld.World
{
    public static class DomainEventId
    {
        private const string CrockfordBase32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string NewUlid()
        {
            return NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public static string NewUlid(long unixTimeMilliseconds)
        {
            byte[] bytes = new byte[16];
            bytes[0] = (byte)(unixTimeMilliseconds >> 40);
            bytes[1] = (byte)(unixTimeMilliseconds >> 32);
            bytes[2] = (byte)(unixTimeMilliseconds >> 24);
            bytes[3] = (byte)(unixTimeMilliseconds >> 16);
            bytes[4] = (byte)(unixTimeMilliseconds >> 8);
            bytes[5] = (byte)unixTimeMilliseconds;

            byte[] random = new byte[10];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(random);
            }

            Buffer.BlockCopy(random, 0, bytes, 6, random.Length);
            return EncodeBase32(bytes);
        }

        private static string EncodeBase32(byte[] bytes)
        {
            char[] output = new char[26];
            int outputIndex = 0;
            int buffer = 0;
            int bitsLeft = 0;

            for (int i = 0; i < bytes.Length && outputIndex < output.Length; i++)
            {
                buffer = (buffer << 8) | bytes[i];
                bitsLeft += 8;

                while (bitsLeft >= 5 && outputIndex < output.Length)
                {
                    int index = (buffer >> (bitsLeft - 5)) & 31;
                    output[outputIndex++] = CrockfordBase32[index];
                    bitsLeft -= 5;
                }
            }

            if (bitsLeft > 0 && outputIndex < output.Length)
            {
                int index = (buffer << (5 - bitsLeft)) & 31;
                output[outputIndex++] = CrockfordBase32[index];
            }

            while (outputIndex < output.Length)
            {
                output[outputIndex++] = '0';
            }

            return new string(output);
        }
    }
}

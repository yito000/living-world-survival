using System.Security.Cryptography;
using System.Text;

namespace SurvivalWorld.World
{
    public static class SnapshotChecksum
    {
        public static string ComputeSha256Hex(byte[] payload)
        {
            byte[] input = payload ?? new byte[0];
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(input);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}

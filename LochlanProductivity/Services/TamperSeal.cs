using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LochlanProductivity.Services
{
    // ============================================================
    // TAMPER SEAL
    //
    // Detects hand-edited tasks.json. An HMAC key is generated once,
    // protected with DPAPI (current user), and never leaves the
    // machine. Saves record the HMAC of the exact JSON written; loads
    // verify it.
    //
    // Trust on first use: a missing seal alongside an existing save
    // (first run after upgrading) is accepted and sealed going
    // forward. A seal that EXISTS but does not match means the file
    // was modified outside the app.
    //
    // This is accountability, not cryptography against an attacker
    // with full machine control - such a person can read this source
    // code. It stops casual future-you.
    // ============================================================

    public class TamperSeal
    {
        private readonly string keyPath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "seal.key");

        private byte[]? cachedKey;

        public string Compute(string content)
        {
            using HMACSHA256 hmac =
                new(GetOrCreateKey());

            byte[] hash =
                hmac.ComputeHash(
                    Encoding.UTF8.GetBytes(content));

            return Convert.ToHexString(hash);
        }

        /// null seal = trust on first use (returns true and seals).
        public bool Verify(string content, string? seal)
        {
            string expected =
                Compute(content);

            if (string.IsNullOrWhiteSpace(seal))
                return true; // TOFU

            return string.Equals(
                expected,
                seal.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private byte[] GetOrCreateKey()
        {
            if (cachedKey != null)
                return cachedKey;

            Directory.CreateDirectory(
                Path.GetDirectoryName(keyPath)!);

            if (File.Exists(keyPath))
            {
                try
                {
                    cachedKey =
                        ProtectedData.Unprotect(
                            File.ReadAllBytes(keyPath),
                            null,
                            DataProtectionScope.CurrentUser);

                    if (cachedKey!.Length == 32)
                        return cachedKey;
                }
                catch
                {
                    // Unprotectable (profile moved?) - rotate below.
                }
            }

            byte[] fresh =
                RandomNumberGenerator.GetBytes(32);

            File.WriteAllBytes(
                keyPath,
                ProtectedData.Protect(
                    fresh,
                    null,
                    DataProtectionScope.CurrentUser));

            cachedKey = fresh;

            return fresh;
        }
    }
}

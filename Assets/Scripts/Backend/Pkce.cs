using System;
using System.Security.Cryptography;
using System.Text;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Proof Key for Code Exchange: the thing that makes an OAuth redirect safe on a
    /// machine you do not control.
    ///
    /// ══ WHY A GAME CLIENT CANNOT USE THE ORDINARY FLOW ════════════════════════════
    ///
    /// The classic OAuth flow proves the app is who it says it is with a client SECRET.
    /// A game has nowhere to keep one: a WebGL build is downloadable and a desktop
    /// build is a file on someone's disk. Shipping the secret means every player has
    /// it, which means it is not a secret and the flow proves nothing.
    ///
    /// PKCE replaces the secret with a value invented FRESH for each sign-in. The
    /// client sends a hash of it up front and the value itself at the end, so whoever
    /// redeems the authorisation code has to be whoever started the exchange. Nothing
    /// durable needs hiding, so nothing durable can leak.
    ///
    /// ══ WHY THIS MATTERS PARTICULARLY HERE ════════════════════════════════════════
    ///
    /// In the editor and in standalone builds the redirect lands on a loopback HTTP
    /// server -- and on a shared machine, any other local process could race to grab
    /// that code. Without PKCE, grabbing the code is enough to become the player.
    /// With it, the code is worthless without a verifier that never left this process.
    /// </summary>
    public static class Pkce
    {
        /// <summary>
        /// Bytes of entropy in a verifier.
        ///
        /// 32 bytes base64url-encodes to 43 characters, which is the minimum the spec
        /// allows and comfortably beyond guessing. The maximum is 128; more length
        /// buys nothing once the value is already unguessable.
        /// </summary>
        public const int VerifierBytes = 32;

        /// <summary>
        /// A fresh verifier. Cryptographically random, never reused.
        ///
        /// Reuse would defeat the whole mechanism: the point is that this sign-in's
        /// code can only be redeemed by this sign-in, and a verifier shared between
        /// two attempts makes either one able to complete the other.
        /// </summary>
        public static string NewVerifier()
        {
            byte[] entropy = new byte[VerifierBytes];

            using var random = RandomNumberGenerator.Create();
            random.GetBytes(entropy);

            return Base64Url(entropy);
        }

        /// <summary>
        /// The challenge to send up front: base64url of the SHA-256 of the verifier.
        ///
        /// The S256 method rather than "plain". Plain sends the verifier itself in the
        /// authorisation request, which puts it in the browser's history, the server's
        /// access log and any proxy in between -- and a verifier anybody can read is
        /// no better than no verifier at all.
        /// </summary>
        public static string Challenge(string verifier)
        {
            // The instance API, not SHA256.HashData: that static shortcut is .NET 5+
            // and Unity compiles this against netstandard2.1, where it does not exist.
            using var sha = SHA256.Create();

            byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier ?? ""));

            return Base64Url(hash);
        }

        /// <summary>
        /// Base64, in the alphabet URLs allow.
        ///
        /// Standard base64 uses + / and =, all three of which mean something else in a
        /// URL. Encoding them percent-style would work but the spec calls for this
        /// variant, and a challenge the server hashes differently is a mismatch with
        /// no useful error attached.
        /// </summary>
        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes)
                   .TrimEnd('=')
                   .Replace('+', '-')
                   .Replace('/', '_');
    }
}

using System;
using System.Security.Cryptography;

namespace OpenEsport.Accounts
{
	/// <summary>Mots de passe : jamais stockés en clair, seulement un condensé PBKDF2-SHA256 salé (nombre d'itérations enregistré avec lui pour pouvoir l'augmenter plus tard).</summary>
	public static class Passwords
	{
		public const int Iterations = 120_000;
		private const int SaltSize = 16, HashSize = 32;

		public static (byte[] hash, byte[] salt) Hash(string password, int iterations = Iterations)
		{
			var salt = RandomNumberGenerator.GetBytes(SaltSize);
			return (Derive(password, salt, iterations), salt);
		}

		public static bool Verify(string password, byte[] hash, byte[] salt, int iterations)
			=> CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), hash);

		private static byte[] Derive(string password, byte[] salt, int iterations)
			=> Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, HashSize);

		/// <summary>Règles d'un mot de passe : 8 caractères au moins (et pas plus de 128).</summary>
		public static string Check(string password)
		{
			if (string.IsNullOrEmpty(password) || password.Length < 8) return "Le mot de passe doit faire au moins 8 caractères.";
			if (password.Length > 128) return "Le mot de passe est trop long (128 caractères au plus).";
			return null;
		}
	}
}

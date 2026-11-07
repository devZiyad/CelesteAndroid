using System;
using System.IO;
using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace CelesteAndroid
{
	/// <summary>
	/// Stores the Steam session (account name + refresh token) encrypted with an AES-GCM key from the Android Keystore
	/// (the key never leaves the chip/TEE). The password is NEVER stored; the file only holds ciphertext.
	/// </summary>
	public static class SteamTokenStore
	{
		private const string KeyAlias = "celeste-steam-session";
		private const string Provider = "AndroidKeyStore";

		private static string SteamDir(Android.Content.Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "steam");
		private static string FilePath(Android.Content.Context context) => Path.Combine(SteamDir(context), "session.bin");
		public static string ResumeStatePath(Android.Content.Context context) => Path.Combine(SteamDir(context), "resume.json");

		public static bool HasSession(Android.Content.Context context) => File.Exists(FilePath(context));

		private static IKey GetOrCreateKey()
		{
			var store = KeyStore.GetInstance(Provider)!;
			store.Load(null);
			if (store.GetKey(KeyAlias, null) is IKey existing)
				return existing;
			var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
			generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
				.SetBlockModes(KeyProperties.BlockModeGcm)!
				.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
				.SetKeySize(256)!
				.Build());
			return generator.GenerateKey()!;
		}

		public static void Save(Android.Content.Context context, string accountName, string refreshToken)
		{
			byte[] plain = Encoding.UTF8.GetBytes(accountName + "\n" + refreshToken);
			var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
			cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
			byte[] iv = cipher.GetIV()!;
			byte[] encrypted = cipher.DoFinal(plain)!;
			Array.Clear(plain);
			Directory.CreateDirectory(SteamDir(context));
			using FileStream file = File.Create(FilePath(context));
			file.WriteByte((byte)iv.Length);
			file.Write(iv);
			file.Write(encrypted);
		}

		public static (string Account, string RefreshToken)? Load(Android.Content.Context context)
		{
			if (!File.Exists(FilePath(context)))
				return null;
			try
			{
				byte[] all = File.ReadAllBytes(FilePath(context));
				int ivLength = all[0];
				var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
				cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(128, all, 1, ivLength));
				byte[] plain = cipher.DoFinal(all, 1 + ivLength, all.Length - 1 - ivLength)!;
				string text = Encoding.UTF8.GetString(plain);
				Array.Clear(plain);
				int nl = text.IndexOf('\n');
				return nl <= 0 ? null : (text[..nl], text[(nl + 1)..]);
			}
			catch (Exception)
			{
				// Invalidated key or corrupted file: treat it as "no session" (the user signs in again).
				Clear(context);
				return null;
			}
		}

		/// <summary>Deletes the stored session and its Keystore key (a new key is made on the next sign-in).</summary>
		public static void Clear(Android.Content.Context context)
		{
			if (File.Exists(FilePath(context)))
				File.Delete(FilePath(context));
			try
			{
				var store = KeyStore.GetInstance(Provider)!;
				store.Load(null);
				if (store.ContainsAlias(KeyAlias))
					store.DeleteEntry(KeyAlias);
			}
			catch (Exception)
			{
				// Without the file the key protects nothing; ignore a Keystore that refuses to delete it.
			}
		}
	}
}

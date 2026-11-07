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
	/// Guarda a sessão da Steam (nome da conta + refresh token) criptografada com uma chave AES-GCM do Android Keystore
	/// (a chave nunca sai do chip/TEE). A senha NUNCA é guardada; o arquivo só tem texto cifrado.
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
				// Chave invalidada ou arquivo corrompido: trata como "sem sessão" (o usuário entra de novo).
				Clear(context);
				return null;
			}
		}

		public static void Clear(Android.Content.Context context)
		{
			if (File.Exists(FilePath(context)))
				File.Delete(FilePath(context));
		}
	}
}

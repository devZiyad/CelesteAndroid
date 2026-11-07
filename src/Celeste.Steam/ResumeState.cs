using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CelesteAndroid.Steam
{
	/// <summary>
	/// Estado de retomada do download: quais chunks de cada arquivo já foram gravados e quais arquivos já foram verificados.
	/// Vale só para o manifesto indicado; é apagado quando a instalação termina verificada.
	/// </summary>
	public sealed class ResumeState
	{
		public ulong ManifestId { get; set; }
		public Dictionary<string, HashSet<int>> Chunks { get; set; } = new(StringComparer.Ordinal);
		public HashSet<string> VerifiedFiles { get; set; } = new(StringComparer.Ordinal);

		private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

		public static ResumeState Load(string path, ulong manifestId)
		{
			try
			{
				if (File.Exists(path))
				{
					ResumeState? state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(path), Json);
					if (state != null && state.ManifestId == manifestId)
						return state;
				}
			}
			catch (Exception e) when (e is IOException or JsonException)
			{
			}
			return new ResumeState { ManifestId = manifestId };
		}

		public void Save(string path)
		{
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);
			string tmp = path + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
			File.Move(tmp, path, overwrite: true);
		}

		public static void Delete(string path)
		{
			if (File.Exists(path)) File.Delete(path);
			if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
		}
	}
}

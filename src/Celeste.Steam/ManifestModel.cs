using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelesteAndroid.Steam
{
	/// <summary>Um chunk de um arquivo do depot (o que o CDN entrega, comprimido e criptografado).</summary>
	public sealed record ManifestChunk(string Id, long Offset, int UncompressedLength, int CompressedLength, uint Checksum);

	/// <summary>Um arquivo do manifesto. <see cref="Path"/> usa sempre '/' como separador, relativo à raiz do depot.</summary>
	public sealed record ManifestFile(string Path, long Size, int Flags, string Sha1, IReadOnlyList<ManifestChunk> Chunks)
	{
		/// <summary>EDepotFileFlag.Directory.</summary>
		public const int DirectoryFlag = 64;

		[JsonIgnore]
		public bool IsDirectory => (Flags & DirectoryFlag) != 0;

		public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
	}

	/// <summary>
	/// O manifesto "gravado" de um depot (nomes, tamanhos, hashes e chunks; nenhuma chave): é o que os testes usam
	/// no lugar da Steam e o formato em que o app pode gravar o manifesto real para virar fixture.
	/// </summary>
	public sealed record ManifestSnapshot(uint AppId, uint DepotId, ulong ManifestId, string Branch, IReadOnlyList<ManifestFile> Files)
	{
		private static readonly JsonSerializerOptions Json = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			WriteIndented = true,
		};

		public string ToJson() => JsonSerializer.Serialize(this, Json);

		public static ManifestSnapshot FromJson(string json)
		{
			ManifestSnapshot snapshot = JsonSerializer.Deserialize<ManifestSnapshot>(json, Json)
				?? throw new InvalidDataException("Manifest JSON is empty.");
			// Normaliza caminhos vindos de qualquer fonte (Windows usa '\').
			return snapshot with
			{
				Files = snapshot.Files.Select(f => f with { Path = ManifestFile.NormalizePath(f.Path) }).ToList(),
			};
		}

		public static ManifestSnapshot Load(string path) => FromJson(File.ReadAllText(path));
	}
}

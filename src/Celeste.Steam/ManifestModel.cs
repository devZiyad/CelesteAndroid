using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelesteAndroid.Steam
{
	/// <summary>A chunk of a depot file (what the CDN delivers, compressed and encrypted).</summary>
	public sealed record ManifestChunk(string Id, long Offset, int UncompressedLength, int CompressedLength, uint Checksum);

	/// <summary>A manifest file. <see cref="Path"/> always uses '/' as the separator, relative to the depot root.</summary>
	public sealed record ManifestFile(string Path, long Size, int Flags, string Sha1, IReadOnlyList<ManifestChunk> Chunks)
	{
		/// <summary>EDepotFileFlag.Directory.</summary>
		public const int DirectoryFlag = 64;

		[JsonIgnore]
		public bool IsDirectory => (Flags & DirectoryFlag) != 0;

		public static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
	}

	/// <summary>
	/// A "recorded" depot manifest (names, sizes, hashes and chunks; no keys): what the tests use
	/// in place of Steam, and the format in which the app can record the real manifest to become a fixture.
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
			// Normalizes paths from any source (Windows uses '\').
			return snapshot with
			{
				Files = snapshot.Files.Select(f => f with { Path = ManifestFile.NormalizePath(f.Path) }).ToList(),
			};
		}

		public static ManifestSnapshot Load(string path) => FromJson(File.ReadAllText(path));
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Android.Content;
using Android.Database;
using Android.Graphics;
using Android.Provider;
using Android.Util;
using CelesteAndroid.Patcher;
using Microsoft.Win32.SafeHandles;
using Path = System.IO.Path;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// Imports the user's game files into the app's internal storage and prepares everything to run:
	/// copy (folder, .zip or the personal APK's assets) → MonoMod patch → side-bar background.
	/// </summary>
	public sealed class GameInstaller
	{
		private const string GameAssetsRoot = "game";
		private const int BufferSize = 1 << 20;

		private readonly Context context;
		private readonly Action<string, float> progress;
		private readonly byte[] buffer = new byte[BufferSize];

		public GameInstaller(Context context, Action<string, float> progress)
		{
			this.context = context;
			this.progress = progress;
		}

		private static string Files(Context context) => context.FilesDir!.AbsolutePath;
		public static string GameDir(Context context) => Path.Combine(Files(context), "Celeste");
		public static string PatchedDll(Context context) => Path.Combine(Files(context), "patched", "Celeste.dll");
		public static string BackgroundPng(Context context) => Path.Combine(Files(context), "background.png");
		public static string UserDir(Context context) => Path.Combine(Files(context), "userdata");

		public static bool IsInstalled(Context context) =>
			File.Exists(PatchedDll(context)) && Directory.Exists(Path.Combine(GameDir(context), "Content"));

		/// <summary>Personal APK: the game ships in the assets (build with -p:EmbedGame=true).</summary>
		public static bool HasEmbeddedGame(Context context) =>
			context.Assets!.List(GameAssetsRoot)?.Contains("Celeste.exe") == true;

		#region Import

		/// <summary>Folder chosen by the user (ACTION_OPEN_DOCUMENT_TREE).</summary>
		public void ImportFolder(Uri treeUri)
		{
			progress("Looking for Celeste in the folder…", -1);
			ContentResolver resolver = context.ContentResolver!;
			string rootId = DocumentsContract.GetTreeDocumentId(treeUri)!;

			Doc root = FindGameRoot(treeUri, new Doc(rootId, "", true, 0), depth: 3)
				?? throw new InstallException("Couldn't find Celeste.exe and the Content folder there.");
			List<Doc> top = ListChildren(treeUri, root.Id);
			CheckFnaBuild(top.Any(d => d.Name.Equals("FNA.dll", StringComparison.OrdinalIgnoreCase)));

			var files = new List<(Doc doc, string relative)>();
			files.Add((top.First(d => d.Name == "Celeste.exe"), "Celeste.exe"));
			CollectFiles(treeUri, top.First(d => d.Name == "Content" && d.IsDir), "Content", files);

			CopyAll(files.Select(f => (f.relative, f.doc.Size,
				(Func<Stream>)(() => resolver.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(treeUri, f.doc.Id)!)!))));
		}

		/// <summary>.zip chosen by the user (e.g. the Linux zip from itch.io).</summary>
		public void ImportZip(Uri zipUri)
		{
			progress("Reading the .zip…", -1);
			using var pfd = context.ContentResolver!.OpenFileDescriptor(zipUri, "r")
				?? throw new InstallException("Couldn't open the file.");
			using var stream = new FileStream(new SafeFileHandle(pfd.DetachFd(), ownsHandle: true), FileAccess.Read);
			using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

			ZipArchiveEntry exe = zip.Entries
				.Where(e => e.Name.Equals("Celeste.exe", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.FullName.Length)
				.FirstOrDefault() ?? throw new InstallException("This .zip doesn't contain Celeste.exe.");
			string prefix = exe.FullName[..^exe.Name.Length];
			CheckFnaBuild(zip.GetEntry(prefix + "FNA.dll") != null);

			var entries = zip.Entries
				.Where(e => e == exe || (e.FullName.StartsWith(prefix + "Content/", StringComparison.Ordinal) && e.Name.Length > 0))
				.ToList();
			CopyAll(entries.Select(e => (e.FullName[prefix.Length..], e.Length, (Func<Stream>)e.Open)));
		}

		/// <summary>Personal APK: copies the game embedded in the assets.</summary>
		public void ImportEmbedded()
		{
			progress("Preparing the bundled game…", -1);
			var files = new List<string>();
			CollectAssets(GameAssetsRoot, files);
			CopyAll(files.Select(f => (f[(GameAssetsRoot.Length + 1)..], -1L, (Func<Stream>)(() => context.Assets!.Open(f)))));
		}

		#region Saves

		public static string SavesDir(Context context) => Path.Combine(UserDir(context), "Celeste", "Saves");

		/// <summary>"0.celeste", "1.celeste", "2.celeste", "debug.celeste" and "settings.celeste": the files the game keeps in Saves/.</summary>
		private static bool IsSaveFileName(string name) =>
			name.EndsWith(".celeste", StringComparison.OrdinalIgnoreCase)
			&& (name.Equals("settings.celeste", StringComparison.OrdinalIgnoreCase) || name.Equals("debug.celeste", StringComparison.OrdinalIgnoreCase)
				|| (name.Length > ".celeste".Length && name[..^".celeste".Length].All(char.IsAsciiDigit)));

		/// <summary>Reads the current save files (raw bytes, never re-serialized, so an export matches byte for byte).</summary>
		private List<(string Name, byte[] Data)> ReadCurrentSaves()
		{
			string dir = SavesDir(context);
			if (!Directory.Exists(dir))
				return new();
			return Directory.GetFiles(dir, "*.celeste").Where(f => IsSaveFileName(Path.GetFileName(f)))
				.OrderBy(f => f, StringComparer.Ordinal).Select(f => (Path.GetFileName(f), File.ReadAllBytes(f))).ToList();
		}

		/// <summary>Writes the save files (and the settings) into a folder chosen with the file picker.</summary>
		public int ExportSavesToFolder(Uri treeUri)
		{
			progress("Exporting saves…", -1);
			List<(string Name, byte[] Data)> saves = ReadCurrentSaves();
			if (saves.Count == 0)
				throw new InstallException("There are no save files to export yet.");
			ContentResolver resolver = context.ContentResolver!;
			string rootId = DocumentsContract.GetTreeDocumentId(treeUri)!;
			Uri parent = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId)!;
			List<Doc> existing = ListChildren(treeUri, rootId);
			foreach ((string name, byte[] data) in saves)
			{
				Doc old = existing.FirstOrDefault(d => d.Name == name && !d.IsDir);
				Uri target = old.Id != null
					? DocumentsContract.BuildDocumentUriUsingTree(treeUri, old.Id)!
					: DocumentsContract.CreateDocument(resolver, parent, "application/octet-stream", name)
						?? throw new InstallException("Couldn't create " + name + " in that folder.");
				using Stream output = resolver.OpenOutputStream(target, "wt")!;
				output.Write(data, 0, data.Length);
			}
			return saves.Count;
		}

		/// <summary>Writes the save files (and the settings) into a .zip created with the file picker.</summary>
		public int ExportSavesToZip(Uri zipUri)
		{
			progress("Exporting saves…", -1);
			List<(string Name, byte[] Data)> saves = ReadCurrentSaves();
			if (saves.Count == 0)
				throw new InstallException("There are no save files to export yet.");
			using Stream output = context.ContentResolver!.OpenOutputStream(zipUri, "wt")
				?? throw new InstallException("Couldn't write to that file.");
			using var zip = new ZipArchive(output, ZipArchiveMode.Create);
			foreach ((string name, byte[] data) in saves)
			{
				using Stream entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
				entry.Write(data, 0, data.Length);
			}
			return saves.Count;
		}

		/// <summary>
		/// Saves from a PC (or another device): a folder with the .celeste files, or the game folder with Saves/ inside.
		/// </summary>
		public int ImportSavesFromFolder(Uri treeUri)
		{
			progress("Looking for save files…", -1);
			string rootId = DocumentsContract.GetTreeDocumentId(treeUri)!;
			List<Doc> files = ListChildren(treeUri, rootId);
			Doc? savesDir = files.FirstOrDefault(d => d.IsDir && d.Name.Equals("Saves", StringComparison.OrdinalIgnoreCase));
			if (savesDir is Doc dir)
				files = ListChildren(treeUri, dir.Id);
			var found = new List<(string Name, byte[] Data)>();
			foreach (Doc save in files.Where(d => !d.IsDir && IsSaveFileName(d.Name)))
			{
				using Stream input = context.ContentResolver!.OpenInputStream(DocumentsContract.BuildDocumentUriUsingTree(treeUri, save.Id)!)!;
				using var memory = new MemoryStream();
				input.CopyTo(memory);
				found.Add((save.Name, memory.ToArray()));
			}
			return ImportSaveFiles(found);
		}

		/// <summary>Saves from a .zip (the files may sit at the top or in a Saves/ folder).</summary>
		public int ImportSavesFromZip(Uri zipUri)
		{
			progress("Reading the .zip…", -1);
			using Stream input = context.ContentResolver!.OpenInputStream(zipUri) ?? throw new InstallException("Couldn't open the file.");
			using var zip = new ZipArchive(input, ZipArchiveMode.Read);
			var found = new List<(string Name, byte[] Data)>();
			foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Name.Length > 0 && IsSaveFileName(e.Name)))
			{
				using Stream stream = entry.Open();
				using var memory = new MemoryStream();
				stream.CopyTo(memory);
				found.Add((entry.Name, memory.ToArray()));
			}
			return ImportSaveFiles(found);
		}

		/// <summary>Validates every file first (nothing is touched if one is bad), backs up the current saves, then overwrites.</summary>
		private int ImportSaveFiles(List<(string Name, byte[] Data)> incoming)
		{
			if (incoming.Count == 0)
				throw new InstallException("No Celeste save files (0.celeste, settings.celeste...) were found there.");
			foreach ((string name, byte[] data) in incoming)
				ValidateSave(name, data);

			string target = SavesDir(context);
			Directory.CreateDirectory(target);
			string[] current = Directory.GetFiles(target, "*.celeste");
			if (current.Length > 0)
			{
				string backup = Path.Combine(UserDir(context), "Celeste", "ImportBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
				Directory.CreateDirectory(backup);
				foreach (string existing in current)
					File.Copy(existing, Path.Combine(backup, Path.GetFileName(existing)), overwrite: true);
				Log.Info(GameActivity.LogTag, $"[CelesteAndroid] saves: backed up {current.Length} file(s) to {backup}");
			}
			foreach ((string name, byte[] data) in incoming)
			{
				string path = Path.Combine(target, name.ToLowerInvariant());
				File.WriteAllBytes(path + ".tmp", data);
				File.Move(path + ".tmp", path, overwrite: true);
			}
			return incoming.Count;
		}

		private static void ValidateSave(string name, byte[] data)
		{
			string expected = name.Equals("settings.celeste", StringComparison.OrdinalIgnoreCase) ? "Settings" : "SaveData";
			try
			{
				var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
				using var reader = System.Xml.XmlReader.Create(new MemoryStream(data), settings);
				var doc = System.Xml.Linq.XDocument.Load(reader);
				if (doc.Root?.Name.LocalName != expected)
					throw new InstallException($"{name} is not a Celeste {(expected == "Settings" ? "settings" : "save")} file.");
			}
			catch (System.Xml.XmlException)
			{
				throw new InstallException($"{name} is not a valid Celeste file, so nothing was imported.");
			}
		}

		#endregion

		private void CheckFnaBuild(bool hasFna)
		{
			if (!hasFna)
			{
				throw new InstallException(
					"This is the XNA version of Celeste. On Steam, enable the \"opengl\" beta " +
					"(Properties → Betas) and copy the folder again, or use the Linux .zip from itch.io.");
			}
		}

		private void CopyAll(IEnumerable<(string relative, long size, Func<Stream> open)> source)
		{
			var files = source.ToList();
			long total = Math.Max(1, files.Sum(f => Math.Max(0, f.size)));
			long done = 0;

			string staging = GameDir(context) + ".importing";
			if (Directory.Exists(staging))
				Directory.Delete(staging, recursive: true);

			for (int i = 0; i < files.Count; i++)
			{
				var (relative, size, open) = files[i];
				string dest = Path.Combine(staging, relative.Replace('\\', '/'));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				using (Stream input = open())
				using (FileStream output = File.Create(dest))
				{
					int read;
					while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
					{
						output.Write(buffer, 0, read);
						done += read;
					}
				}
				// Without a known size (assets), progress follows the file count.
				float fraction = size >= 0 ? done / (float)total : (i + 1) / (float)files.Count;
				if (i % 8 == 0 || i == files.Count - 1)
					progress($"Copying game files… {i + 1}/{files.Count}", fraction);
			}

			if (!File.Exists(Path.Combine(staging, "Celeste.exe")) || !Directory.Exists(Path.Combine(staging, "Content")))
				throw new InstallException("The copy is incomplete.");

			string gameDir = GameDir(context);
			if (Directory.Exists(gameDir))
				Directory.Delete(gameDir, recursive: true);
			Directory.Move(staging, gameDir);
		}

		#endregion

		#region Patch and background

		/// <summary>Generates the Celeste.dll that runs on Android's .NET (MonoMod, on the device itself).</summary>
		public void Patch()
		{
			progress("Patching Celeste.exe", -1);
			string patcherDir = Path.Combine(context.CacheDir!.AbsolutePath, "patcher");
			Directory.CreateDirectory(patcherDir);
			foreach (string asset in context.Assets!.List("patcher")!)
			{
				using Stream input = context.Assets.Open("patcher/" + asset);
				using FileStream output = File.Create(Path.Combine(patcherDir, asset));
				input.CopyTo(output);
			}

			string patched = PatchedDll(context);
			string staging = patched + ".tmp";
			CelestePatcher.Patch(
				Path.Combine(GameDir(context), "Celeste.exe"),
				Path.Combine(patcherDir, "Celeste.Android.mm.dll"),
				staging,
				new[] { patcherDir },
				msg => Log.Info(GameActivity.LogTag, msg)
			);
			File.Move(staging, patched, overwrite: true);
			// MonoMod also writes debug symbols (.mdb) that we do not use.
			foreach (string leftover in Directory.GetFiles(Path.GetDirectoryName(patched)!, "*.mdb"))
				File.Delete(leftover);
			// The copy of the patcher modules (tens of MB) is only needed for this run: leave nothing in the cache.
			if (Directory.Exists(patcherDir))
				Directory.Delete(patcherDir, recursive: true);
		}

		/// <summary>
		/// Deletes the downloaded/imported game (game folder, patched Celeste.dll, background and download state) to free space.
		/// Saves and settings are kept. Returns how many bytes were freed.
		/// </summary>
		public long DeleteGame()
		{
			progress("Deleting the game files…", -1);
			long freed = 0;
			foreach (string dir in new[] { GameDir(context), GameDir(context) + ".importing", Path.GetDirectoryName(PatchedDll(context))!, Path.Combine(Files(context), "steam", "resume") })
			{
				if (!Directory.Exists(dir)) continue;
				freed += Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
				Directory.Delete(dir, recursive: true);
			}
			foreach (string file in new[] { BackgroundPng(context), Path.Combine(Files(context), "steam", "resume.json"), Path.Combine(Files(context), "steam", "resume.json.tmp"),
				Path.Combine(Files(context), "steam", "manifest-record.json") })
			{
				if (!File.Exists(file)) continue;
				freed += new FileInfo(file).Length;
				File.Delete(file);
			}
			return freed;
		}

		/// <summary>Blurred, darkened version of the game's key art, for the side bars.</summary>
		public void PrepareBackground()
		{
			string art = Path.Combine(GameDir(context), "Content", "Graphics", "SplashScreen.png");
			if (!File.Exists(art))
				return;
			progress("Generating background art", -1);

			const int width = 192, height = 108;
			using Bitmap decoded = BitmapFactory.DecodeFile(art, new BitmapFactory.Options { InSampleSize = 8 })!;
			using Bitmap small = Bitmap.CreateScaledBitmap(decoded, width, height, true)!;
			int[] pixels = new int[width * height];
			small.GetPixels(pixels, 0, width, 0, 0, width, height);

			// Three box-blur passes ≈ Gaussian blur; then darken so it does not compete with the game.
			for (int pass = 0; pass < 3; pass++)
			{
				BoxBlur(pixels, width, height, radius: 7, horizontal: true);
				BoxBlur(pixels, width, height, radius: 7, horizontal: false);
			}
			for (int i = 0; i < pixels.Length; i++)
			{
				int p = pixels[i];
				pixels[i] = unchecked((int)0xFF000000)
					| (((p >> 16) & 0xFF) * 38 / 100) << 16
					| (((p >> 8) & 0xFF) * 38 / 100) << 8
					| ((p & 0xFF) * 38 / 100);
			}

			using Bitmap result = Bitmap.CreateBitmap(pixels, width, height, Bitmap.Config.Argb8888!)!;
			using FileStream output = File.Create(BackgroundPng(context));
			result.Compress(Bitmap.CompressFormat.Png!, 100, output);
		}

		private static void BoxBlur(int[] pixels, int width, int height, int radius, bool horizontal)
		{
			int lines = horizontal ? height : width;
			int length = horizontal ? width : height;
			int[] line = new int[length];
			for (int l = 0; l < lines; l++)
			{
				int Index(int i) => horizontal ? l * width + i : i * width + l;
				for (int i = 0; i < length; i++)
					line[i] = pixels[Index(i)];

				for (int i = 0; i < length; i++)
				{
					int r = 0, g = 0, b = 0, count = 0;
					for (int k = Math.Max(0, i - radius); k <= Math.Min(length - 1, i + radius); k++)
					{
						int p = line[k];
						r += (p >> 16) & 0xFF;
						g += (p >> 8) & 0xFF;
						b += p & 0xFF;
						count++;
					}
					pixels[Index(i)] = unchecked((int)0xFF000000) | (r / count) << 16 | (g / count) << 8 | (b / count);
				}
			}
		}

		#endregion

		#region SAF / assets

		private readonly record struct Doc(string Id, string Name, bool IsDir, long Size);

		private List<Doc> ListChildren(Uri treeUri, string parentId)
		{
			var result = new List<Doc>();
			Uri children = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentId)!;
			string[] projection =
			{
				DocumentsContract.Document.ColumnDocumentId,
				DocumentsContract.Document.ColumnDisplayName,
				DocumentsContract.Document.ColumnMimeType,
				DocumentsContract.Document.ColumnSize,
			};
			using ICursor? cursor = context.ContentResolver!.Query(children, projection, null, null, null);
			while (cursor != null && cursor.MoveToNext())
			{
				result.Add(new Doc(
					cursor.GetString(0)!,
					cursor.GetString(1) ?? "",
					cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir,
					cursor.IsNull(3) ? -1 : cursor.GetLong(3)
				));
			}
			return result;
		}

		/// <summary>Accepts the game folder itself or a folder above it (e.g. Steam's "common").</summary>
		private Doc? FindGameRoot(Uri treeUri, Doc dir, int depth)
		{
			List<Doc> children = ListChildren(treeUri, dir.Id);
			if (children.Any(c => c.Name == "Celeste.exe") && children.Any(c => c.Name == "Content" && c.IsDir))
				return dir;
			if (depth == 0)
				return null;
			foreach (Doc child in children.Where(c => c.IsDir))
			{
				Doc? found = FindGameRoot(treeUri, child, depth - 1);
				if (found != null)
					return found;
			}
			return null;
		}

		private void CollectFiles(Uri treeUri, Doc dir, string relative, List<(Doc, string)> files)
		{
			foreach (Doc child in ListChildren(treeUri, dir.Id))
			{
				string path = relative + "/" + child.Name;
				if (child.IsDir)
					CollectFiles(treeUri, child, path, files);
				else
					files.Add((child, path));
			}
		}

		private void CollectAssets(string path, List<string> files)
		{
			string[] children = context.Assets!.List(path) ?? Array.Empty<string>();
			if (children.Length == 0)
			{
				files.Add(path);
				return;
			}
			foreach (string child in children)
				CollectAssets(path + "/" + child, files);
		}

		#endregion
	}

	public sealed class InstallException(string message) : Exception(message);
}

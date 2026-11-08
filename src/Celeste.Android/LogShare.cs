using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Util;
using CelesteAndroid.Steam;
using Uri = Android.Net.Uri;

namespace CelesteAndroid
{
	/// <summary>
	/// "Share logs": builds one plain-text report (app/device/port settings, Celeste's error log if there is one, the app's own recent logcat),
	/// scrubbed of anything credential-like (<see cref="LogScrubber"/>), and hands it to Android's share sheet.
	/// </summary>
	public static class LogShare
	{
		private const string LogTag = "CelesteLogShare";
		private const int LogcatLines = 4000;
		private const int ErrorLogMaxChars = 100_000;

		public static string Directory(Context context) => System.IO.Path.Combine(context.CacheDir!.AbsolutePath, "logs");

		public static string Authority(Context context) => context.PackageName + ".logs";

		/// <summary>Writes the report (replacing any earlier one) and returns its path. Blocking: call off the UI thread.</summary>
		public static string CreateReport(Context context)
		{
			string dir = Directory(context);
			System.IO.Directory.CreateDirectory(dir);
			foreach (string old in System.IO.Directory.GetFiles(dir, "*.txt"))
				File.Delete(old);

			string path = System.IO.Path.Combine(dir, $"celeste-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
			string report = Compose(context);
			// The Steam account name is a secret for the scrubber (it is not printed anywhere in the report on purpose).
			report = LogScrubber.Scrub(report, SteamAccount.Name(context));
			File.WriteAllText(path, report, new UTF8Encoding(false));
			Log.Info(LogTag, $"report written: {path} ({new FileInfo(path).Length} bytes)");
			return path;
		}

		/// <summary>The share sheet for <paramref name="path"/>.</summary>
		public static Intent ShareIntent(Context context, string path)
		{
			Uri uri = Uri.Parse($"content://{Authority(context)}/{System.IO.Path.GetFileName(path)}")!;
			var send = new Intent(Intent.ActionSend)
				.SetType("text/plain")!
				.PutExtra(Intent.ExtraStream, uri)!
				.PutExtra(Intent.ExtraSubject, "Celeste logs")!
				.AddFlags(ActivityFlags.GrantReadUriPermission)!;
			send.ClipData = ClipData.NewRawUri("Celeste logs", uri);
			return Intent.CreateChooser(send, "Share logs")!;
		}

		private static string Compose(Context context)
		{
			var sb = new StringBuilder();
			Android.Content.PM.PackageInfo? info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0);
			sb.AppendLine("Celeste Android — logs");
			sb.AppendLine($"Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
			sb.AppendLine($"App: {context.PackageName} {info?.VersionName} (build {info?.LongVersionCode})");
			sb.AppendLine($"Device: {Build.Manufacturer} {Build.Model} ({Build.Device})");
			sb.AppendLine($"Android: {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), ABI {(Build.SupportedAbis is { Count: > 0 } abis ? abis[0] : "?")}");
			sb.AppendLine($"Game installed: {(GameInstaller.IsInstalled(context) ? "yes" : "no")}");
			sb.AppendLine();
			sb.AppendLine("Port settings:");
			foreach (PortSetting setting in PortSettings.All)
				sb.AppendLine($"  {setting.Key} = {PortSettings.GetValue(context, setting)}");
			sb.AppendLine();

			sb.AppendLine("--- Celeste error log ---");
			string? errorLog = ReadErrorLog(context);
			sb.AppendLine(errorLog ?? "(none)");
			sb.AppendLine();

			sb.AppendLine($"--- Recent logcat (last {LogcatLines} lines of this app) ---");
			sb.AppendLine(ReadLogcat());
			return sb.ToString();
		}

		private static string? ReadErrorLog(Context context)
		{
			foreach (string dir in new[] { GameInstaller.GameDir(context), context.FilesDir!.AbsolutePath, GameInstaller.UserDir(context) })
			{
				string file = System.IO.Path.Combine(dir, "error_log.txt");
				if (!File.Exists(file)) continue;
				try
				{
					string text = File.ReadAllText(file);
					return text.Length > ErrorLogMaxChars ? "(start cut)\n" + text[^ErrorLogMaxChars..] : text;
				}
				catch (IOException e)
				{
					return "(could not be read: " + e.Message + ")";
				}
			}
			return null;
		}

		/// <summary>The app's own logcat (an app can only read its own; both processes share the UID).</summary>
		private static string ReadLogcat()
		{
			try
			{
				Java.Lang.Process process = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-d", "-v", "threadtime", "-t", LogcatLines.ToString() })!;
				var lines = new List<string>();
				using (var reader = new Java.IO.BufferedReader(new Java.IO.InputStreamReader(process.InputStream)))
				{
					string? line;
					while ((line = reader.ReadLine()) != null)
						lines.Add(line);
				}
				process.Destroy();
				return string.Join("\n", lines);
			}
			catch (Exception e)
			{
				return "(logcat could not be read: " + e.Message + ")";
			}
		}
	}

	/// <summary>Serves the report in cache/logs/ to the share sheet (read-only, temporary URI grants only).</summary>
	[ContentProvider(new[] { "${applicationId}.logs" }, Name = "org.devziyad.celeste.LogShareProvider", Exported = false, GrantUriPermissions = true)]
	public class LogShareProvider : ContentProvider
	{
		public override bool OnCreate() => true;

		private Java.IO.File? Resolve(Uri uri)
		{
			string? name = uri.LastPathSegment;
			if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains("..")) return null;
			var file = new Java.IO.File(LogShare.Directory(Context!), name);
			return file.Exists() ? file : null;
		}

		public override ParcelFileDescriptor? OpenFile(Uri uri, string mode)
		{
			Java.IO.File file = Resolve(uri) ?? throw new Java.IO.FileNotFoundException(uri.ToString());
			return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
		}

		public override ICursor? Query(Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
		{
			Java.IO.File? file = Resolve(uri);
			if (file == null) return null;
			var cursor = new MatrixCursor(new[] { IOpenableColumns.DisplayName, IOpenableColumns.Size });
			cursor.AddRow(new Java.Lang.Object[] { file.Name!, file.Length() });
			return cursor;
		}

		public override string? GetType(Uri uri) => "text/plain";
		public override Uri? Insert(Uri uri, ContentValues? values) => null;
		public override int Delete(Uri uri, string? selection, string[]? selectionArgs) => 0;
		public override int Update(Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
	}
}

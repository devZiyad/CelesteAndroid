using System;
using System.Globalization;

namespace CelesteAndroid.Steam
{
	/// <summary>Transfer numbers behind the install's stats line.</summary>
	public sealed record TransferStats(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, double BytesPerSecond);

	/// <summary>
	/// Pure text helpers for the launcher's install progress (no Android dependency, unit tested): the status line's step text, the
	/// stats line ("312 / 1217 files · 640 MB / 1.1 GB · 3.0 MB/s") and the middle ellipsis that keeps a long path on one line.
	/// </summary>
	public static class InstallText
	{
		public const string Ellipsis = "…";

		/// <summary>"1.1 GB" from one gigabyte up, otherwise whole megabytes ("640 MB"); a non-zero amount below a megabyte in kilobytes ("123 KB").</summary>
		public static string FormatBytes(long bytes) =>
			bytes >= 1L << 30
				? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB")
				: bytes > 0 && bytes < 1L << 20
					? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes / 1024)} KB")
					: string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB");

		/// <summary>"3.0 MB/s"; below one megabyte per second "512 KB/s".</summary>
		public static string FormatSpeed(double bytesPerSecond) =>
			bytesPerSecond >= 1 << 20
				? string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / (1 << 20):0.0} MB/s")
				: string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024:0} KB/s");

		/// <summary>"312 / 1217 files · 640 MB / 1.1 GB · 3.0 MB/s". The speed part is left out while it is unknown (zero).</summary>
		public static string FormatStats(TransferStats s)
		{
			string line = $"{s.FilesDone} / {s.FilesTotal} files · {FormatBytes(s.BytesDone)} / {FormatBytes(s.BytesTotal)}";
			return s.BytesPerSecond > 0 ? line + " · " + FormatSpeed(s.BytesPerSecond) : line;
		}

		/// <summary>The status line while a file is being worked on: "Downloading Content/Maps/1-ForsakenCity.bin".</summary>
		public static string StepLine(string phase, string? file) =>
			string.IsNullOrEmpty(file) ? phase + " files" : phase + " " + file;

		/// <summary>
		/// Shortens <paramref name="text"/> to at most <paramref name="maxChars"/> characters by replacing its middle with an ellipsis
		/// (the start and the end both survive: "Content/Ma…ity.bin"). Text that already fits is returned unchanged.
		/// </summary>
		public static string EllipsizeMiddle(string text, int maxChars)
		{
			if (text.Length <= maxChars) return text;
			if (maxChars <= 0) return "";
			if (maxChars == 1) return Ellipsis;
			int keep = maxChars - 1;
			int head = (keep + 1) / 2, tail = keep / 2;
			return text[..head] + Ellipsis + text[^tail..];
		}

		/// <summary>
		/// The longest middle-ellipsized version of <paramref name="text"/> whose measured width is at most <paramref name="maxWidth"/>.
		/// The first <paramref name="protectedPrefix"/> characters (e.g. "Downloading ") are never cut. <paramref name="measure"/> is the
		/// text width in pixels (a TextView's Paint.MeasureText on the device).
		/// </summary>
		public static string FitMiddle(string text, Func<string, float> measure, float maxWidth, int protectedPrefix = 0)
		{
			if (measure(text) <= maxWidth) return text;
			protectedPrefix = Math.Clamp(protectedPrefix, 0, text.Length);
			string prefix = text[..protectedPrefix], rest = text[protectedPrefix..];
			int lo = 0, hi = rest.Length - 1; // largest length that still fits; 0 = only the ellipsis
			while (lo < hi)
			{
				int mid = (lo + hi + 1) / 2;
				if (measure(prefix + EllipsizeMiddle(rest, mid)) <= maxWidth) lo = mid; else hi = mid - 1;
			}
			return prefix + EllipsizeMiddle(rest, lo);
		}
	}
}

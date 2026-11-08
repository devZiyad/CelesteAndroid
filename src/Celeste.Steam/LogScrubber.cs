using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CelesteAndroid.Steam
{
	/// <summary>
	/// Removes anything that looks like a credential or an account detail from log text before it is shared: lines that mention tokens,
	/// passwords or similar are dropped whole, and known secrets (the account name), JWT-like tokens, long opaque strings, Steam IDs and
	/// e-mail addresses are replaced inside the other lines. Pure text logic (no Android dependency), unit tested.
	/// </summary>
	public static class LogScrubber
	{
		public const string OmittedLine = "<line omitted: may contain credentials>";

		// Lines that talk about credentials are dropped entirely, even when they carry no value ("refresh token revoked").
		private static readonly Regex SensitiveLine = new(
			@"token|passw|secret|authoriz|bearer|cookie|credential|steam ?guard|2fa|login ?key|sentry",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static readonly (Regex Pattern, string Replacement)[] Patterns =
		{
			(new Regex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]*", RegexOptions.Compiled), "<token>"),
			(new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled), "<steamid>"),
			(new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled), "<email>"),
			(new Regex(@"(?<![A-Za-z0-9_\-+/])[A-Za-z0-9_\-+/]{40,}={0,2}(?![A-Za-z0-9_\-+/])", RegexOptions.Compiled), "<redacted>"),
		};

		/// <param name="text">The log text (any line endings).</param>
		/// <param name="secrets">Strings that must never appear (e.g. the Steam account name); matched ignoring case.</param>
		public static string Scrub(string text, params string?[] secrets)
		{
			var secretList = new List<string>();
			foreach (string? s in secrets)
				if (!string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 2)
					secretList.Add(s.Trim());

			var result = new StringBuilder(text.Length);
			foreach (string line in text.Split('\n'))
			{
				string clean = line.TrimEnd('\r');
				if (SensitiveLine.IsMatch(clean))
				{
					result.Append(OmittedLine).Append('\n');
					continue;
				}
				foreach (string secret in secretList)
					clean = Regex.Replace(clean, Regex.Escape(secret), "<account>", RegexOptions.IgnoreCase);
				foreach (var (pattern, replacement) in Patterns)
					clean = pattern.Replace(clean, replacement);
				result.Append(clean).Append('\n');
			}
			return result.ToString().TrimEnd('\n') + "\n";
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CelesteAndroid
{
	/// <summary>
	/// A port option: labels for the user and the values (text) the game receives through AppContext.
	/// This file is compiled into the patches (it goes into Celeste.dll), the Android app and the desktop host: the game's
	/// PORT menu, the launcher's "Port settings" screen and the host all use the same definition.
	/// </summary>
	public sealed class PortSetting
	{
		public readonly string Key;
		public readonly string Label;
		public readonly string[] Labels;
		public readonly string[] Values;
		public readonly int Default;
		/// <summary>Only takes effect on the next launch (the game menu says so).</summary>
		public readonly bool NextLaunch;

		public PortSetting(string key, string label, string[] labels, string[] values, int defaultIndex, bool nextLaunch = false)
		{
			Key = key; Label = label; Labels = labels; Values = values; Default = defaultIndex; NextLaunch = nextLaunch;
		}

		public int IndexOf(string? value)
		{
			int i = value == null ? -1 : Array.FindIndex(Values, v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
			return i >= 0 ? i : Default;
		}
	}

	public static class PortSettingsCatalog
	{
		public const string ContextPrefix = "CelesteAndroid.";
		/// <summary>AppContext key holding the path of the shared options file (launcher, game and host).</summary>
		public const string FileKey = ContextPrefix + "SettingsFile";
		public const string FileName = "port-settings.json";

		public static readonly PortSetting Renderer = new("Renderer", "Renderer",
			new[] { "Vulkan", "OpenGL ES" }, new[] { "Vulkan", "OpenGL" }, 0, nextLaunch: true);
		// Only 30 and 60: Celeste's logic is fixed at 60 Hz, so a higher cap would never produce more frames.
		public static readonly PortSetting FrameCap = new("FrameCap", "Frame cap",
			new[] { "30", "60" }, new[] { "30", "60" }, 1);
		public static readonly PortSetting VSync = new("VSync", "VSync",
			new[] { "On", "Off" }, new[] { "true", "false" }, 0);
		public static readonly PortSetting ResolutionScale = new("ResolutionScale", "Resolution scale",
			new[] { "50%", "75%", "100%" }, new[] { "0.5", "0.75", "1" }, 2);
		public static readonly PortSetting Overlay = new("Overlay", "Performance overlay",
			new[] { "Off", "FPS", "FPS + frametime graph" }, new[] { "off", "fps", "graph" }, 0);
		public static readonly PortSetting OverlayCorner = new("OverlayCorner", "Overlay corner",
			new[] { "Top left", "Top right", "Bottom left", "Bottom right" },
			new[] { "topleft", "topright", "bottomleft", "bottomright" }, 0);
		public static readonly PortSetting ScreenFit = new("ScreenFit", "Screen fit",
			new[] { "16:9 with side art", "16:9 with black bars", "Stretch to fill" },
			new[] { "sideart", "blackbars", "stretch" }, 0);
		public static readonly PortSetting IntegerScaling = new("IntegerScaling", "Integer scaling",
			new[] { "Off", "On" }, new[] { "false", "true" }, 0);

		public static readonly PortSetting[] All =
		{
			Renderer, FrameCap, VSync, ResolutionScale, Overlay, OverlayCorner, ScreenFit, IntegerScaling,
		};

		public static PortSetting? Find(string key) =>
			Array.Find(All, s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Flat JSON file ({"Key": "value", ...}) shared by the launcher, the game's PORT menu and the
	/// desktop host. Tolerant reader (accepts unquoted true/false/numbers and camelCase keys) and a simple writer,
	/// without depending on System.Text.Json (the code also runs inside the patched Celeste.dll).
	/// </summary>
	public static class PortSettingsFile
	{
		/// <summary>Reads the file; unknown keys are ignored, known ones come back under their canonical name.</summary>
		public static Dictionary<string, string> Read(string? path)
		{
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (path == null || !File.Exists(path))
				return result;
			string text;
			try { text = File.ReadAllText(path); } catch (IOException) { return result; }
			int i = 0;
			while (i < text.Length)
			{
				string? key = NextString(text, ref i);
				if (key == null) break;
				while (i < text.Length && text[i] != ':') i++;
				i++;
				while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
				string value;
				if (i < text.Length && text[i] == '"')
					value = NextString(text, ref i) ?? "";
				else
				{
					int start = i;
					while (i < text.Length && text[i] != ',' && text[i] != '}' && !char.IsWhiteSpace(text[i])) i++;
					value = text.Substring(start, i - start);
				}
				PortSetting? setting = PortSettingsCatalog.Find(key);
				if (setting != null)
					result[setting.Key] = value;
			}
			return result;
		}

		/// <summary>Writes all options (the dictionary value or the default) atomically.</summary>
		public static void Write(string path, IDictionary<string, string> values)
		{
			var sb = new StringBuilder("{\n");
			for (int n = 0; n < PortSettingsCatalog.All.Length; n++)
			{
				PortSetting s = PortSettingsCatalog.All[n];
				string v = values.TryGetValue(s.Key, out string? raw) ? raw : s.Values[s.Default];
				sb.Append("  \"").Append(s.Key).Append("\": \"").Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
				sb.Append(n + 1 < PortSettingsCatalog.All.Length ? ",\n" : "\n");
			}
			sb.Append("}\n");
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);
			string tmp = path + ".tmp";
			File.WriteAllText(tmp, sb.ToString());
			File.Move(tmp, path, overwrite: true);
		}

		private static string? NextString(string text, ref int i)
		{
			while (i < text.Length && text[i] != '"') i++;
			if (i >= text.Length) return null;
			i++;
			var sb = new StringBuilder();
			while (i < text.Length && text[i] != '"')
			{
				if (text[i] == '\\' && i + 1 < text.Length) i++;
				sb.Append(text[i]);
				i++;
			}
			i++;
			return sb.ToString();
		}
	}
}

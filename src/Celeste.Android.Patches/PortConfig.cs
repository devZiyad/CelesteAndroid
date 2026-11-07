using System;
using System.Globalization;

namespace CelesteAndroid
{
	public enum ScreenFitMode { SideArt, BlackBars, Stretch }
	public enum OverlayMode { Off, Fps, Graph }
	public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

	/// <summary>
	/// The port's options ("Port settings" on Android; JSON/flags on desktop). The host publishes them in
	/// AppContext as "CelesteAndroid.&lt;Name&gt;" (text); here they are read and validated.
	/// </summary>
	public static class PortConfig
	{
		public const string Prefix = "CelesteAndroid.";

		private static string? Raw(string key) => AppContext.GetData(Prefix + key) as string;

		/// <summary>"Vulkan" or "OpenGL" (informational here only: the host picks the driver before the game starts).</summary>
		public static string Renderer => Raw("Renderer") ?? "Vulkan";

		/// <summary>
		/// Cap on frames drawn per second: 30 or 60. Celeste's logic is fixed at 60 Hz, so higher values
		/// larger (or "unlimited") become 60.
		/// </summary>
		public static int FrameCap
		{
			get
			{
				string? v = Raw("FrameCap");
				return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 && n < 60 ? n : 60;
			}
		}

		/// <summary>Current text of an option (AppContext) or the catalog default.</summary>
		public static string Get(PortSetting setting) => setting.Values[setting.IndexOf(Raw(setting.Key))];

		/// <summary>
		/// Changes an option at run time (the game's PORT menu): updates AppContext (effective from the next frame)
		/// and writes the file shared with the launcher and the desktop host.
		/// </summary>
		public static void Set(PortSetting setting, string value)
		{
			AppContext.SetData(Prefix + setting.Key, value);
			string? file = AppContext.GetData(PortSettingsCatalog.FileKey) as string;
			if (file == null)
			{
				return;
			}
			var values = new System.Collections.Generic.Dictionary<string, string>();
			foreach (PortSetting s in PortSettingsCatalog.All)
			{
				values[s.Key] = Get(s);
			}
			try
			{
				PortSettingsFile.Write(file, values);
				Console.WriteLine("[CelesteAndroid] setting " + setting.Key.ToLowerInvariant() + "=" + value + " (changed in game, saved to " + PortSettingsCatalog.FileName + ")");
			}
			catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
			{
				Console.WriteLine("[CelesteAndroid] could not save " + file + ": " + e.Message);
			}
		}

		public static bool VSync => ParseBool(Raw("VSync"), true);

		public static float ResolutionScale
		{
			get
			{
				string? v = Raw("ResolutionScale");
				return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
					? Math.Clamp(f, 0.25f, 1f) : 1f;
			}
		}

		public static OverlayMode Overlay => ParseEnum(Raw("Overlay"), OverlayMode.Off);
		public static OverlayCorner Corner => ParseEnum(Raw("OverlayCorner"), OverlayCorner.TopLeft);
		public static ScreenFitMode ScreenFit => ParseEnum(Raw("ScreenFit"), ScreenFitMode.SideArt);
		public static bool IntegerScaling => ParseBool(Raw("IntegerScaling"), false);

		private static bool ParseBool(string? v, bool fallback) =>
			v == null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase);

		private static T ParseEnum<T>(string? v, T fallback) where T : struct =>
			v != null && Enum.TryParse(v.Replace("-", "").Replace("_", ""), ignoreCase: true, out T r) ? r : fallback;

		/// <summary>Logs each applied option ("[CelesteAndroid] setting name=value"), one per line.</summary>
		public static void LogApplied(int backBufferWidth, int backBufferHeight)
		{
			Log("renderer", Renderer);
			string? requestedCap = Raw("FrameCap");
			string capNote = requestedCap != null && requestedCap != FrameCap.ToString(CultureInfo.InvariantCulture)
				? " (requested " + requestedCap + ", limited to 60: the game logic is fixed at 60 Hz)" : "";
			Log("frame_cap", FrameCap.ToString(CultureInfo.InvariantCulture) + capNote);
			Log("vsync", VSync ? "on" : "off");
			Log("resolution_scale", ResolutionScale.ToString("0.##", CultureInfo.InvariantCulture));
			Log("overlay", Overlay.ToString().ToLowerInvariant());
			Log("overlay_corner", Corner.ToString().ToLowerInvariant());
			Log("screen_fit", ScreenFit.ToString().ToLowerInvariant());
			Log("integer_scaling", IntegerScaling ? "on" : "off");
			Log("host", HostConfig.Host);
			Log("backbuffer", backBufferWidth + "x" + backBufferHeight);
		}

		private static void Log(string name, string value) => Console.WriteLine("[CelesteAndroid] setting " + name + "=" + value);
	}
}

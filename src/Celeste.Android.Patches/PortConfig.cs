using System;
using System.Globalization;

namespace CelesteAndroid
{
	public enum ScreenFitMode { SideArt, BlackBars, Stretch }
	public enum OverlayMode { Off, Fps, Graph }
	public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

	/// <summary>
	/// Opções do port ("Port settings" no Android; JSON/flags no desktop). O host as publica no
	/// AppContext como "CelesteAndroid.&lt;Nome&gt;" (texto); aqui são lidas e validadas.
	/// </summary>
	public static class PortConfig
	{
		public const string Prefix = "CelesteAndroid.";

		private static string? Raw(string key) => AppContext.GetData(Prefix + key) as string;

		/// <summary>"Vulkan" ou "OpenGL" (só informativo aqui: o host escolhe o driver antes do jogo iniciar).</summary>
		public static string Renderer => Raw("Renderer") ?? "Vulkan";

		/// <summary>
		/// Limite de quadros desenhados por segundo: 30 ou 60. A lógica do Celeste é fixa em 60 Hz, então valores
		/// maiores (ou "unlimited") viram 60.
		/// </summary>
		public static int FrameCap
		{
			get
			{
				string? v = Raw("FrameCap");
				return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 && n < 60 ? n : 60;
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

		/// <summary>Registra cada opção aplicada ("[CelesteAndroid] setting nome=valor"), uma por linha.</summary>
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

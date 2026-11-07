using System;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>Uma opção da tela "Port settings": rótulos para o usuário e valores que o jogo recebe.</summary>
	public sealed record PortSetting(string Key, string Label, string[] Labels, string[] Values, int Default);

	/// <summary>
	/// Configurações do port. Ficam em SharedPreferences ("port") e chegam ao jogo pelo AppContext
	/// ("CelesteAndroid.&lt;Key&gt;" = valor em texto; ver CelesteAndroid.PortConfig nos patches).
	/// </summary>
	public static class PortSettings
	{
		public const string ContextPrefix = "CelesteAndroid.";
		private const string PrefsName = "port";

		public static readonly PortSetting Renderer = new("Renderer", "Renderer",
			new[] { "Vulkan", "OpenGL ES" }, new[] { "Vulkan", "OpenGL" }, 0);
		// Só 30 e 60: a lógica do Celeste é fixa em 60 Hz, então um limite maior nunca produziria mais quadros.
		public static readonly PortSetting FrameCap = new("FrameCap", "Frame cap",
			new[] { "30", "60" }, new[] { "30", "60" }, 1);
		// Índices gravados por versões antigas (30/60/90/120/ilimitado): qualquer valor acima de 60 vira 60.
		private static readonly string[] LegacyFrameCapLabels = { "30", "60", "90", "120", "unlimited" };
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

		private static ISharedPreferences Prefs(Context context) =>
			context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

		public static int GetIndex(Context context, PortSetting setting)
		{
			int i = Prefs(context).GetInt(setting.Key, setting.Default);
			return i >= 0 && i < setting.Values.Length ? i : setting.Default;
		}

		public static void SetIndex(Context context, PortSetting setting, int index) =>
			Prefs(context).Edit()!.PutInt(setting.Key, ((index % setting.Values.Length) + setting.Values.Length) % setting.Values.Length)!.Apply();

		public static string GetValue(Context context, PortSetting setting) => setting.Values[GetIndex(context, setting)];

		/// <summary>Valores gravados que deixaram de existir (limite de quadros &gt; 60) passam a 60.</summary>
		private static string? MigrateFrameCap(Context context)
		{
			ISharedPreferences prefs = Prefs(context);
			int saved = prefs.GetInt(FrameCap.Key, FrameCap.Default);
			if (saved < FrameCap.Values.Length || saved >= LegacyFrameCapLabels.Length)
				return null;
			prefs.Edit()!.PutInt(FrameCap.Key, FrameCap.Default)!.Apply();
			return $"saved {LegacyFrameCapLabels[saved]} migrated to {FrameCap.Values[FrameCap.Default]}";
		}

		public static void Reset(Context context) => Prefs(context).Edit()!.Clear()!.Apply();

		/// <summary>Publica todas as opções no AppContext para o Celeste.dll patcheado ler.</summary>
		public static void ApplyToAppContext(Context context)
		{
			string? migrated = MigrateFrameCap(context);
			foreach (PortSetting s in All)
			{
				string value = GetValue(context, s);
				AppContext.SetData(ContextPrefix + s.Key, value);
				string note = s == FrameCap && migrated != null ? $" ({migrated})" : "";
				Log.Info("CelesteAndroid", $"[CelesteAndroid] host setting {s.Key}={value}{note}");
			}
		}
	}
}

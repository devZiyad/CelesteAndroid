using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Util;

namespace CelesteAndroid
{
	/// <summary>
	/// Configurações do port no lado do app. Ficam em <c>files/userdata/port-settings.json</c>, o MESMO arquivo que o
	/// menu PORT do jogo lê e grava (ver <see cref="PortSettingsFile"/>), então o que muda em um aparece no outro.
	/// Chegam ao jogo pelo AppContext ("CelesteAndroid.&lt;Key&gt;" = texto; ver CelesteAndroid.PortConfig nos patches).
	/// </summary>
	public static class PortSettings
	{
		public const string ContextPrefix = PortSettingsCatalog.ContextPrefix;

		public static readonly PortSetting Renderer = PortSettingsCatalog.Renderer;
		public static readonly PortSetting FrameCap = PortSettingsCatalog.FrameCap;
		public static readonly PortSetting[] All = PortSettingsCatalog.All;

		// Índices gravados em SharedPreferences ("port") por versões anteriores (importados uma vez para o JSON).
		private static readonly string[] LegacyFrameCap = { "30", "60", "90", "120", "unlimited" };

		public static string FilePath(Context context) => Path.Combine(GameInstaller.UserDir(context), PortSettingsCatalog.FileName);

		private static Dictionary<string, string> Load(Context context)
		{
			string path = FilePath(context);
			if (!File.Exists(path))
				ImportLegacyPrefs(context, path);
			return PortSettingsFile.Read(path);
		}

		private static void ImportLegacyPrefs(Context context, string path)
		{
			ISharedPreferences prefs = context.GetSharedPreferences("port", FileCreationMode.Private)!;
			var values = new Dictionary<string, string>();
			foreach (PortSetting s in All)
			{
				if (!prefs.Contains(s.Key)) continue;
				int i = prefs.GetInt(s.Key, s.Default);
				string[] source = s == FrameCap ? LegacyFrameCap : s.Values;
				if (i >= 0 && i < source.Length)
					values[s.Key] = source[i];
			}
			if (values.Count == 0) return;
			PortSettingsFile.Write(path, values);
			prefs.Edit()!.Clear()!.Apply();
			Log.Info("CelesteAndroid", $"[CelesteAndroid] host imported {values.Count} saved settings from SharedPreferences into {PortSettingsCatalog.FileName}");
		}

		public static int GetIndex(Context context, PortSetting setting) =>
			setting.IndexOf(Load(context).GetValueOrDefault(setting.Key));

		public static void SetIndex(Context context, PortSetting setting, int index)
		{
			Dictionary<string, string> values = Load(context);
			int n = setting.Values.Length;
			values[setting.Key] = setting.Values[((index % n) + n) % n];
			PortSettingsFile.Write(FilePath(context), values);
		}

		public static string GetValue(Context context, PortSetting setting) => setting.Values[GetIndex(context, setting)];

		public static void Reset(Context context)
		{
			string path = FilePath(context);
			if (File.Exists(path)) File.Delete(path);
		}

		/// <summary>
		/// Publica todas as opções no AppContext para o Celeste.dll patcheado ler, junto com o caminho do arquivo
		/// (o menu do jogo grava nele). Valores que não existem mais (ex.: limite de quadros &gt; 60) viram o padrão.
		/// </summary>
		public static void ApplyToAppContext(Context context)
		{
			Dictionary<string, string> values = Load(context);
			bool fix = false;
			foreach (PortSetting s in All)
			{
				values.TryGetValue(s.Key, out string? raw);
				string value = s.Values[s.IndexOf(raw)];
				string note = "";
				if (raw != null && !string.Equals(raw, value, StringComparison.OrdinalIgnoreCase))
				{
					note = $" (saved {raw} migrated to {value})";
					values[s.Key] = value;
					fix = true;
				}
				AppContext.SetData(ContextPrefix + s.Key, value);
				Log.Info("CelesteAndroid", $"[CelesteAndroid] host setting {s.Key}={value}{note}");
			}
			if (fix)
				PortSettingsFile.Write(FilePath(context), values);
			AppContext.SetData(PortSettingsCatalog.FileKey, FilePath(context));
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteAndroid.Desktop
{
	/// <summary>
	/// Opções do port no host desktop, as mesmas da tela "Port settings" do launcher e do menu PORT do jogo.
	/// Fontes (a última vence): <c>port-settings.json</c> ao lado do host (ou <c>--settings arquivo</c>) e flags.
	/// O menu PORT do jogo grava de volta nesse mesmo arquivo.
	/// Chaves do JSON: Renderer, FrameCap, VSync, ResolutionScale, Overlay, OverlayCorner, ScreenFit, IntegerScaling.
	/// Flags: --renderer vulkan|opengl, --frame-cap 30|60 (maiores viram 60), --vsync on|off, --res-scale 0.5..1,
	/// --overlay off|fps|graph, --corner topleft|topright|bottomleft|bottomright, --screen-fit sideart|blackbars|stretch,
	/// --integer-scaling on|off.
	/// </summary>
	public static class PortOptions
	{
		private static readonly Dictionary<string, string> Flags = new(StringComparer.OrdinalIgnoreCase)
		{
			["--renderer"] = "Renderer", ["--frame-cap"] = "FrameCap", ["--vsync"] = "VSync", ["--res-scale"] = "ResolutionScale",
			["--overlay"] = "Overlay", ["--corner"] = "OverlayCorner", ["--screen-fit"] = "ScreenFit", ["--integer-scaling"] = "IntegerScaling",
		};

		/// <summary>Publica as opções no AppContext e devolve os argumentos que sobraram (para o Main do Celeste).</summary>
		public static string[] Apply(string[] args, string hostDir)
		{
			string file = Path.Combine(hostDir, PortSettingsCatalog.FileName);
			int s = Array.IndexOf(args, "--settings");
			if (s >= 0 && s + 1 < args.Length)
				file = Path.GetFullPath(args[s + 1]);

			Dictionary<string, string> values = PortSettingsFile.Read(file);

			var rest = new List<string>();
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--settings") { i++; continue; }
				if (Flags.TryGetValue(args[i], out string? key) && i + 1 < args.Length)
					values[key] = args[++i];
				else
					rest.Add(args[i]);
			}

			foreach ((string key, string value) in values)
				AppContext.SetData(PortSettingsCatalog.ContextPrefix + key, value);
			// O menu PORT do jogo grava aqui (o arquivo é criado na primeira mudança).
			AppContext.SetData(PortSettingsCatalog.FileKey, file);
			return rest.ToArray();
		}
	}
}

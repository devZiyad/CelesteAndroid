using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CelesteAndroid.Desktop
{
	/// <summary>
	/// Opções do port no host desktop, as mesmas da tela "Port settings" do Android. Fontes (a última vence):
	/// <c>port-settings.json</c> ao lado do host (ou <c>--settings arquivo</c>) e flags de linha de comando.
	/// Chaves do JSON: renderer, frameCap, vsync, resolutionScale, overlay, overlayCorner, screenFit, integerScaling.
	/// Flags: --renderer vulkan|opengl, --frame-cap 30|60 (higher values become 60), --vsync on|off, --res-scale 0.5..1,
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
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			string? file = null;
			int s = Array.IndexOf(args, "--settings");
			if (s >= 0 && s + 1 < args.Length)
				file = args[s + 1];
			else if (File.Exists(Path.Combine(hostDir, "port-settings.json")))
				file = Path.Combine(hostDir, "port-settings.json");
			if (file != null)
			{
				using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
				foreach (JsonProperty p in doc.RootElement.EnumerateObject())
				{
					string text = p.Value.ValueKind switch
					{
						JsonValueKind.True => "true",
						JsonValueKind.False => "false",
						_ => p.Value.ToString(),
					};
					values[p.Name] = text;
				}
			}

			var rest = new List<string>();
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--settings") { i++; continue; }
				if (Flags.TryGetValue(args[i], out string? key) && i + 1 < args.Length)
					values[key] = args[++i];
				else
					rest.Add(args[i]);
			}

			foreach ((string key, string value) in values.Select(kv => (Normalize(kv.Key), kv.Value)))
				AppContext.SetData(PortPrefix + key, value);
			return rest.ToArray();
		}

		private const string PortPrefix = "CelesteAndroid.";

		/// <summary>Aceita camelCase do JSON ("frameCap") e o nome canônico ("FrameCap").</summary>
		private static string Normalize(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];
	}
}

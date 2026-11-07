using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CelesteAndroid
{
	/// <summary>
	/// Performance overlay (FPS, or FPS + a frame-time graph), drawn after the scene in
	/// Engine.RenderCore, straight to the backbuffer at native resolution. Uses its own 3x5 font.
	/// Once per second it logs "[CelesteAndroid] fps N ...".
	/// </summary>
	public static class PortOverlay
	{
		private const int Samples = 120;
		private const float GraphMaxMs = 33.4f;

		private static SpriteBatch? batch;
		private static Texture2D? pixel;
		private static readonly float[] frameMs = new float[Samples];
		private static int sampleIndex;
		private static long lastTicks;
		private static long secondStart;
		private static int framesThisSecond;
		private static float lastFps, lastAvgMs;

		// 3x5 glyphs, a 15-char string each (row by row, '#' = lit pixel).
		private static readonly string[] Glyphs = Build();

		private static string[] Build()
		{
			var g = new string[128];
			g['0'] = "###" + "#.#" + "#.#" + "#.#" + "###";
			g['1'] = ".#." + "##." + ".#." + ".#." + "###";
			g['2'] = "###" + "..#" + "###" + "#.." + "###";
			g['3'] = "###" + "..#" + "###" + "..#" + "###";
			g['4'] = "#.#" + "#.#" + "###" + "..#" + "..#";
			g['5'] = "###" + "#.." + "###" + "..#" + "###";
			g['6'] = "###" + "#.." + "###" + "#.#" + "###";
			g['7'] = "###" + "..#" + "..#" + "..#" + "..#";
			g['8'] = "###" + "#.#" + "###" + "#.#" + "###";
			g['9'] = "###" + "#.#" + "###" + "..#" + "###";
			g['.'] = "..." + "..." + "..." + "..." + ".#.";
			g['F'] = "###" + "#.." + "##." + "#.." + "#..";
			g['P'] = "###" + "#.#" + "###" + "#.." + "#..";
			g['S'] = "###" + "#.." + "###" + "..#" + "###";
			g['M'] = "#.#" + "###" + "###" + "#.#" + "#.#";
			return g;
		}

		public static void Draw(GraphicsDevice device)
		{
			OverlayMode mode = PortConfig.Overlay;
			if (mode == OverlayMode.Off)
			{
				return;
			}

			long now = Stopwatch.GetTimestamp();
			if (lastTicks != 0)
			{
				frameMs[sampleIndex++ % Samples] = (now - lastTicks) * 1000f / Stopwatch.Frequency;
			}
			lastTicks = now;
			framesThisSecond++;
			if (secondStart == 0)
			{
				secondStart = now;
			}
			double elapsed = (now - secondStart) / (double)Stopwatch.Frequency;
			if (elapsed >= 1.0)
			{
				lastFps = (float)(framesThisSecond / elapsed);
				lastAvgMs = (float)(elapsed * 1000.0 / framesThisSecond);
				Console.WriteLine("[CelesteAndroid] fps " + lastFps.ToString("0.0", CultureInfo.InvariantCulture)
					+ " frame_ms " + lastAvgMs.ToString("0.0", CultureInfo.InvariantCulture));
				framesThisSecond = 0;
				secondStart = now;
			}
			if (lastFps <= 0f)
			{
				return;
			}

			if (batch == null)
			{
				batch = new SpriteBatch(device);
				pixel = new Texture2D(device, 1, 1);
				pixel.SetData(new[] { Color.White });
			}

			PresentationParameters pp = device.PresentationParameters;
			device.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
			int unit = Math.Max(2, (int)Math.Round(pp.BackBufferHeight / 288f));

			string text = Math.Round(lastFps).ToString(CultureInfo.InvariantCulture) + " FPS";
			if (mode == OverlayMode.Graph)
			{
				text += " " + lastAvgMs.ToString("0.0", CultureInfo.InvariantCulture) + " MS";
			}
			int textW = TextWidth(text, unit);
			int pad = unit * 2;
			int graphH = mode == OverlayMode.Graph ? unit * 12 : 0;
			int barW = Math.Max(1, unit / 2);
			int graphW = mode == OverlayMode.Graph ? Samples * barW : 0;
			int boxW = Math.Max(textW, graphW) + pad * 2;
			int boxH = unit * 5 + pad * 2 + (graphH > 0 ? graphH + pad : 0);

			// Generous margin: rounded corners and the device's camera cutout.
			int margin = unit * 8;
			OverlayCorner corner = PortConfig.Corner;
			int x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? margin : pp.BackBufferWidth - margin - boxW;
			int y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? margin : pp.BackBufferHeight - margin - boxH;

			batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
			Rect(x, y, boxW, boxH, new Color(0, 0, 0, 190));
			DrawText(text, x + pad, y + pad, unit, Color.White);
			if (graphH > 0)
			{
				int gy = y + pad * 2 + unit * 5;
				int gx = x + pad;
				Rect(gx, gy, graphW, graphH, new Color(255, 255, 255, 30));
				for (int i = 0; i < Samples; i++)
				{
					float ms = frameMs[(sampleIndex + i) % Samples];
					int h = (int)Math.Min(graphH, ms / GraphMaxMs * graphH);
					if (h <= 0)
					{
						continue;
					}
					Color c = ms <= 17.5f ? new Color(90, 230, 120) : ms <= 25f ? new Color(250, 210, 70) : new Color(250, 90, 90);
					Rect(gx + i * barW, gy + graphH - h, barW, h, c);
				}
			}
			batch.End();
		}

		private static int TextWidth(string text, int unit) => text.Length * 4 * unit - unit;

		private static void DrawText(string text, int x, int y, int unit, Color color)
		{
			foreach (char ch in text)
			{
				string? glyph = ch < Glyphs.Length ? Glyphs[ch] : null;
				if (glyph != null)
				{
					for (int i = 0; i < 15; i++)
					{
						if (glyph[i] == '#')
						{
							Rect(x + (i % 3) * unit, y + (i / 3) * unit, unit, unit, color);
						}
					}
				}
				x += 4 * unit;
			}
		}

		private static void Rect(int x, int y, int w, int h, Color color) =>
			batch!.Draw(pixel, new Rectangle(x, y, w, h), color);
	}
}

using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>"Screen fit": image fit modes (16:9 with art/black, or stretched) and integer scaling.</summary>
	public static class PortView
	{
		private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		private static void Set(string property, object value) =>
			typeof(Engine).GetProperty(property, Static)!.GetSetMethod(true)!.Invoke(null, new[] { value });

		private static string lastViewport = "";

		/// <summary>Logs the game viewport rectangle whenever it changes (a marker for scripted checks: "[CelesteAndroid] viewport x,y,w,h ...").</summary>
		public static void LogViewport(Viewport viewport, int backWidth, int backHeight)
		{
			string text = $"viewport {viewport.X},{viewport.Y},{viewport.Width},{viewport.Height} backbuffer {backWidth}x{backHeight} fit={PortConfig.ScreenFit.ToString().ToLowerInvariant()} integer={PortConfig.IntegerScaling.ToString().ToLowerInvariant()}";
			if (text == lastViewport)
			{
				return;
			}
			lastViewport = text;
			Console.WriteLine("[CelesteAndroid] " + text);
		}

		/// <summary>Called after the original UpdateView (which already fitted 16:9 and applied ViewPadding).</summary>
		public static void Adjust(int backWidth, int backHeight, int width, int height, int padding)
		{
			ScreenFitMode fit = PortConfig.ScreenFit;
			bool integer = PortConfig.IntegerScaling;
			if (fit != ScreenFitMode.Stretch && !integer)
			{
				return;
			}

			// The game's pixel grid is 320x180; integer scale = a multiple of that.
			int viewW, viewH;
			if (fit == ScreenFitMode.Stretch)
			{
				viewW = integer ? Math.Max(1, backWidth / 320) * 320 : backWidth;
				viewH = integer ? Math.Max(1, backHeight / 180) * 180 : backHeight;
			}
			else
			{
				int scale = Math.Max(1, (int)Math.Floor(Math.Min(backWidth / 320f, backHeight / 180f)));
				viewW = 320 * scale;
				viewH = 180 * scale;
			}
			float ratio = viewH / (float)viewW;
			viewW -= padding * 2;
			viewH -= (int)(ratio * padding * 2f);

			Set("ViewWidth", viewW);
			Set("ViewHeight", viewH);
			Engine.ScreenMatrix = Matrix.CreateScale(viewW / (float)width, viewH / (float)height, 1f);
			Set("Viewport", new Viewport
			{
				X = (int)(backWidth / 2f - viewW / 2),
				Y = (int)(backHeight / 2f - viewH / 2),
				Width = viewW,
				Height = viewH,
				MinDepth = 0f,
				MaxDepth = 1f,
			});
		}
	}
}

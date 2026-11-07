using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>"Screen fit": modos de encaixe da imagem (16:9 com arte/preto ou esticado) e escala inteira.</summary>
	public static class PortView
	{
		private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		private static void Set(string property, object value) =>
			typeof(Engine).GetProperty(property, Static)!.GetSetMethod(true)!.Invoke(null, new[] { value });

		/// <summary>Chamado depois do UpdateView original (que já encaixou 16:9 e aplicou o ViewPadding).</summary>
		public static void Adjust(int backWidth, int backHeight, int width, int height, int padding)
		{
			ScreenFitMode fit = PortConfig.ScreenFit;
			bool integer = PortConfig.IntegerScaling;
			if (fit != ScreenFitMode.Stretch && !integer)
			{
				return;
			}

			// A grade de pixels do jogo é 320x180; escala inteira = múltiplo disso.
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

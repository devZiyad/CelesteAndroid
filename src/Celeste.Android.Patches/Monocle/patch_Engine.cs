using System;
using System.IO;
using CelesteAndroid;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod;

namespace Monocle
{
	/// <summary>
	/// Patches do port em Monocle.Engine. Regra: só ganchos/wrappers aditivos (orig_* + lógica extra), nunca
	/// [MonoModReplace] de um método inteiro, para coexistir com o Everest (ver docs/EVEREST.md).
	/// </summary>
	[MonoModPatch("Monocle.Engine")]
	public class patch_Engine : Engine
	{
		private static Texture2D? artTexture;
		private static SpriteBatch? barBatch;
		private static Texture2D? barPixel;
		private static bool artLoaded;
		private static int updateCount;

		[MonoModIgnore]
		private static bool resizing;

		[MonoModIgnore]
		public patch_Engine(int width, int height, int windowWidth, int windowHeight, string windowTitle, bool fullscreen, bool vsync)
			: base(width, height, windowWidth, windowHeight, windowTitle, fullscreen, vsync)
		{
		}

		protected extern void orig_Update(GameTime gameTime);

		protected override void Update(GameTime gameTime)
		{
			orig_Update(gameTime);
			// A lógica do Celeste roda a 60 Hz fixos (a física depende disso), então o limite é 30 ou 60:
			// 30 = um quadro a cada dois updates; 60 = todo update.
			if (PortConfig.FrameCap < 60 && (updateCount++ % (60 / PortConfig.FrameCap)) != 0)
			{
				SuppressDraw();
			}
		}

		public static extern void orig_SetWindowed(int width, int height);

		/// <summary>Sempre tela cheia no Android: o modo janela renderiza 320x180×escala e o sistema estica a imagem.</summary>
		public static new void SetWindowed(int width, int height)
		{
			if (HostConfig.IsAndroid)
			{
				SetFullscreen();
				return;
			}
			orig_SetWindowed(width, height);
		}

		public static extern void orig_SetFullscreen();

		/// <summary>Depois da tela cheia nativa do original, aplica "Resolution scale" (o FNA3D amplia o backbuffer menor ao apresentar).</summary>
		public static new void SetFullscreen()
		{
			orig_SetFullscreen();
			float scale = PortConfig.ResolutionScale;
			if (global::Celeste.Celeste.IsGGP || scale >= 1f)
			{
				return;
			}
			DisplayMode mode = Graphics.GraphicsDevice.Adapter.CurrentDisplayMode;
			resizing = true;
			Graphics.PreferredBackBufferWidth = Math.Max(320, (int)(mode.Width * scale));
			Graphics.PreferredBackBufferHeight = Math.Max(180, (int)(mode.Height * scale));
			Graphics.ApplyChanges();
			Console.WriteLine("FULLSCREEN " + Graphics.PreferredBackBufferWidth + "x" + Graphics.PreferredBackBufferHeight + " (resolution scale " + scale + ")");
			resizing = false;
		}

		private extern void orig_UpdateView();

		/// <summary>O cálculo original encaixa 16:9; aqui entram "Stretch" e "Integer scaling".</summary>
		private void UpdateView()
		{
			orig_UpdateView();
			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			PortView.Adjust(pp.BackBufferWidth, pp.BackBufferHeight, Width, Height, ViewPadding);
		}

		protected extern void orig_RenderCore();

		/// <summary>
		/// Renderização original (limpa a tela com ClearColor e desenha a cena) e, por cima, as faixas fora da área do
		/// jogo ("Screen fit": arte ou preto) e o overlay de desempenho.
		/// </summary>
		protected override void RenderCore()
		{
			orig_RenderCore();
			DrawBars();
			PortOverlay.Draw(GraphicsDevice);
		}

		private static readonly RasterizerState ScissorState = new() { CullMode = CullMode.None, ScissorTestEnable = true };

		/// <summary>
		/// Pinta só o que fica fora do viewport do jogo: a arte (side art) ou preto. Roda depois da cena porque algumas
		/// cenas (ex.: Level) limpam a tela inteira de preto ao renderizar.
		/// </summary>
		private void DrawBars()
		{
			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			Viewport view = Viewport;
			Rectangle[] bars =
			{
				new(0, 0, view.X, pp.BackBufferHeight),
				new(view.X + view.Width, 0, pp.BackBufferWidth - view.X - view.Width, pp.BackBufferHeight),
				new(view.X, 0, view.Width, view.Y),
				new(view.X, view.Y + view.Height, view.Width, pp.BackBufferHeight - view.Y - view.Height),
			};
			bool any = false;
			foreach (Rectangle bar in bars)
			{
				any |= bar.Width > 0 && bar.Height > 0;
			}
			if (!any)
			{
				return;
			}
			EnsureResources();

			bool art = artTexture != null && PortConfig.ScreenFit == ScreenFitMode.SideArt;
			GraphicsDevice.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
			foreach (Rectangle bar in bars)
			{
				if (bar.Width <= 0 || bar.Height <= 0)
				{
					continue;
				}
				if (art)
				{
					barBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, null, ScissorState);
					GraphicsDevice.ScissorRectangle = bar;
					DrawArt(pp);
				}
				else
				{
					barBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, null, null);
					barBatch.Draw(barPixel, bar, Color.Black);
				}
				barBatch.End();
			}
			GraphicsDevice.Viewport = view;
		}

		/// <summary>"Cover": preenche a tela inteira mantendo a proporção da imagem (dentro de um Begin/End).</summary>
		private static void DrawArt(PresentationParameters pp)
		{
			float scale = Math.Max(
				pp.BackBufferWidth / (float)artTexture!.Width,
				pp.BackBufferHeight / (float)artTexture.Height
			);
			Vector2 size = new Vector2(artTexture.Width, artTexture.Height) * scale;
			Vector2 position = (new Vector2(pp.BackBufferWidth, pp.BackBufferHeight) - size) / 2f;
			barBatch!.Draw(artTexture, position, null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
		}

		private void EnsureResources()
		{
			if (barBatch == null)
			{
				barBatch = new SpriteBatch(GraphicsDevice);
				barPixel = new Texture2D(GraphicsDevice, 1, 1);
				barPixel.SetData(new[] { Color.White });
			}
			if (!artLoaded)
			{
				artLoaded = true;
				string? path = HostConfig.BackgroundPath;
				if (path != null && File.Exists(path))
				{
					using FileStream stream = File.OpenRead(path);
					artTexture = Texture2D.FromStream(GraphicsDevice, stream);
				}
			}
		}
	}
}

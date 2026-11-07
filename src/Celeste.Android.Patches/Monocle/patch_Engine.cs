using System;
using System.IO;
using System.Reflection;
using CelesteAndroid;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod;

namespace Monocle
{
	[MonoModPatch("Monocle.Engine")]
	public class patch_Engine : Engine
	{
		[MonoModIgnore]
		private Scene scene = null!;

		private static Texture2D? pillarboxBackground;
		private static SpriteBatch? pillarboxBatch;
		private static Texture2D? pillarboxPixel;
		private static bool pillarboxLoaded;
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
			// A lógica do Celeste roda a 60 Hz fixos (a física depende disso). O limite de quadros só reduz
			// o desenho: 30 = um quadro a cada dois updates; 60 ou mais = todo update (não há quadros novos além de 60).
			if (PortConfig.FrameCap is > 0 and < 60 && (updateCount++ % (60 / PortConfig.FrameCap)) != 0)
			{
				SuppressDraw();
			}
		}

		/// <summary>Sempre tela cheia: no Android o modo janela renderiza 320x180×escala e o sistema estica a imagem.</summary>
		[MonoModReplace]
		public static new void SetWindowed(int width, int height)
		{
			if (HostConfig.IsAndroid)
			{
				SetFullscreen();
				return;
			}
			if (!global::Celeste.Celeste.IsGGP && width > 0 && height > 0)
			{
				resizing = true;
				Graphics.PreferredBackBufferWidth = width;
				Graphics.PreferredBackBufferHeight = height;
				Graphics.IsFullScreen = false;
				Graphics.ApplyChanges();
				Console.WriteLine("WINDOW-" + width + "x" + height);
				resizing = false;
			}
		}

		/// <summary>Tela cheia na resolução nativa do aparelho × "Resolution scale" (o FNA3D amplia o backbuffer menor ao apresentar).</summary>
		[MonoModReplace]
		public static new void SetFullscreen()
		{
			if (!global::Celeste.Celeste.IsGGP)
			{
				resizing = true;
				DisplayMode mode = Graphics.GraphicsDevice.Adapter.CurrentDisplayMode;
				float scale = PortConfig.ResolutionScale;
				Graphics.PreferredBackBufferWidth = Math.Max(320, (int)(mode.Width * scale));
				Graphics.PreferredBackBufferHeight = Math.Max(180, (int)(mode.Height * scale));
				Graphics.IsFullScreen = true;
				Graphics.ApplyChanges();
				Console.WriteLine("FULLSCREEN " + Graphics.PreferredBackBufferWidth + "x" + Graphics.PreferredBackBufferHeight);
				resizing = false;
			}
		}

		private extern void orig_UpdateView();

		/// <summary>O cálculo original encaixa 16:9; aqui entram "Stretch" e "Integer scaling".</summary>
		private void UpdateView()
		{
			orig_UpdateView();
			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			PortView.Adjust(pp.BackBufferWidth, pp.BackBufferHeight, Width, Height, ViewPadding);
		}

		/// <summary>
		/// Igual ao original, mas desenha uma imagem nas faixas laterais (telas mais largas que 16:9)
		/// em vez de deixá-las pretas, respeita "Screen fit" e desenha o overlay de desempenho por cima.
		/// </summary>
		[MonoModReplace]
		protected new virtual void RenderCore()
		{
			if (scene != null)
			{
				scene.BeforeRender();
			}
			GraphicsDevice.SetRenderTarget(null);
			DrawBars();
			GraphicsDevice.Viewport = Viewport;
			if (!usesSideArt)
			{
				GraphicsDevice.Clear(ClearColor);
			}
			if (scene != null)
			{
				scene.Render();
				scene.AfterRender();
			}
			PortOverlay.Draw(GraphicsDevice);
		}

		private bool usesSideArt;

		/// <summary>Limpa a tela inteira: arte nas faixas ("side art") ou preto.</summary>
		private void DrawBars()
		{
			usesSideArt = false;
			if (PortConfig.ScreenFit == ScreenFitMode.SideArt)
			{
				DrawPillarbox();
				usesSideArt = pillarboxBackground != null;
			}
			if (!usesSideArt)
			{
				PresentationParameters pp = GraphicsDevice.PresentationParameters;
				GraphicsDevice.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
				GraphicsDevice.Clear(Color.Black);
			}
		}

		private void DrawPillarbox()
		{
			if (!pillarboxLoaded)
			{
				pillarboxLoaded = true;
				string? path = HostConfig.BackgroundPath;
				if (path != null && File.Exists(path))
				{
					using FileStream stream = File.OpenRead(path);
					pillarboxBackground = Texture2D.FromStream(GraphicsDevice, stream);
					pillarboxBatch = new SpriteBatch(GraphicsDevice);
					pillarboxPixel = new Texture2D(GraphicsDevice, 1, 1);
					pillarboxPixel.SetData(new[] { Color.White });
				}
			}
			if (pillarboxBackground == null)
			{
				return;
			}

			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			GraphicsDevice.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
			GraphicsDevice.Clear(Color.Black);

			// "Cover": preenche a tela inteira mantendo a proporção da imagem.
			float scale = Math.Max(
				pp.BackBufferWidth / (float)pillarboxBackground.Width,
				pp.BackBufferHeight / (float)pillarboxBackground.Height
			);
			Vector2 size = new Vector2(pillarboxBackground.Width, pillarboxBackground.Height) * scale;
			Vector2 position = (new Vector2(pp.BackBufferWidth, pp.BackBufferHeight) - size) / 2f;

			pillarboxBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, null, null);
			pillarboxBatch.Draw(pillarboxBackground, position, null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
			// Área do jogo: cor de fundo normal, para cenas que não cobrem a tela toda (loading, wipes).
			Viewport view = Viewport;
			pillarboxBatch.Draw(pillarboxPixel, new Rectangle(view.X, view.Y, view.Width, view.Height), ClearColor);
			pillarboxBatch.End();
		}
	}
}

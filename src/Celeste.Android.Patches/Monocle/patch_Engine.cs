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
			// A lógica do Celeste roda a 60 Hz fixos (a física depende disso), então o limite é 30 ou 60:
			// 30 = um quadro a cada dois updates; 60 = todo update.
			if (PortConfig.FrameCap < 60 && (updateCount++ % (60 / PortConfig.FrameCap)) != 0)
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
			if (usesSideArt)
			{
				// Algumas cenas (ex.: Level) limpam a tela inteira de preto ao renderizar: repinta a arte só nas faixas.
				RedrawBars();
			}
			PortOverlay.Draw(GraphicsDevice);
		}

		private bool usesSideArt;

		private static readonly RasterizerState ScissorState = new() { CullMode = CullMode.None, ScissorTestEnable = true };

		/// <summary>Desenha a arte de novo somente fora da área do jogo (faixas laterais/superior/inferior).</summary>
		private void RedrawBars()
		{
			PresentationParameters pp = GraphicsDevice.PresentationParameters;
			Viewport view = Viewport;
			GraphicsDevice.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
			Rectangle[] bars =
			{
				new(0, 0, view.X, pp.BackBufferHeight),
				new(view.X + view.Width, 0, pp.BackBufferWidth - view.X - view.Width, pp.BackBufferHeight),
				new(view.X, 0, view.Width, view.Y),
				new(view.X, view.Y + view.Height, view.Width, pp.BackBufferHeight - view.Y - view.Height),
			};
			foreach (Rectangle bar in bars)
			{
				if (bar.Width <= 0 || bar.Height <= 0)
				{
					continue;
				}
				pillarboxBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, null, ScissorState);
				GraphicsDevice.ScissorRectangle = bar;
				DrawArt(pp);
				pillarboxBatch.End();
			}
			GraphicsDevice.Viewport = view;
		}

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

		/// <summary>"Cover": preenche a tela inteira mantendo a proporção da imagem (dentro de um Begin/End).</summary>
		private static void DrawArt(PresentationParameters pp)
		{
			float scale = Math.Max(
				pp.BackBufferWidth / (float)pillarboxBackground!.Width,
				pp.BackBufferHeight / (float)pillarboxBackground.Height
			);
			Vector2 size = new Vector2(pillarboxBackground.Width, pillarboxBackground.Height) * scale;
			Vector2 position = (new Vector2(pp.BackBufferWidth, pp.BackBufferHeight) - size) / 2f;
			pillarboxBatch!.Draw(pillarboxBackground, position, null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
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

			pillarboxBatch!.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, null, null);
			DrawArt(pp);
			// Área do jogo: cor de fundo normal, para cenas que não cobrem a tela toda (loading, wipes).
			Viewport view = Viewport;
			pillarboxBatch.Draw(pillarboxPixel, new Rectangle(view.X, view.Y, view.Width, view.Height), ClearColor);
			pillarboxBatch.End();
		}
	}
}

using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;

namespace CelesteAndroid
{
	/// <summary>
	/// Base das telas do app: tudo navegável por controle. D-pad / analógico esquerdo movem o foco,
	/// A confirma o item focado, B volta. Eventos de foco/tecla vão para o logcat.
	/// </summary>
	public abstract class ControllerActivity : Activity
	{
		private long nextStickNavMs;
		private FocusSearchDirection? stickDir;

		protected abstract string Tag { get; }

		/// <summary>Primeira view a receber o foco.</summary>
		protected abstract View? InitialFocus();

		/// <summary>B / voltar. Padrão: fecha a tela.</summary>
		protected virtual void OnBackButton()
		{
			Log.Info(Tag, "back: close");
			Finish();
		}

		/// <summary>Esquerda/direita antes de virar movimento de foco; devolva true para consumir (ex.: trocar um valor).</summary>
		protected virtual bool OnHorizontal(int direction) => false;

		protected virtual bool InputBlocked => false;

		protected static string Describe(View? view) => view?.ContentDescription ?? view?.GetType().Name ?? "none";

		protected void WatchFocus(View root) =>
			root.ViewTreeObserver!.GlobalFocusChange += (_, e) =>
				Log.Info(Tag, $"focus: {Describe(e.OldFocus)} -> {Describe(e.NewFocus)}");

		/// <summary>Torna a view alcançável por D-pad/analógico; o destaque do foco vem do fundo (state_focused).</summary>
		protected static void MakeFocusable(View view, string name)
		{
			view.ContentDescription = name;
			view.Focusable = true;
			view.FocusableInTouchMode = true;
		}

		protected void HideSystemBars()
		{
			if (!OperatingSystem.IsAndroidVersionAtLeast(30))
				return;
			// A partir do Android 15 o app já é edge-to-edge por padrão.
			if (!OperatingSystem.IsAndroidVersionAtLeast(35))
				Window!.SetDecorFitsSystemWindows(false);
			IWindowInsetsController? insets = Window!.InsetsController;
			if (insets != null)
			{
				insets.Hide(WindowInsets.Type.SystemBars());
				insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
			}
		}

		protected void EnsureFocus()
		{
			if (CurrentFocus != null && CurrentFocus.IsShown && CurrentFocus.Enabled)
				return;
			View? first = InitialFocus();
			if (first != null && first.Enabled)
				first.RequestFocus();
		}

		protected bool MoveFocus(FocusSearchDirection dir)
		{
			if (InputBlocked)
				return true;
			if (dir == FocusSearchDirection.Left && OnHorizontal(-1))
				return true;
			if (dir == FocusSearchDirection.Right && OnHorizontal(1))
				return true;
			View? from = CurrentFocus;
			if (from == null)
			{
				EnsureFocus();
				return true;
			}
			View? next = from.FocusSearch(dir);
			next?.RequestFocus(dir);
			return true;
		}

		public override bool DispatchKeyEvent(KeyEvent? e)
		{
			if (e == null)
				return base.DispatchKeyEvent(e);
			Keycode code = e.KeyCode;
			Log.Info(Tag, $"key: {code} {e.Action}");
			switch (code)
			{
				case Keycode.DpadLeft or Keycode.DpadRight when !InputBlocked:
					if (e.Action == KeyEventActions.Down && e.RepeatCount == 0 && OnHorizontal(code == Keycode.DpadLeft ? -1 : 1))
						return true;
					break;
				case Keycode.ButtonA:
					// A = confirmar (o Android só mapeia ENTER/DPAD_CENTER por padrão).
					if (e.Action == KeyEventActions.Up && !InputBlocked)
					{
						View? target = CurrentFocus;
						Log.Info(Tag, $"confirm: {Describe(target)}");
						target?.PerformClick();
					}
					return true;
				case Keycode.ButtonB:
					if (e.Action == KeyEventActions.Up && !InputBlocked)
						OnBackButton();
					return true;
			}
			return base.DispatchKeyEvent(e);
		}

		/// <summary>Analógico esquerdo e D-pad "hat" (muitos controles o reportam como eixo) movem o foco, com repetição.</summary>
		public override bool OnGenericMotionEvent(MotionEvent? e)
		{
			if (e != null && e.Action == MotionEventActions.Move
				&& (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick)
			{
				float x = e.GetAxisValue(Axis.HatX), y = e.GetAxisValue(Axis.HatY);
				if (x == 0 && y == 0) { x = e.GetAxisValue(Axis.X); y = e.GetAxisValue(Axis.Y); }
				FocusSearchDirection? dir = null;
				if (Math.Abs(x) > 0.6f || Math.Abs(y) > 0.6f)
				{
					dir = Math.Abs(x) > Math.Abs(y)
						? (x > 0 ? FocusSearchDirection.Right : FocusSearchDirection.Left)
						: (y > 0 ? FocusSearchDirection.Down : FocusSearchDirection.Up);
				}
				long now = SystemClock.UptimeMillis();
				if (dir == null)
				{
					stickDir = null;
				}
				else if (dir != stickDir || now >= nextStickNavMs)
				{
					nextStickNavMs = now + (dir != stickDir ? 380 : 140);
					stickDir = dir;
					Log.Info(Tag, $"stick: {dir}");
					MoveFocus(dir.Value);
				}
				return true;
			}
			return base.OnGenericMotionEvent(e);
		}
	}
}

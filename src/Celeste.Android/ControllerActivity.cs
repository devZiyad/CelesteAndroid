using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;

namespace CelesteAndroid
{
	/// <summary>
	/// Base for the app's screens: everything is navigable with a controller. D-pad / left stick move focus,
	/// A confirms the focused item, B goes back. Focus/key events go to logcat.
	/// </summary>
	public abstract class ControllerActivity : Activity
	{
		private long nextStickNavMs;
		private FocusSearchDirection? stickDir;

		protected abstract string Tag { get; }

		/// <summary>The first view to receive focus.</summary>
		protected abstract View? InitialFocus();

		/// <summary>B / back. Default: closes the screen.</summary>
		protected virtual void OnBackButton()
		{
			Log.Info(Tag, "back: close");
			Finish();
		}

		/// <summary>Left/right before it becomes focus movement; return true to consume it (e.g. to change a value).</summary>
		protected virtual bool OnHorizontal(int direction) => false;

		protected virtual bool InputBlocked => false;

		protected static string Describe(View? view) => view?.ContentDescription ?? view?.GetType().Name ?? "none";

		protected void WatchFocus(View root) =>
			root.ViewTreeObserver!.GlobalFocusChange += (_, e) =>
				Log.Info(Tag, $"focus: {Describe(e.OldFocus)} -> {Describe(e.NewFocus)}");

		/// <summary>Makes the view reachable by D-pad/stick; the focus highlight comes from the background (state_focused).</summary>
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
			// From Android 15 on the app is edge-to-edge by default.
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
					// A = confirm (Android only maps ENTER/DPAD_CENTER by default).
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

		/// <summary>The left stick and the D-pad "hat" (many controllers report it as an axis) move focus, with repeat.</summary>
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

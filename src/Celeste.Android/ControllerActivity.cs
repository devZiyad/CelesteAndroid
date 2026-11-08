using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

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

		/// <summary>L1 (-1) / R1 (+1) shoulder buttons; return true to consume (e.g. to switch tabs).</summary>
		protected virtual bool OnShoulder(int direction) => false;

		/// <summary>
		/// An explicit order for Up/Down, when the screen's layout is not a simple column (e.g. a tab bar above a list). Up/Down walk it and
		/// wrap at both ends. Null (default): the nearest focusable control in that direction, wrapping to the opposite end of the screen.
		/// </summary>
		protected virtual List<View>? VerticalRing() => null;

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
			View? next = null;
			if ((dir == FocusSearchDirection.Up || dir == FocusSearchDirection.Down) && VerticalRing() is { Count: > 0 } ring)
			{
				int at = ring.IndexOf(from);
				int step = dir == FocusSearchDirection.Down ? 1 : -1;
				next = at < 0 ? ring[0] : ring[((at + step) % ring.Count + ring.Count) % ring.Count];
			}
			else
			{
				next = from.FocusSearch(dir);
				if (next == null || ReferenceEquals(next, from))
					next = WrapTarget(from, dir); // wrap-around: past the last item is the first, before the first is the last
			}
			if (next != null && !ReferenceEquals(next, from))
			{
				if (!next.RequestFocus(dir)) next.RequestFocus();
				Log.Info(Tag, $"move: {dir} -> {Describe(next)}");
			}
			return true;
		}

		/// <summary>The control to jump to when there is nothing further in <paramref name="dir"/>: the opposite end of the column / row.</summary>
		private View? WrapTarget(View from, FocusSearchDirection dir)
		{
			var all = new List<View>();
			CollectFocusables(Window!.DecorView, all);
			var items = all.Where(v => v.IsShown && v.Enabled && v.Width > 0 && v.Height > 0).Select(v => (View: v, Pos: ScreenPos(v))).ToList();
			if (dir == FocusSearchDirection.Up || dir == FocusSearchDirection.Down)
			{
				var ordered = items.OrderBy(i => i.Pos.Y).ThenBy(i => i.Pos.X).Select(i => i.View).ToList();
				if (ordered.Count == 0) return null;
				return dir == FocusSearchDirection.Down ? ordered[0] : ordered[^1];
			}
			// Left/Right: stay in the same row (controls whose vertical span overlaps the focused control's).
			(int X, int Y) f = ScreenPos(from);
			int mid = f.Y + from.Height / 2;
			var row = items.Where(i => mid >= i.Pos.Y && mid <= i.Pos.Y + i.View.Height).OrderBy(i => i.Pos.X).Select(i => i.View).ToList();
			if (row.Count < 2) return null;
			return dir == FocusSearchDirection.Right ? row[0] : row[^1];
		}

		/// <summary>Every focusable, visible control under <paramref name="view"/>, skipping groups that block their descendants (the content behind a dialog).</summary>
		private static void CollectFocusables(View view, List<View> into)
		{
			if (view.Visibility != ViewStates.Visible) return;
			if (view is ViewGroup group)
			{
				if (group.DescendantFocusability == DescendantFocusability.BlockDescendants) return;
				for (int i = 0; i < group.ChildCount; i++)
					CollectFocusables(group.GetChildAt(i)!, into);
			}
			if (view.Focusable && view.FocusableInTouchMode) into.Add(view);
		}

		private static (int X, int Y) ScreenPos(View v)
		{
			var loc = new int[2];
			v.GetLocationOnScreen(loc);
			return (loc[0], loc[1]);
		}

		public override bool DispatchKeyEvent(KeyEvent? e)
		{
			if (e == null)
				return base.DispatchKeyEvent(e);
			Keycode code = e.KeyCode;
			Log.Info(Tag, $"key: {code} {e.Action}");
			switch (code)
			{
				case Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight when !InputBlocked:
					// Focus movement is ours (not the system's) so every screen wraps around at its ends. Text fields keep Left/Right for the cursor.
					if (CurrentFocus is EditText && (code == Keycode.DpadLeft || code == Keycode.DpadRight))
						break;
					if (e.Action == KeyEventActions.Down)
					{
						FocusSearchDirection dir = code switch
						{
							Keycode.DpadUp => FocusSearchDirection.Up,
							Keycode.DpadDown => FocusSearchDirection.Down,
							Keycode.DpadLeft => FocusSearchDirection.Left,
							_ => FocusSearchDirection.Right,
						};
						if ((code == Keycode.DpadLeft || code == Keycode.DpadRight) && e.RepeatCount > 0)
							return true; // a held Left/Right does not auto-repeat (one step per press: value changes, tab switches)
						MoveFocus(dir);
					}
					return true;
				case Keycode.ButtonL1 or Keycode.ButtonR1:
					if (e.Action == KeyEventActions.Down && e.RepeatCount == 0 && !InputBlocked)
						OnShoulder(code == Keycode.ButtonL1 ? -1 : 1);
					return true;
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

using System;
using System.Collections.Generic;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;

namespace CelesteAndroid
{
	/// <summary>
	/// A modal dialog in the app's look, drawn over the activity's own root so the activity (and its scroll position) stays where it is.
	/// Gamepad: the focused button is chosen by the caller, A presses it, B closes the dialog (the activity calls <see cref="Dismiss"/>).
	/// Closing it puts focus back on the control that opened it (or on a fallback when that control is gone).
	/// </summary>
	public sealed class UiDialog
	{
		public sealed record Button(string Label, bool Filled, Action? OnClick = null, string? Name = null);

		private readonly FrameLayout host;
		private readonly ViewGroup content;
		private readonly View overlay;
		private readonly View? returnFocus, fallbackFocus;

		public bool IsOpen { get; private set; } = true;

		private UiDialog(FrameLayout host, ViewGroup content, View overlay, View? returnFocus, View? fallbackFocus)
		{
			this.host = host; this.content = content; this.overlay = overlay; this.returnFocus = returnFocus; this.fallbackFocus = fallbackFocus;
		}

		/// <param name="host">The activity's root FrameLayout the dialog is added to.</param>
		/// <param name="content">The view group holding the focusable controls; they cannot take focus while the dialog is open.</param>
		/// <param name="returnFocus">What gets focus when the dialog closes (default: the focused view when it opened).</param>
		/// <param name="fallbackFocus">Used when <paramref name="returnFocus"/> is hidden or disabled by then.</param>
		public static UiDialog Show(Activity activity, FrameLayout host, ViewGroup content, string title, string message,
			IReadOnlyList<Button> buttons, int focusIndex = 0, View? returnFocus = null, View? fallbackFocus = null, bool cancelable = true)
		{
			int Dp(float dp) => UiKit.Dp(activity, dp);
			returnFocus ??= activity.CurrentFocus;
			content.DescendantFocusability = DescendantFocusability.BlockDescendants;

			var overlay = new FrameLayout(activity) { Clickable = true };
			overlay.SetBackgroundColor(Color.Argb(205, 8, 5, 18));
			var card = UiKit.NoClip(new LinearLayout(activity) { Orientation = Orientation.Vertical });
			var shape = new GradientDrawable();
			shape.SetCornerRadius(Dp(22));
			shape.SetColor(Color.ParseColor("#1F1636"));
			shape.SetStroke(Dp(1.5f), Color.Argb(120, 242, 184, 216));
			card.Background = shape;
			card.SetPadding(Dp(26), Dp(22), Dp(26), Dp(22));
			card.Clickable = true; // taps on the card do not reach the overlay's "tap outside to close"
			card.AddView(UiKit.Text(activity, title, UiKit.HeadingSp, Color.White, TypefaceStyle.Bold));
			if (message.Length > 0)
			{
				var scroll = new ScrollView(activity) { VerticalScrollBarEnabled = false };
				scroll.AddView(UiKit.Text(activity, message, UiKit.SecondarySp, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal));
				card.AddView(scroll, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });
			}

			var row = UiKit.NoClip(new LinearLayout(activity) { Orientation = Orientation.Horizontal });
			row.SetGravity(GravityFlags.End);
			card.AddView(row, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(14) });
			overlay.AddView(card, new FrameLayout.LayoutParams(Dp(520), ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
				{ TopMargin = Dp(16), BottomMargin = Dp(16) });
			host.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
			overlay.Alpha = 0f;
			overlay.Animate()!.Alpha(1f).SetDuration(140)!.Start(); // short fade in

			var dialog = new UiDialog(host, content, overlay, returnFocus, fallbackFocus);
			if (cancelable) overlay.Click += (_, _) => dialog.Dismiss();
			View? toFocus = null;
			for (int i = 0; i < buttons.Count; i++)
			{
				Button b = buttons[i];
				TextView view = UiKit.Pill(activity, b.Label, b.Filled);
				view.ContentDescription = b.Name ?? b.Label;
				view.Click += (_, _) => { dialog.Dismiss(); b.OnClick?.Invoke(); };
				row.AddView(view, UiKit.ButtonParams(activity, 0, marginTop: 4, marginSide: 6));
				if (i == focusIndex) toFocus = view;
			}
			toFocus?.RequestFocus();
			return dialog;
		}

		public void Dismiss()
		{
			if (!IsOpen) return;
			IsOpen = false;
			host.RemoveView(overlay);
			content.DescendantFocusability = DescendantFocusability.AfterDescendants;
			View? target = returnFocus is { IsShown: true, Enabled: true } ? returnFocus : fallbackFocus;
			target?.RequestFocus();
		}
	}
}

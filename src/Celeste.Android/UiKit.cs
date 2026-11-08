using System.IO;
using Path = System.IO.Path;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;

namespace CelesteAndroid
{
	/// <summary>
	/// Visual pieces shared by the app's screens. Rules: no fixed heights (height comes from the text + padding, with a minimum),
	/// a margin around every button so the focus outline is never clipped, and parents that do not clip their children.
	/// </summary>
	public static class UiKit
	{
		public static readonly Color Night = Color.ParseColor("#120C22");
		public static readonly Color Accent = Color.ParseColor("#F2B8D8");

		// Type scale shared by every screen.
		public const float TitleSp = 26, HeadingSp = 22, BodySp = 17, SecondarySp = 15, CaptionSp = 14, SectionSp = 12;
		/// <summary>The focus outline's width everywhere (buttons, rows, tabs, fields, links).</summary>
		public const float FocusStrokeDp = 3f;

		public static TextView Text(Context context, string text, float sp, Color color, TypefaceStyle style = TypefaceStyle.Normal)
		{
			var view = new TextView(context) { Text = text };
			view.SetTextSize(ComplexUnitType.Sp, sp);
			view.SetTextColor(color);
			view.SetTypeface(Typeface.Create("sans-serif", style), style);
			return view;
		}

		/// <summary>The night sky behind every screen without the key art (splash, dialogs' card).</summary>
		public static Drawable NightBackground() =>
			new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Color.Argb(255, 38, 24, 70), Color.Argb(255, 18, 12, 34) });

		/// <summary>
		/// The key art (the build's art, or the imported game's) under a dark scrim, as the first layers of a screen's root. The launcher shows
		/// the art itself; the other screens get it dimmed so every screen shares one background.
		/// </summary>
		public static void AddArtBackdrop(Context context, FrameLayout root)
		{
			root.SetBackgroundColor(Night);
			var art = new ImageView(context);
			art.SetScaleType(ImageView.ScaleType.CenterCrop);
			LoadArt(context, art);
			root.AddView(art, new FrameLayout.LayoutParams(-1, -1));
			root.AddView(new View(context)
			{
				Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Color.Argb(225, 38, 24, 70), Color.Argb(245, 18, 12, 34) }),
			}, new FrameLayout.LayoutParams(-1, -1));
		}

		/// <summary>The art embedded in the build, or, in the shareable build, the imported game's key art, or a plain color.</summary>
		public static void LoadArt(Context context, ImageView target)
		{
			if (context.Resources!.GetIdentifier("celeste_art", "drawable", context.PackageName) != 0)
			{
				target.SetImageResource(Resource.Drawable.launcher_art);
				return;
			}
			string imported = Path.Combine(GameInstaller.GameDir(context), "Content", "Graphics", "SplashScreen.png");
			if (File.Exists(imported))
				target.SetImageBitmap(BitmapFactory.DecodeFile(imported, new BitmapFactory.Options { InSampleSize = 2 }));
			else
				target.SetImageResource(Resource.Drawable.launcher_art);
		}

		/// <summary>
		/// The one focus style: the accent outline and a soft accent fill when focused. Idle: nothing, or a fill / outline / "selected" fill
		/// (tabs) as asked.
		/// </summary>
		public static Drawable FocusBackground(Context context, float radiusDp, Color? idleFill = null, Color? idleStroke = null, float idleStrokeDp = 1.5f, Color? selectedFill = null)
		{
			GradientDrawable Shape(Color fill, Color? stroke, float strokeDp)
			{
				var shape = new GradientDrawable();
				shape.SetCornerRadius(Dp(context, radiusDp));
				shape.SetColor(fill);
				if (stroke is Color c) shape.SetStroke(Dp(context, strokeDp), c);
				return shape;
			}
			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, Shape(Color.Argb(90, 242, 184, 216), Accent, FocusStrokeDp));
			if (selectedFill is Color selected)
				states.AddState(new[] { Android.Resource.Attribute.StateSelected }, Shape(selected, null, 0));
			states.AddState(new int[0], Shape(idleFill ?? Color.Transparent, idleStroke, idleStrokeDp));
			return states;
		}

		public static int Dp(Context context, float dp) =>
			(int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

		/// <summary>Pill button with a visible focus outline. Height = text + padding (minimum <paramref name="minHeightDp"/>).</summary>
		public static TextView Pill(Context context, string label, bool filled, float minHeightDp = 44)
		{
			var view = new TextView(context) { Text = label, Gravity = GravityFlags.Center, Clickable = true, Focusable = true, FocusableInTouchMode = true };
			view.SetTextSize(ComplexUnitType.Sp, filled ? 17 : 15);
			view.SetTypeface(Typeface.Create("sans-serif-medium", filled ? TypefaceStyle.Bold : TypefaceStyle.Normal), filled ? TypefaceStyle.Bold : TypefaceStyle.Normal);
			view.LetterSpacing = filled ? 0.12f : 0.02f;
			view.SetTextColor(filled ? Night : Color.White);
			view.SetSingleLine(true);
			view.SetMinHeight(Dp(context, minHeightDp));
			view.SetMinimumHeight(Dp(context, minHeightDp));
			view.SetPadding(Dp(context, 22), Dp(context, 11), Dp(context, 22), Dp(context, 11));

			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, Shape(context,
				filled ? Color.Argb(255, 255, 236, 246) : Color.Argb(110, 242, 184, 216), Accent, FocusStrokeDp));
			states.AddState(new[] { Android.Resource.Attribute.StatePressed }, Shape(context,
				filled ? Color.Argb(255, 242, 214, 232) : Color.Argb(90, 242, 184, 216), Accent, 2f));
			states.AddState(new int[0], filled ? Shape(context, Color.White, null, 0) : Shape(context, Color.Argb(40, 255, 255, 255), Color.Argb(200, 255, 255, 255), 1.5f));
			view.Background = states;
			return view;
		}

		private static GradientDrawable Shape(Context context, Color fill, Color? stroke, float strokeDp)
		{
			var shape = new GradientDrawable();
			shape.SetCornerRadius(Dp(context, 40)); // larger than half the height: becomes a pill
			shape.SetColor(fill);
			if (stroke is Color s)
				shape.SetStroke(Dp(context, strokeDp), s);
			return shape;
		}

		/// <summary>A button's layout params: wrap_content height and a margin around it (so the focus outline fits).</summary>
		public static LinearLayout.LayoutParams ButtonParams(Context context, int widthDp, float marginTop = 6, float marginSide = 4)
		{
			int side = Dp(context, marginSide);
			return new LinearLayout.LayoutParams(widthDp > 0 ? Dp(context, widthDp) : ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
			{
				TopMargin = Dp(context, marginTop), BottomMargin = Dp(context, 6), LeftMargin = side, RightMargin = side,
			};
		}

		/// <summary>
		/// The splash / loading screen: the launcher's night sky, the game's logo (or the name as text), a subtitle and a thin
		/// indeterminate bar. Drawn over an activity's content and removed with <see cref="FadeOut"/>.
		/// </summary>
		public static FrameLayout Splash(Context context, string subtitle)
		{
			var root = new FrameLayout(context) { Clickable = true, Focusable = false }; // swallows touches, never takes focus
			root.Background = NightBackground();
			var column = NoClip(new LinearLayout(context) { Orientation = Orientation.Vertical });
			column.SetGravity(GravityFlags.Center);
			root.AddView(column, new FrameLayout.LayoutParams(-2, -2, GravityFlags.Center));

			int logoId = context.Resources!.GetIdentifier("celeste_logo", "drawable", context.PackageName);
			if (logoId != 0)
			{
				var logo = new ImageView(context);
				logo.SetImageResource(logoId);
				logo.SetAdjustViewBounds(true);
				column.AddView(logo, new LinearLayout.LayoutParams(Dp(context, 190), Dp(context, 130)));
			}
			else
			{
				TextView title = Text(context, "CELESTE", 54, Color.White, TypefaceStyle.Bold);
				title.Gravity = GravityFlags.Center;
				title.LetterSpacing = 0.18f;
				column.AddView(title);
			}
			TextView sub = Text(context, subtitle, SecondarySp, Accent);
			sub.Gravity = GravityFlags.Center;
			sub.LetterSpacing = 0.06f;
			column.AddView(sub, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(context, 4) });

			var bar = new ProgressBar(context, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true };
			bar.IndeterminateTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
			column.AddView(bar, new LinearLayout.LayoutParams(Dp(context, 170), Dp(context, 6)) { TopMargin = Dp(context, 22) });
			return root;
		}

		/// <summary>Fades <paramref name="view"/> out (short), then removes it from its parent.</summary>
		public static void FadeOut(View view, int durationMs = 250, System.Action? done = null)
		{
			view.Animate()!.Alpha(0f).SetDuration(durationMs)!.WithEndAction(new Java.Lang.Runnable(() =>
			{
				(view.Parent as ViewGroup)?.RemoveView(view);
				done?.Invoke();
			}))!.Start();
		}

		/// <summary>Turns off child clipping on a container (the focus outline and shadows may extend past the edge).</summary>
		public static T NoClip<T>(T group) where T : ViewGroup
		{
			group.SetClipChildren(false);
			group.SetClipToPadding(false);
			return group;
		}
	}
}

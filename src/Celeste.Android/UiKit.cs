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
	/// Peças visuais compartilhadas pelas telas do app. Regras: nada de altura fixa (a altura vem do texto + padding, com um mínimo),
	/// margem em volta de todo botão para o contorno de foco nunca ser cortado, e pais que não cortam os filhos.
	/// </summary>
	public static class UiKit
	{
		public static readonly Color Night = Color.ParseColor("#120C22");
		public static readonly Color Accent = Color.ParseColor("#F2B8D8");

		public static int Dp(Context context, float dp) =>
			(int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

		/// <summary>Botão em pílula com contorno de foco visível. Altura = texto + padding (mínimo <paramref name="minHeightDp"/>).</summary>
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
				filled ? Color.Argb(255, 255, 236, 246) : Color.Argb(110, 242, 184, 216), Accent, 3.5f));
			states.AddState(new[] { Android.Resource.Attribute.StatePressed }, Shape(context,
				filled ? Color.Argb(255, 242, 214, 232) : Color.Argb(90, 242, 184, 216), Accent, 2f));
			states.AddState(new int[0], filled ? Shape(context, Color.White, null, 0) : Shape(context, Color.Argb(40, 255, 255, 255), Color.Argb(200, 255, 255, 255), 1.5f));
			view.Background = states;
			return view;
		}

		private static GradientDrawable Shape(Context context, Color fill, Color? stroke, float strokeDp)
		{
			var shape = new GradientDrawable();
			shape.SetCornerRadius(Dp(context, 40)); // maior que metade da altura: vira pílula
			shape.SetColor(fill);
			if (stroke is Color s)
				shape.SetStroke(Dp(context, strokeDp), s);
			return shape;
		}

		/// <summary>Layout params de um botão: altura wrap_content e margem em volta (para o contorno de foco caber).</summary>
		public static LinearLayout.LayoutParams ButtonParams(Context context, int widthDp, float marginTop = 6, float marginSide = 4)
		{
			int side = Dp(context, marginSide);
			return new LinearLayout.LayoutParams(widthDp > 0 ? Dp(context, widthDp) : ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
			{
				TopMargin = Dp(context, marginTop), BottomMargin = Dp(context, 6), LeftMargin = side, RightMargin = side,
			};
		}

		/// <summary>Tira o corte de filhos de um contêiner (o contorno de foco e as sombras podem passar da borda).</summary>
		public static T NoClip<T>(T group) where T : ViewGroup
		{
			group.SetClipChildren(false);
			group.SetClipToPadding(false);
			return group;
		}
	}
}

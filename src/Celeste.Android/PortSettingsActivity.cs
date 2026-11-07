using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;

namespace CelesteAndroid
{
	/// <summary>
	/// "Port settings": renderer, limite de quadros, vsync, escala de resolução, overlay e ajuste de tela.
	/// Controle: cima/baixo escolhem a linha, esquerda/direita (ou A) trocam o valor, B volta.
	/// </summary>
	[Activity(
		Name = "org.devziyad.celeste.PortSettingsActivity",
		Exported = false,
		Theme = "@style/Theme.Celeste",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
	public class PortSettingsActivity : ControllerActivity
	{
		private const string LogTag = "CelestePortSettings";
		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private readonly Dictionary<View, (PortSetting Setting, TextView Value)> rows = new();
		private View? firstRow;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => firstRow;

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
		}

		protected override void OnResume()
		{
			base.OnResume();
			HideSystemBars();
			EnsureFocus();
		}

		protected override bool OnHorizontal(int direction)
		{
			if (CurrentFocus != null && rows.TryGetValue(CurrentFocus, out var row))
			{
				Cycle(row.Setting, row.Value, direction);
				return true;
			}
			return false;
		}

		private void Cycle(PortSetting setting, TextView value, int direction)
		{
			PortSettings.SetIndex(this, setting, PortSettings.GetIndex(this, setting) + direction);
			value.Text = ValueText(setting);
			Log.Info(LogTag, $"changed: {setting.Key}={PortSettings.GetValue(this, setting)}");
		}

		private string ValueText(PortSetting setting) => "‹  " + setting.Labels[PortSettings.GetIndex(this, setting)] + "  ›";

		private View BuildLayout()
		{
			var root = new FrameLayout(this);
			root.SetBackgroundColor(Night);
			root.AddView(new View(this)
			{
				Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[]
				{
					Color.Argb(255, 38, 24, 70), Color.Argb(255, 18, 12, 34),
				}),
			}, new FrameLayout.LayoutParams(-1, -1));

			var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
			column.SetPadding(Dp(72), Dp(16), Dp(72), Dp(8));
			root.AddView(column, new FrameLayout.LayoutParams(-1, -1));

			var title = Text("Port settings", 26, Color.White, TypefaceStyle.Bold);
			title.LetterSpacing = 0.06f;
			column.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(6) });

			var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
			foreach (PortSetting setting in PortSettings.All)
				list.AddView(Row(setting), new LinearLayout.LayoutParams(-1, Dp(44)));

			var reset = Row(null);
			list.AddView(reset, new LinearLayout.LayoutParams(-1, Dp(44)));

			var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
			scroll.AddView(list);
			column.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

			var hint = Text("D-pad / stick  move        ‹ ›  change        A  next        B  back", 12,
				Color.Argb(150, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(hint, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) });
			return root;
		}

		/// <summary>Uma linha "Rótulo ........ ‹ valor ›"; com setting == null, o botão "Reset to defaults".</summary>
		private View Row(PortSetting? setting)
		{
			var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(16), 0, Dp(16), 0);
			row.Background = FocusBackground();
			string name = setting?.Key ?? "ResetDefaults";
			MakeFocusable(row, name);

			var label = Text(setting?.Label ?? "Reset to defaults", 17, setting == null ? Accent : Color.White, TypefaceStyle.Normal);
			row.AddView(label, new LinearLayout.LayoutParams(0, -2, 1f));
			if (setting != null)
			{
				var value = Text(ValueText(setting), 17, Accent, TypefaceStyle.Bold);
				value.Gravity = GravityFlags.End;
				row.AddView(value, new LinearLayout.LayoutParams(-2, -2));
				rows[row] = (setting, value);
				row.Click += (_, _) => Cycle(setting, value, 1);
			}
			else
			{
				row.Click += (_, _) =>
				{
					PortSettings.Reset(this);
					foreach (var (s, v) in rows.Values)
						v.Text = ValueText(s);
					Log.Info(LogTag, "changed: reset to defaults");
				};
			}
			firstRow ??= row;
			return row;
		}

		private Drawable FocusBackground()
		{
			var focused = new GradientDrawable();
			focused.SetCornerRadius(Dp(14));
			focused.SetColor(Color.Argb(70, 242, 184, 216));
			focused.SetStroke(Dp(2.5f), Accent);
			var states = new StateListDrawable();
			states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
			states.AddState(new int[0], new ColorDrawable(Color.Transparent));
			return states;
		}

		private TextView Text(string text, float sp, Color color, TypefaceStyle style)
		{
			var view = new TextView(this) { Text = text };
			view.SetTextSize(ComplexUnitType.Sp, sp);
			view.SetTextColor(color);
			view.SetTypeface(Typeface.Create("sans-serif", style), style);
			return view;
		}

		private int Dp(float dp) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, Resources!.DisplayMetrics);
	}
}

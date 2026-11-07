using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
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
	/// "Port settings": the port's options (renderer, frame cap, autoplay...) and the launcher-only actions: Check for updates and the
	/// Steam account (name, Sign out, Switch account). Controller: up/down pick the row, left/right (or A) change the value, B goes back.
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
		private TextView message = null!, accountText = null!;
		private View signOutRow = null!, switchRow = null!;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => firstRow;

		protected override void OnCreate(Bundle? savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			PortSettings.ApplyToAppContext(this); // also migrates old values that were already saved
			View root = BuildLayout();
			SetContentView(root);
			WatchFocus(root);
			HideSystemBars();
		}

		protected override void OnResume()
		{
			base.OnResume();
			HideSystemBars();
			RefreshAccount();
			EnsureFocus();
		}

		private void RefreshAccount()
		{
			string? name = SteamAccount.Name(this);
			accountText.Text = name == null ? "Not signed in to Steam" : "Signed in as " + name;
			signOutRow.Visibility = name == null ? ViewStates.Gone : ViewStates.Visible;
			switchRow.Visibility = name == null ? ViewStates.Gone : ViewStates.Visible;
		}

		private async void SignOut(bool thenSignInAgain)
		{
			message.Text = "Signing out…";
			try
			{
				await SteamAccount.SignOutAsync(this);
			}
			catch (Exception e)
			{
				Log.Error(LogTag, "sign out failed: " + e.Message);
			}
			RefreshAccount();
			EnsureFocus();
			if (thenSignInAgain)
			{
				message.Text = "";
				StartActivity(new Intent(this, typeof(SteamActivity)).PutExtra(SteamActivity.ExtraForceLogin, true).PutExtra(SteamActivity.ExtraUpdate, true));
			}
			else
			{
				message.Text = "Signed out. Your game files and saves were kept.";
			}
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

			message = Text("", 14, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(message, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(2) });

			// No fixed heights: rows are as tall as their text plus padding, with a margin so the focus outline is never clipped.
			var list = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			list.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
			foreach (PortSetting setting in PortSettings.All)
				list.AddView(Row(setting), RowParams());

			// Launcher-only: not part of the in-game PORT section.
			list.AddView(Section("Steam"), new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(10), LeftMargin = Dp(16) });
			accountText = Text("", 15, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			list.AddView(accountText, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), BottomMargin = Dp(4), TopMargin = Dp(2) });
			list.AddView(ActionRow("Check for updates", "CheckForUpdates", () =>
				StartActivity(new Intent(this, typeof(SteamActivity)).PutExtra(SteamActivity.ExtraUpdate, true))), RowParams());
			signOutRow = ActionRow("Sign out", "SignOut", () => SignOut(thenSignInAgain: false));
			list.AddView(signOutRow, RowParams());
			switchRow = ActionRow("Switch account", "SwitchAccount", () => SignOut(thenSignInAgain: true));
			list.AddView(switchRow, RowParams());

			var reset = Row(null);
			list.AddView(reset, RowParams(topMargin: 10));

			var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
			scroll.SetClipChildren(false);
			scroll.SetClipToPadding(false);
			scroll.AddView(list);
			column.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

			var hint = Text("D-pad / stick  move        ‹ ›  change value        A  next value        B  back", 12,
				Color.Argb(150, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(hint, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(6) });
			return root;
		}

		/// <summary>A row "Label ........ ‹ value ›"; with setting == null, the "Reset to defaults" button.</summary>
		private View Row(PortSetting? setting)
		{
			var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(16), Dp(9), Dp(16), Dp(9));
			row.SetMinimumHeight(Dp(44));
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

		private LinearLayout.LayoutParams RowParams(int topMargin = 0) =>
			new(-1, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2 + topMargin), BottomMargin = Dp(2) };

		private TextView Section(string text)
		{
			var view = Text(text.ToUpperInvariant(), 12, Color.Argb(190, 242, 184, 216), TypefaceStyle.Bold);
			view.LetterSpacing = 0.12f;
			return view;
		}

		/// <summary>A row that runs an action instead of changing a value.</summary>
		private View ActionRow(string label, string name, Action action)
		{
			var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(16), Dp(9), Dp(16), Dp(9));
			row.SetMinimumHeight(Dp(44));
			row.Background = FocusBackground();
			MakeFocusable(row, name);
			row.AddView(Text(label, 17, Accent, TypefaceStyle.Normal), new LinearLayout.LayoutParams(0, -2, 1f));
			row.Click += (_, _) => action();
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

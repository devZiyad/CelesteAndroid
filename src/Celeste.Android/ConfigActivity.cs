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
	/// Config: Port settings (renderer, frame cap, autoplay...), Saves (import/export), Game files (check for updates, re-import, delete)
	/// and the Steam account (name, Sign out, Switch account). These are launcher-only: the in-game PORT section has only the port settings.
	/// Saves import/export and Delete game files run here and report through dialogs; closing a dialog puts focus back on the row that
	/// opened it. Only what needs the launcher's progress UI (re-import, update check) is handed back to it as a result.
	/// Controller: up/down pick the row, left/right (or A) change the value, B goes back.
	/// </summary>
	[Activity(
		Name = "org.devziyad.celeste.ConfigActivity",
		Exported = false,
		Theme = "@style/Theme.Celeste",
		ScreenOrientation = ScreenOrientation.SensorLandscape,
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
			| ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
	public class ConfigActivity : ControllerActivity
	{
		private const string LogTag = "CelesteConfig";

		/// <summary>Result extras: what the launcher should do (<see cref="ExtraAction"/>) and with which picked file or folder (the intent data).</summary>
		public const string ExtraAction = "action";
		public const string ActionReimportFolder = "reimport_folder", ActionCheckUpdates = "check_updates";

		private const int ReqSignIn = 20;
		private const int ReqImportSavesFolder = 11, ReqExportSavesFolder = 13, ReqReimportFolder = 15;
		private static readonly Color Night = Color.ParseColor("#120C22");
		private static readonly Color Accent = Color.ParseColor("#F2B8D8");

		private readonly Dictionary<View, (PortSetting Setting, TextView Value)> rows = new();
		private View? firstRow;
		private TextView message = null!, accountText = null!;
		private View signOutRow = null!, switchRow = null!, deleteRow = null!, reimportFolderRow = null!, checkUpdatesRow = null!;
		private FrameLayout rootFrame = null!;
		private LinearLayout column = null!;
		private UiDialog? dialog;
		private bool working;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => firstRow;
		protected override bool InputBlocked => working;
		protected override void OnBackButton()
		{
			if (dialog is { IsOpen: true }) { Log.Info(LogTag, "back: close dialog"); dialog.Dismiss(); return; }
			base.OnBackButton();
		}

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

		protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
		{
			base.OnActivityResult(requestCode, resultCode, data);
			Android.Net.Uri? uri = data?.Data;
			if (requestCode == ReqSignIn)
			{
				RefreshAccount();
				message.Text = SteamAccount.Name(this) is string name ? "Signed in as " + name : "";
				return;
			}
			if (resultCode != Result.Ok || uri == null) return;
			switch (requestCode)
			{
				case ReqImportSavesFolder:
					RunJob("Saves imported", installer => $"Imported {installer.ImportSavesFromFolder(uri)} save file(s). The previous saves were backed up first.");
					break;
				case ReqExportSavesFolder:
					RunJob("Saves exported", installer => $"Exported {installer.ExportSavesToFolder(uri)} save file(s).");
					break;
				case ReqReimportFolder:
					Hand(ActionReimportFolder, uri);
					break;
			}
		}

		/// <summary>Runs an installer task off the UI thread (input blocked), then reports its outcome in a dialog; failures too.</summary>
		private void RunJob(string successTitle, Func<GameInstaller, string> job, View? returnFocus = null, View? fallbackFocus = null)
		{
			returnFocus ??= CurrentFocus;
			working = true;
			var installer = new GameInstaller(this, (text, _) => RunOnUiThread(() => message.Text = text));
			new System.Threading.Thread(() =>
			{
				string title = successTitle, body;
				try
				{
					body = job(installer);
				}
				catch (InstallException e)
				{
					title = "Couldn't finish"; body = e.Message;
				}
				catch (Exception e)
				{
					Log.Error(LogTag, e.ToString());
					title = "Couldn't finish"; body = "Something went wrong: " + e.Message;
				}
				RunOnUiThread(() =>
				{
					working = false;
					message.Text = "";
					RefreshAccount();
					if (title.Length == 0) ShowInfo(body, "", returnFocus, fallbackFocus); // the outcome itself is the dialog's title
					else ShowInfo(title, body, returnFocus, fallbackFocus);
				});
			}) { Name = "CelesteConfigJob", IsBackground = true }.Start();
		}

		/// <summary>A dialog with one OK button; closing it returns to this section with focus on the row that opened it.</summary>
		private void ShowInfo(string title, string body, View? returnFocus, View? fallbackFocus)
		{
			Log.Info("CelesteAndroid", "[CelesteAndroid] config dialog: " + title);
			dialog = UiDialog.Show(this, rootFrame, column, title, body,
				new[] { new UiDialog.Button("OK", Filled: true, Name: "DialogOk") }, 0, returnFocus, fallbackFocus);
		}

		private void ConfirmDelete()
		{
			View? origin = CurrentFocus;
			dialog = UiDialog.Show(this, rootFrame, column, "Delete game files?",
				"This frees the space used by the game (about 1.2 GB). Your saves and settings are kept; you can install again from Steam or import the game.",
				new[]
				{
					new UiDialog.Button("Cancel", Filled: false, Name: "ConfirmCancel"),
					new UiDialog.Button("Delete", Filled: true, () => RunJob("", installer =>
					{
						long freed = installer.DeleteGame();
						return $"Game files deleted — {freed / (1024 * 1024)} MB freed";
					}, origin, checkUpdatesRow), Name: "ConfirmYes"),
				}, 0, origin, checkUpdatesRow);
		}

		/// <summary>Hands the work to the launcher (it owns the main-screen progress) and closes Config.</summary>
		private void Hand(string action, Android.Net.Uri? uri = null)
		{
			var result = new Intent().PutExtra(ExtraAction, action);
			if (uri != null) result.SetData(uri).AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
			SetResult(Result.Ok, result);
			Finish();
		}

		private void PickFolder(int request) => StartActivityForResult(new Intent(Intent.ActionOpenDocumentTree), request);

		private void RefreshAccount()
		{
			bool installed = GameInstaller.IsInstalled(this);
			bool idle = installed && !InstallCoordinator.IsActive; // never delete or replace the files under a running install
			deleteRow.Visibility = idle ? ViewStates.Visible : ViewStates.Gone;
			reimportFolderRow.Visibility = idle ? ViewStates.Visible : ViewStates.Gone;
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
				StartActivityForResult(new Intent(this, typeof(SteamActivity)).PutExtra(SteamActivity.ExtraForceLogin, true), ReqSignIn);
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
			rootFrame = root;
			root.SetBackgroundColor(Night);
			root.AddView(new View(this)
			{
				Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[]
				{
					Color.Argb(255, 38, 24, 70), Color.Argb(255, 18, 12, 34),
				}),
			}, new FrameLayout.LayoutParams(-1, -1));

			column = new LinearLayout(this) { Orientation = Orientation.Vertical };
			column.SetPadding(Dp(72), Dp(16), Dp(72), Dp(8));
			root.AddView(column, new FrameLayout.LayoutParams(-1, -1));

			var title = Text("Config", 26, Color.White, TypefaceStyle.Bold);
			title.LetterSpacing = 0.06f;
			column.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(6) });

			message = Text("", 14, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(message, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(2) });

			// No fixed heights: rows are as tall as their text plus padding, with a margin so the focus outline is never clipped.
			var list = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			list.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));

			list.AddView(Section("Port settings"), SectionParams(first: true));
			foreach (PortSetting setting in PortSettings.All)
				list.AddView(Row(setting), RowParams());
			list.AddView(Row(null), RowParams());

			list.AddView(Section("Saves"), SectionParams());
			list.AddView(ActionRow("Import saves from a folder", "ImportSavesFolder", () => PickFolder(ReqImportSavesFolder)), RowParams());
			list.AddView(ActionRow("Export saves to a folder", "ExportSavesFolder", () => PickFolder(ReqExportSavesFolder)), RowParams());

			list.AddView(Section("Game files"), SectionParams());
			checkUpdatesRow = ActionRow("Check for updates", "CheckForUpdates", () => Hand(ActionCheckUpdates));
			list.AddView(checkUpdatesRow, RowParams());
			reimportFolderRow = ActionRow("Re-import from folder", "ReimportFolder", () => PickFolder(ReqReimportFolder));
			list.AddView(reimportFolderRow, RowParams());
			deleteRow = ActionRow("Delete game files", "DeleteGameFiles", ConfirmDelete);
			list.AddView(deleteRow, RowParams());

			list.AddView(Section("Steam account"), SectionParams());
			accountText = Text("", 15, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			list.AddView(accountText, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), BottomMargin = Dp(4), TopMargin = Dp(2) });
			signOutRow = ActionRow("Sign out", "SignOut", () => SignOut(thenSignInAgain: false));
			list.AddView(signOutRow, RowParams());
			switchRow = ActionRow("Switch account", "SwitchAccount", () => SignOut(thenSignInAgain: true));
			list.AddView(switchRow, RowParams());

			var scroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
			scroll.SetClipChildren(false);
			scroll.SetClipToPadding(false);
			scroll.AddView(list);
			column.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

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

			var label = Text(setting == null ? "Reset to defaults" : setting.Label, 17, setting == null ? Accent : Color.White, TypefaceStyle.Normal);
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

		private LinearLayout.LayoutParams SectionParams(bool first = false) =>
			new(-2, -2) { TopMargin = Dp(first ? 2 : 14), LeftMargin = Dp(16), BottomMargin = Dp(2) };

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

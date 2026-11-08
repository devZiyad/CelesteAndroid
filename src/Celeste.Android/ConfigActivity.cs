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
	/// Config, in five tabs: Port settings (renderer, frame cap, autoplay...), Saves (import/export), Game files (check for updates,
	/// re-import, delete), Steam account (name, Sign out, Switch account) and About (version). These are launcher-only: the in-game PORT
	/// section has only the port settings. L1/R1 switch tabs; Left/Right on the tab bar too; Down from the tab bar enters the list and Up
	/// from its first row returns to the tab bar. Everything wraps around.
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
		private static readonly Color Accent = UiKit.Accent;

		private static readonly string[] TabNames = { "Port settings", "Saves", "Game files", "Steam account", "About" };

		private readonly Dictionary<View, (PortSetting Setting, TextView Value)> rows = new();
		private readonly TextView[] tabs = new TextView[TabNames.Length];
		private readonly LinearLayout[] panels = new LinearLayout[TabNames.Length];
		private int selectedTab;
		private ScrollView scroll = null!;
		private TextView message = null!, accountText = null!;
		private View signInRow = null!, signOutRow = null!, switchRow = null!, deleteRow = null!, reimportFolderRow = null!, checkUpdatesRow = null!;
		private FrameLayout rootFrame = null!;
		private LinearLayout column = null!;
		private UiDialog? dialog;
		private bool working;

		protected override string Tag => LogTag;
		protected override View? InitialFocus() => tabs[selectedTab];
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
			signInRow.Visibility = name == null ? ViewStates.Visible : ViewStates.Gone;
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
			if (CurrentFocus != null && Array.IndexOf(tabs, CurrentFocus) >= 0)
			{
				SelectTab(selectedTab + direction, focusTab: true);
				return true;
			}
			if (CurrentFocus != null && rows.TryGetValue(CurrentFocus, out var row))
			{
				Cycle(row.Setting, row.Value, direction);
				return true;
			}
			return false;
		}

		protected override bool OnShoulder(int direction)
		{
			if (dialog is { IsOpen: true }) return true;
			SelectTab(selectedTab + direction, focusTab: Array.IndexOf(tabs, CurrentFocus) >= 0);
			return true;
		}

		/// <summary>The Up/Down order: the selected tab, then the visible rows of its panel (wraps at both ends).</summary>
		protected override List<View>? VerticalRing()
		{
			var ring = new List<View> { tabs[selectedTab] };
			LinearLayout panel = panels[selectedTab];
			for (int i = 0; i < panel.ChildCount; i++)
			{
				View child = panel.GetChildAt(i)!;
				if (child.Focusable && child.Visibility == ViewStates.Visible && child.Enabled) ring.Add(child);
			}
			return ring;
		}

		/// <summary>Shows tab <paramref name="index"/> (wrapping). Focus stays on the tab bar when it was there, otherwise it goes to the new tab's first row.</summary>
		private void SelectTab(int index, bool focusTab)
		{
			int n = TabNames.Length;
			selectedTab = ((index % n) + n) % n;
			for (int i = 0; i < n; i++)
			{
				bool on = i == selectedTab;
				tabs[i].Selected = on;
				tabs[i].SetTextColor(on ? Color.White : Color.Argb(190, 255, 255, 255));
				tabs[i].SetTypeface(Typeface.Create("sans-serif-medium", on ? TypefaceStyle.Bold : TypefaceStyle.Normal), on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
				panels[i].Visibility = on ? ViewStates.Visible : ViewStates.Gone;
			}
			scroll.ScrollTo(0, 0);
			panels[selectedTab].Alpha = 0f;
			panels[selectedTab].Animate()!.Alpha(1f).SetDuration(120)!.Start(); // short crossfade between tabs
			RefreshAccount();
			Log.Info("CelesteAndroid", "[CelesteAndroid] config tab: " + TabNames[selectedTab]);
			if (focusTab)
				tabs[selectedTab].RequestFocus();
			else
			{
				List<View> ring = VerticalRing()!;
				(ring.Count > 1 ? ring[1] : ring[0]).RequestFocus();
			}
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
			UiKit.AddArtBackdrop(this, root);

			column = new LinearLayout(this) { Orientation = Orientation.Vertical };
			column.SetPadding(Dp(72), Dp(16), Dp(72), Dp(8));
			root.AddView(column, new FrameLayout.LayoutParams(-1, -1));

			var title = Text("Config", UiKit.TitleSp, Color.White, TypefaceStyle.Bold);
			title.LetterSpacing = 0.06f;
			column.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(6) });

			// Tab bar. No fixed heights; a margin around each tab so the focus outline is never clipped.
			var tabBar = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Horizontal });
			tabBar.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
			for (int i = 0; i < TabNames.Length; i++)
			{
				int index = i;
				var tab = Text(TabNames[i], UiKit.SecondarySp, Color.White, TypefaceStyle.Normal);
				tab.Gravity = GravityFlags.Center;
				tab.SetSingleLine(true);
				tab.SetPadding(Dp(10), Dp(9), Dp(10), Dp(9));
				tab.SetMinimumHeight(Dp(40));
				tab.Background = UiKit.FocusBackground(this, 20, selectedFill: Color.Argb(60, 242, 184, 216));
				MakeFocusable(tab, "Tab" + TabNames[i].Replace(" ", ""));
				tab.Click += (_, _) => SelectTab(index, focusTab: true);
				tabs[i] = tab;
				tabBar.AddView(tab, new LinearLayout.LayoutParams(0, -2, 1f) { LeftMargin = Dp(3), RightMargin = Dp(3) });
			}
			column.AddView(tabBar, new LinearLayout.LayoutParams(-1, -2));

			message = Text("", UiKit.CaptionSp, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			column.AddView(message, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(2), BottomMargin = Dp(2) });

			// No fixed heights: rows are as tall as their text plus padding, with a margin so the focus outline is never clipped.
			var list = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical });
			list.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
			for (int i = 0; i < panels.Length; i++)
			{
				panels[i] = UiKit.NoClip(new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone });
				panels[i].SetPadding(Dp(4), Dp(2), Dp(4), Dp(2)); // room around the rows for their focus outline
				list.AddView(panels[i], new LinearLayout.LayoutParams(-1, -2));
			}

			foreach (PortSetting setting in PortSettings.All)
				panels[0].AddView(Row(setting), RowParams());
			panels[0].AddView(Row(null), RowParams());

			panels[1].AddView(ActionRow("Import saves from a folder", "ImportSavesFolder", () => PickFolder(ReqImportSavesFolder)), RowParams());
			panels[1].AddView(ActionRow("Export saves to a folder", "ExportSavesFolder", () => PickFolder(ReqExportSavesFolder)), RowParams());

			checkUpdatesRow = ActionRow("Check for updates", "CheckForUpdates", () => Hand(ActionCheckUpdates));
			panels[2].AddView(checkUpdatesRow, RowParams());
			reimportFolderRow = ActionRow("Re-import from folder", "ReimportFolder", () => PickFolder(ReqReimportFolder));
			panels[2].AddView(reimportFolderRow, RowParams());
			deleteRow = ActionRow("Delete game files", "DeleteGameFiles", ConfirmDelete);
			panels[2].AddView(deleteRow, RowParams());

			accountText = Text("", UiKit.SecondarySp, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			panels[3].AddView(accountText, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), BottomMargin = Dp(4), TopMargin = Dp(2) });
			signInRow = ActionRow("Sign in to Steam", "SignIn", () => StartActivityForResult(
				new Intent(this, typeof(SteamActivity)).PutExtra(SteamActivity.ExtraForceLogin, true), ReqSignIn));
			panels[3].AddView(signInRow, RowParams());
			signOutRow = ActionRow("Sign out", "SignOut", () => SignOut(thenSignInAgain: false));
			panels[3].AddView(signOutRow, RowParams());
			switchRow = ActionRow("Switch account", "SwitchAccount", () => SignOut(thenSignInAgain: true));
			panels[3].AddView(switchRow, RowParams());

			AddAbout(panels[4]);

			scroll = new ScrollView(this) { VerticalScrollBarEnabled = false };
			scroll.SetClipChildren(false);
			scroll.SetClipToPadding(false);
			scroll.AddView(list);
			column.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

			SelectTabInitial();
			return root;
		}

		private void SelectTabInitial()
		{
			for (int i = 0; i < tabs.Length; i++)
			{
				bool on = i == selectedTab;
				tabs[i].Selected = on;
				tabs[i].SetTextColor(on ? Color.White : Color.Argb(190, 255, 255, 255));
				tabs[i].SetTypeface(Typeface.Create("sans-serif-medium", on ? TypefaceStyle.Bold : TypefaceStyle.Normal), on ? TypefaceStyle.Bold : TypefaceStyle.Normal);
				panels[i].Visibility = on ? ViewStates.Visible : ViewStates.Gone;
			}
		}

		/// <summary>About: the app's name and version.</summary>
		private void AddAbout(LinearLayout panel)
		{
			Android.Content.PM.PackageInfo? info = PackageManager!.GetPackageInfo(PackageName!, 0);
			string version = $"{info?.VersionName ?? "?"} (build {VersionCode.Of(info)})";
			panel.AddView(Text("Celeste — native Android port", UiKit.BodySp, Color.White, TypefaceStyle.Bold),
				new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), TopMargin = Dp(6), BottomMargin = Dp(4) });
			var versionText = Text("Version " + version, UiKit.SecondarySp, Color.Argb(225, 255, 255, 255), TypefaceStyle.Normal);
			versionText.ContentDescription = "Version";
			panel.AddView(versionText, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), BottomMargin = Dp(10) });

			panel.AddView(Text("If something goes wrong, share a plain text file with the app's recent log, Celeste's error log, the version, " +
				"the device and the port settings. It never contains passwords, tokens or the Steam account.", UiKit.CaptionSp, Color.Argb(190, 255, 255, 255), TypefaceStyle.Normal),
				new LinearLayout.LayoutParams(-2, -2) { LeftMargin = Dp(16), BottomMargin = Dp(6) });
			panel.AddView(ActionRow("Share logs", "ShareLogs", ShareLogs), RowParams());
		}

		/// <summary>Builds the report off the UI thread, then opens the share sheet.</summary>
		private void ShareLogs()
		{
			if (working) return;
			working = true;
			message.Text = "Preparing logs…";
			new System.Threading.Thread(() =>
			{
				string? path = null, error = null;
				try { path = LogShare.CreateReport(this); }
				catch (Exception e)
				{
					Log.Error(LogTag, "share logs failed: " + e);
					error = e.Message;
				}
				RunOnUiThread(() =>
				{
					working = false;
					message.Text = "";
					if (path != null)
					{
						Log.Info("CelesteAndroid", "[CelesteAndroid] config: share sheet opened for " + System.IO.Path.GetFileName(path));
						StartActivity(LogShare.ShareIntent(this, path));
					}
					else
						ShowInfo("Couldn't create the logs", error ?? "Something went wrong.", null, null);
				});
			}) { Name = "CelesteShareLogs", IsBackground = true }.Start();
		}

		/// <summary>A row "Label ........ ‹ value ›"; with setting == null, the "Reset to defaults" button.</summary>
		private View Row(PortSetting? setting)
		{
			var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(16), Dp(9), Dp(16), Dp(9));
			row.SetMinimumHeight(Dp(44));
			row.Background = UiKit.FocusBackground(this, 14);
			string name = setting?.Key ?? "ResetDefaults";
			MakeFocusable(row, name);

			var label = Text(setting == null ? "Reset to defaults" : setting.Label, UiKit.BodySp, setting == null ? Accent : Color.White, TypefaceStyle.Normal);
			row.AddView(label, new LinearLayout.LayoutParams(0, -2, 1f));
			if (setting != null)
			{
				var value = Text(ValueText(setting), UiKit.BodySp, Accent, TypefaceStyle.Bold);
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
			return row;
		}

		private LinearLayout.LayoutParams RowParams(int topMargin = 0) =>
			new(-1, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(2 + topMargin), BottomMargin = Dp(2) };

		/// <summary>A row that runs an action instead of changing a value.</summary>
		private View ActionRow(string label, string name, Action action)
		{
			var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			row.SetPadding(Dp(16), Dp(9), Dp(16), Dp(9));
			row.SetMinimumHeight(Dp(44));
			row.Background = UiKit.FocusBackground(this, 14);
			MakeFocusable(row, name);
			row.AddView(Text(label, UiKit.BodySp, Accent, TypefaceStyle.Normal), new LinearLayout.LayoutParams(0, -2, 1f));
			row.Click += (_, _) => action();
			return row;
		}

		private TextView Text(string text, float sp, Color color, TypefaceStyle style) => UiKit.Text(this, text, sp, color, style);

		private int Dp(float dp) => UiKit.Dp(this, dp);
	}
}

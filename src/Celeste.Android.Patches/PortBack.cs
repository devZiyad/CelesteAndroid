using System;
using Celeste;
using Monocle;

namespace CelesteAndroid
{
	/// <summary>
	/// The game side of the system Back button / gesture. <c>GameActivity</c> shows a "Return to launcher?" dialog and tells the game through
	/// <c>AppContext</c> data <c>CelesteAndroid.BackRequest</c> = <c>show</c> | <c>yes</c> | <c>no</c>; this class reads it once per update on the
	/// game thread: show pauses the level, yes leaves through the "Exit to launcher" path (<see cref="PortExit"/>), no resumes the level.
	/// Gamepad buttons never come here: they are plain game input.
	/// </summary>
	public static class PortBack
	{
		public const string Key = "CelesteAndroid.BackRequest";

		/// <summary>True from "show" until "yes" / "no" (the confirmation is on screen); <see cref="PortState"/> reports it.</summary>
		// A plain field, not an auto-property: MonoMod's PatchField clones the backing field's [DebuggerBrowsable(<enum>)] attribute and has to
		// resolve that enum from System.Runtime, which does not exist as a file on the device (the on-device patch would fail).
		public static bool Showing;

		private static bool pausedByUs;
		private static bool exitWanted;

		public static void Tick()
		{
			if (AppContext.GetData(Key) is string request)
			{
				AppContext.SetData(Key, null);
				Handle(request);
			}
			if (exitWanted && PortExit.RequestExit())
			{
				exitWanted = false;
			}
		}

		private static void Handle(string request)
		{
			switch (request)
			{
				case "show":
					Showing = true;
					if (Engine.Scene is Level level && !level.Paused)
					{
						pausedByUs = true;
						PortLifecycle.PauseLevel();
					}
					break;
				case "yes":
					Showing = false;
					pausedByUs = false;
					exitWanted = true;
					break;
				case "reset": // the dialog went away without an answer (the app lost focus): forget it, the game stays paused as it is
					Showing = false;
					pausedByUs = false;
					break;
				case "no":
					Showing = false;
					if (pausedByUs)
					{
						pausedByUs = false;
						PortLifecycle.CancelPendingPause();
						if (Engine.Scene is Level paused && PortExit.PauseMenu(paused) is TextMenu menu)
						{
							menu.OnCancel?.Invoke(); // the pause menu's own "resume" (closes the menu, unpauses, resumes the audio)
						}
					}
					break;
			}
		}
	}
}

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace CelesteAndroid
{
	/// <summary>
	/// Where the game's rumble goes (the "Rumble" port setting): to the controller through FNA/SDL, to the phone's vibrator, or nowhere.
	/// The game has already applied its own rumble setting (Off = never called, Half = half the strength) before it gets here.
	/// The phone's vibrator belongs to the host: it publishes an <c>Action&lt;int, int&gt;</c> (milliseconds, amplitude 1-255; 0, 0 cancels)
	/// in AppContext under <see cref="VibrateKey"/>.
	/// </summary>
	public static class PortRumble
	{
		public const string VibrateKey = "CelesteAndroid.Vibrate";

		private static float phoneStrength;
		private static long phoneUntil; // Environment.TickCount64

		private static Action<int, int>? Vibrator => AppContext.GetData(VibrateKey) as Action<int, int>;

		/// <summary>True when the controller itself should get this rumble (the caller then runs the game's own code).</summary>
		public static bool Route(PlayerIndex player, float strength, float seconds)
		{
			string mode = PortConfig.Get(PortSettingsCatalog.Rumble);
			bool toController = mode is "controller" or "both";
			bool toPhone = mode is "phone" or "both";
			Console.WriteLine($"[CelesteAndroid] rumble: mode={mode} strength={strength:0.00} time={seconds:0.00}s -> " +
				(toController && toPhone ? "controller + phone vibrator" : toController ? "controller" : toPhone ? "phone vibrator" : "nothing"));
			if (toController) LogControllerCall(player, strength);
			if (toPhone) Phone(strength, seconds);
			return toController;
		}

		/// <summary>
		/// Diagnostics for the controller route: what FNA reports for the pad, and whether SDL accepted a rumble (FNA's SetVibration returns
		/// SDL_RumbleGamepad's result). The call is the same one the game makes right after, so it changes nothing.
		/// </summary>
		private static void LogControllerCall(PlayerIndex player, float strength)
		{
			try
			{
				GamePadCapabilities caps = GamePad.GetCapabilities(player);
				bool ok = GamePad.SetVibration(player, strength, strength);
				Console.WriteLine($"[CelesteAndroid] rumble: controller {player} connected={caps.IsConnected} type={caps.GamePadType} id={GamePad.GetGUIDEXT(player)} " +
					$"fnaMotors(left={caps.HasLeftVibrationMotor},right={caps.HasRightVibrationMotor}) SDL_RumbleGamepad({strength:0.00}) returned {ok}");
			}
			catch (Exception e)
			{
				Console.WriteLine("[CelesteAndroid] rumble: controller call failed: " + e.Message);
			}
		}

		private static void Phone(float strength, float seconds)
		{
			Action<int, int>? vibrate = Vibrator;
			if (vibrate == null)
			{
				Console.WriteLine("[CelesteAndroid] rumble: no phone vibrator available");
				return;
			}
			long now = Environment.TickCount64;
			if (now < phoneUntil && strength < phoneStrength)
			{
				return; // the same rule as the game's own: a weaker rumble does not replace a stronger one still running
			}
			int ms = Math.Clamp((int)(seconds * 1000f), 20, 5000);
			int amplitude = Math.Clamp((int)Math.Round(strength * 255f), 1, 255);
			phoneStrength = strength;
			phoneUntil = now + ms;
			vibrate(ms, amplitude);
			Console.WriteLine($"[CelesteAndroid] rumble: phone vibration {ms}ms amplitude {amplitude}");
		}

		/// <summary>The game stopped rumbling (StopRumble): cancel the phone's vibration too.</summary>
		public static void Stop()
		{
			if (Environment.TickCount64 < phoneUntil)
			{
				phoneUntil = 0;
				Vibrator?.Invoke(0, 0);
			}
		}
	}
}

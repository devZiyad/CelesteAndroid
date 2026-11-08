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
			bool controllerRumbles = ControllerCanRumble(player);
			bool toController, toPhone;
			switch (mode)
			{
				case "off": toController = false; toPhone = false; break;
				case "controller": toController = true; toPhone = false; break;
				case "phone": toController = false; toPhone = true; break;
				default: toController = controllerRumbles; toPhone = !controllerRumbles; break; // auto
			}
			Console.WriteLine($"[CelesteAndroid] rumble: mode={mode} strength={strength:0.00} time={seconds:0.00}s controllerRumble={controllerRumbles} -> " +
				(toController ? "controller" : toPhone ? "phone vibrator" : "nothing"));
			if (toPhone) Phone(strength, seconds);
			return toController;
		}

		private static bool ControllerCanRumble(PlayerIndex player)
		{
			try
			{
				GamePadCapabilities caps = GamePad.GetCapabilities(player);
				return caps.IsConnected && (caps.HasLeftVibrationMotor || caps.HasRightVibrationMotor);
			}
			catch (Exception)
			{
				return false;
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

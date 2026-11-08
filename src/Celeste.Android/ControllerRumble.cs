using System;
using System.Text;
using Android.Content;
using Android.Hardware.Input;
using Android.OS;
using Android.Util;
using Android.Views;

namespace CelesteAndroid
{
	/// <summary>
	/// Android's own view of the connected gamepads and their vibrators (InputDevice / VibratorManager), for diagnostics and for the
	/// launcher's "Test rumble" (the game itself rumbles through FNA/SDL). Logs are `[CelesteAndroid] controller: ...` lines.
	/// </summary>
	public static class ControllerRumble
	{
		private const string Tag = "CelesteAndroid";

		private static bool IsGamepad(InputDevice d) =>
			(d.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (d.Sources & InputSourceType.Joystick) == InputSourceType.Joystick;

		/// <summary>One log line per connected gamepad: name, ids, vibrators the system reports. Returns how many gamepads there are.</summary>
		public static int Describe(string why)
		{
			int count = 0;
			foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
			{
				InputDevice? d = InputDevice.GetDevice(id);
				if (d == null || !IsGamepad(d)) continue;
				count++;
				var sb = new StringBuilder($"[CelesteAndroid] controller ({why}): id={id} name='{d.Name}' vendor=0x{d.VendorId:x4} product=0x{d.ProductId:x4} external={!d.IsVirtual} ");
				if (OperatingSystem.IsAndroidVersionAtLeast(31))
				{
					int[] ids = d.VibratorManager.GetVibratorIds();
					sb.Append($"vibrators={ids.Length}");
					foreach (int vid in ids)
					{
						Vibrator v = d.VibratorManager.GetVibrator(vid);
						sb.Append($" [#{vid} has={v.HasVibrator} amplitude={v.HasAmplitudeControl}]");
					}
				}
				else
					sb.Append("vibrators=n/a (Android < 12)");
				Log.Info(Tag, sb.ToString());
			}
			if (count == 0) Log.Info(Tag, $"[CelesteAndroid] controller ({why}): no gamepad connected");
			return count;
		}

		/// <summary>Vibrates every connected gamepad that has a vibrator for <paramref name="ms"/>. Returns how many were asked to.</summary>
		public static int Vibrate(int ms, int amplitude)
		{
			int asked = 0;
			if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return 0;
			foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
			{
				InputDevice? d = InputDevice.GetDevice(id);
				if (d == null || !IsGamepad(d)) continue;
				foreach (int vid in d.VibratorManager.GetVibratorIds())
				{
					Vibrator v = d.VibratorManager.GetVibrator(vid);
					if (!v.HasVibrator) continue;
					v.Vibrate(VibrationEffect.CreateOneShot(ms, v.HasAmplitudeControl ? amplitude : VibrationEffect.DefaultAmplitude)!);
					asked++;
					Log.Info(Tag, $"[CelesteAndroid] controller: vibrate {ms}ms amplitude {amplitude} on '{d.Name}' vibrator #{vid}");
				}
			}
			if (asked == 0) Log.Info(Tag, "[CelesteAndroid] controller: no gamepad vibrator to test (Android reports none)");
			return asked;
		}

		private sealed class Listener : Java.Lang.Object, InputManager.IInputDeviceListener
		{
			public void OnInputDeviceAdded(int deviceId) => Describe("added");
			public void OnInputDeviceChanged(int deviceId) { }
			public void OnInputDeviceRemoved(int deviceId) => Log.Info(Tag, $"[CelesteAndroid] controller: device {deviceId} removed");
		}

		private static Listener? listener;

		/// <summary>Logs the gamepads now and whenever one connects.</summary>
		public static void Watch(Context context)
		{
			Describe("start");
			if (listener != null) return;
			listener = new Listener();
			(context.GetSystemService(Context.InputService) as InputManager)?.RegisterInputDeviceListener(listener, new Handler(Looper.MainLooper!));
		}
	}
}

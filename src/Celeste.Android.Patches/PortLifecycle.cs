using System;
using System.Reflection;
using Celeste;
using Monocle;
using SDL3;

namespace CelesteAndroid
{
	/// <summary>
	/// Android app lifecycle for the game. SDL pushes WILL_ENTER_BACKGROUND / DID_ENTER_FOREGROUND from inside its event pump,
	/// i.e. on the game thread, and then blocks that pump until the app is resumed, so an event watch is the one place where
	/// the game can still react: it opens Celeste's pause menu (as losing focus does on PC) and suspends the FMOD mixer.
	/// On return the mixer resumes and the game stays paused.
	/// </summary>
	public static unsafe class PortLifecycle
	{
		private static SDL.SDL_EventFilter? watch; // kept alive for the native side
		private static bool suspended;
		private static bool pausePending;
		private static object? fmodLowLevel;

		public static void Install()
		{
			if (!HostConfig.IsAndroid || watch != null)
			{
				return;
			}
			watch = OnEvent;
			SDL.SDL_AddEventWatch(watch, IntPtr.Zero);
			Log("lifecycle watch installed");
		}

		private static bool OnEvent(IntPtr userdata, SDL.SDL_Event* evt)
		{
			try
			{
				switch ((SDL.SDL_EventType)evt->type)
				{
					case SDL.SDL_EventType.SDL_EVENT_WILL_ENTER_BACKGROUND:
						EnterBackground();
						break;
					case SDL.SDL_EventType.SDL_EVENT_DID_ENTER_FOREGROUND:
						EnterForeground();
						break;
				}
			}
			catch (Exception e)
			{
				Log("lifecycle handler failed: " + e);
			}
			return true;
		}

		private static void EnterBackground()
		{
			Log("lifecycle: entering background");
			PauseLevel();
			SuspendMixer(true);
		}

		private static void EnterForeground()
		{
			Log("lifecycle: back in foreground");
			SuspendMixer(false);
		}

		/// <summary>Called every update: retries a pause that could not happen yet (transition, death...).</summary>
		public static void Tick()
		{
			if (pausePending)
			{
				PauseLevel();
			}
			PortExit.Tick();
		}

		private static void PauseLevel()
		{
			if (Engine.Scene is not Level level)
			{
				pausePending = false;
				return;
			}
			if (level.Paused)
			{
				pausePending = false;
			}
			else if (level.CanPause)
			{
				level.Pause();
				pausePending = false;
				Log("lifecycle: level paused");
			}
			else
			{
				pausePending = true;
			}
		}

		private static void SuspendMixer(bool suspend)
		{
			if (suspended == suspend)
			{
				return;
			}
			object? system = GetLowLevelSystem();
			if (system == null)
			{
				return;
			}
			object? result = system.GetType().GetMethod(suspend ? "mixerSuspend" : "mixerResume", Type.EmptyTypes)!.Invoke(system, null);
			suspended = suspend;
			Log($"lifecycle: FMOD mixer {(suspend ? "suspended" : "resumed")} ({result})");
		}

		private static object? GetLowLevelSystem()
		{
			if (fmodLowLevel != null)
			{
				return fmodLowLevel;
			}
			FieldInfo? field = typeof(Audio).GetField("system", BindingFlags.NonPublic | BindingFlags.Static);
			if (field?.GetValue(null) is not FMOD.Studio.System studio)
			{
				return null;
			}
			studio.getLowLevelSystem(out FMOD.System low);
			return fmodLowLevel = low;
		}

		private static void Log(string message) => Console.WriteLine("[CelesteAndroid] " + message);
	}
}

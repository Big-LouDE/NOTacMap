using System;
using System.Collections.Generic;
using System.Globalization;
using Rewired;
using Rewired.ControllerExtensions;
using UnityEngine;

namespace NOTacMap
{
	// Swipes and left/right presses on the touchpad of a PlayStation controller (PS4 or PS5), read through the game's own
	// input library while the game sees the controller itself (Steam Input off for the game). They work like extra buttons:
	// the Set rows in F1 take them, and a binding looks like "touch|<controller model>|swipeUp|<controller name>|Swipe up".
	internal static class Touchpad
	{
		internal static readonly string[] Gestures = { "swipeUp", "swipeDown", "swipeLeft", "swipeRight", "pressLeft", "pressRight" };
		internal static readonly string[] GestureNames = { "Swipe up", "Swipe down", "Swipe left", "Swipe right", "Touchpad left press", "Touchpad right press" };

		private const float MinSwipe = 0.25f;   // part of the pad the finger has to travel
		private const float MaxSwipeTime = 0.8f; // seconds from touch to lift

		private class Track
		{
			public bool touching;
			public Vector2 start, last;
			public float startTime;
		}

		private static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
		// What happened this frame, per controller (by its id in the input library): the gesture names.
		private static readonly List<KeyValuePair<Joystick, string>> thisFrame = new List<KeyValuePair<Joystick, string>>();
		private static int frame = -1;
		private static int loggedTouches;

		// Called once per frame before anything asks for gestures.
		internal static void Update()
		{
			if (frame == Time.frameCount)
			{
				return;
			}
			frame = Time.frameCount;
			thisFrame.Clear();
			if (!ReInput.isReady)
			{
				return;
			}
			foreach (Joystick j in ReInput.controllers.Joysticks)
			{
				if (j == null || !j.isConnected) continue;
				IDualShock4Extension pad = j.GetExtension<IDualShock4Extension>();
				if (pad == null) continue;
				if (!tracks.TryGetValue(j.id, out Track t)) tracks[j.id] = t = new Track();
				bool touching = pad.IsTouching(0) && pad.GetTouchPosition(0, out Vector2 pos) ? SetPos(t, pos) : false;
				if (touching && !t.touching)
				{
					t.start = t.last;
					t.startTime = Time.unscaledTime;
				}
				if (!touching && t.touching)
				{
					Vector2 d = t.last - t.start;
					float time = Time.unscaledTime - t.startTime;
					if (loggedTouches < 6)
					{
						loggedTouches++; // a few raw values, so the direction of the pad's numbers shows in the log
						Plugin.Log.LogInfo("NOTacMap touchpad: touch from " + Fmt(t.start) + " to " + Fmt(t.last) + " in " + time.ToString("0.00", CultureInfo.InvariantCulture) + " s");
					}
					if (time <= MaxSwipeTime && d.magnitude >= MinSwipe)
					{
						string g = Mathf.Abs(d.y) >= Mathf.Abs(d.x) ? (d.y > 0 ? "swipeUp" : "swipeDown") : (d.x > 0 ? "swipeRight" : "swipeLeft");
						thisFrame.Add(new KeyValuePair<Joystick, string>(j, g));
					}
				}
				t.touching = touching;
				// a press of the pad, told apart by where the finger is
				int button = TouchpadButton(j);
				if (button >= 0 && j.GetButtonDown(button) && touching)
				{
					thisFrame.Add(new KeyValuePair<Joystick, string>(j, t.last.x < 0.5f ? "pressLeft" : "pressRight"));
				}
			}
		}

		private static bool SetPos(Track t, Vector2 pos)
		{
			t.last = pos;
			return true;
		}

		private static string Fmt(Vector2 v) => "(" + v.x.ToString("0.00", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.00", CultureInfo.InvariantCulture) + ")";

		private static int TouchpadButton(Joystick j)
		{
			try
			{
				for (int b = 0; b < j.buttonCount; b++)
				{
					string n = j.ButtonElementIdentifiers[b].name;
					if (n != null && n.IndexOf("Touchpad", StringComparison.OrdinalIgnoreCase) >= 0) return b;
				}
			}
			catch (Exception)
			{
				// no names on this controller
			}
			return -1;
		}

		// For Set: the first gesture this frame, if any.
		internal static bool Any(out Joystick joystick, out string gesture)
		{
			Update();
			if (thisFrame.Count > 0)
			{
				joystick = thisFrame[0].Key;
				gesture = thisFrame[0].Value;
				return true;
			}
			joystick = null;
			gesture = null;
			return false;
		}

		// For a saved binding: did this gesture happen this frame on a controller of this model.
		internal static bool Happened(string model, string gesture)
		{
			Update();
			foreach (var e in thisFrame)
			{
				if (e.Value == gesture && e.Key.hardwareTypeGuid.ToString() == model) return true;
			}
			return false;
		}

		internal static string Name(string gesture)
		{
			int i = Array.IndexOf(Gestures, gesture);
			return i >= 0 ? GestureNames[i] : gesture;
		}
	}
}

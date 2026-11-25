using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using Rewired;
using UnityEngine;

namespace NOTacMap
{
	// The map's own controls: Combat or All, zoom in, zoom out. Each can be a keyboard key or a button on any controller the
	// game knows (HOTAS, gamepad), read through the game's own input library (Rewired), so no tool like JoyToKey is needed.
	// The map page sets them: DISPLAY > Keys > Set, then you press the key or button you want. Presses are counted, and the
	// counts go out with every snapshot (see Plugin).
	internal sealed class MapControls
	{
		internal static readonly string[] Actions = { "combat", "zoomIn", "zoomOut" };
		private static readonly string[] ActionNames = { "Combat or All", "Zoom in", "Zoom out" };
		private const float LearnSeconds = 15f;

		private readonly ConfigEntry<KeyboardShortcut>[] keys = new ConfigEntry<KeyboardShortcut>[3];
		private readonly ConfigEntry<string>[] buttons = new ConfigEntry<string>[3];
		private readonly int[] presses = new int[3];
		private readonly ConfigFile config;

		// Written by the web server thread, picked up in Update on the main thread.
		private volatile string pendingCommand, pendingAction;
		private int learning = -1;
		private float learnUntil;
		private string lastResult;
		private volatile string stateJson = "{}";

		private static KeyCode[] keyboardKeys;

		internal MapControls(ConfigFile config)
		{
			this.config = config;
			const string keyNote = " Easiest to set from the map: DISPLAY > Keys > Set, then press the key or button you want. Pick one the game does not use. Nothing happens while you type in the chat. Every map that has DISPLAY > Follow the keys on reacts.";
			keys[0] = config.Bind("Keys", "CombatOrAll", KeyboardShortcut.Empty, "Keyboard key that switches the map between your Combat view and All. From any other preset it goes to the Combat view you used last." + keyNote);
			keys[1] = config.Bind("Keys", "ZoomIn", KeyboardShortcut.Empty, "Keyboard key that zooms the map in one step." + keyNote);
			keys[2] = config.Bind("Keys", "ZoomOut", KeyboardShortcut.Empty, "Keyboard key that zooms the map out one step." + keyNote);
			const string buttonNote = " A button on a joystick, HOTAS or gamepad, written by the map page (DISPLAY > Keys > Set). Leave it to the page; empty means none.";
			buttons[0] = config.Bind("Keys", "CombatOrAllButton", "", "Controller button for Combat or All." + buttonNote);
			buttons[1] = config.Bind("Keys", "ZoomInButton", "", "Controller button for zoom in." + buttonNote);
			buttons[2] = config.Bind("Keys", "ZoomOutButton", "", "Controller button for zoom out." + buttonNote);
			BuildState();
		}

		internal KeyPresses Presses()
		{
			if (presses[0] + presses[1] + presses[2] == 0)
			{
				return null;
			}
			return new KeyPresses { combat = presses[0], zoomIn = presses[1], zoomOut = presses[2] };
		}

		// Web server thread.
		internal string StateJson() => stateJson;

		internal string Command(string command, string action)
		{
			if (command != "cancel" && Array.IndexOf(Actions, action) < 0)
			{
				return "Unknown map control.";
			}
			pendingAction = action;
			pendingCommand = command;
			return null;
		}

		// Main thread, every frame.
		internal void Update(bool typing)
		{
			string command = pendingCommand;
			if (command != null)
			{
				pendingCommand = null;
				int i = Array.IndexOf(Actions, pendingAction);
				if (command == "learn" && i >= 0)
				{
					learning = i;
					learnUntil = Time.unscaledTime + LearnSeconds;
					lastResult = null;
				}
				else if (command == "clear" && i >= 0)
				{
					keys[i].Value = KeyboardShortcut.Empty;
					buttons[i].Value = "";
					config.Save();
					lastResult = ActionNames[i] + ": cleared.";
					if (learning == i) learning = -1;
				}
				else if (command == "cancel")
				{
					learning = -1;
					lastResult = null;
				}
				BuildState();
			}

			if (learning >= 0)
			{
				Learn();
				return; // the press that sets a control doesn't also count as a press
			}
			if (typing)
			{
				return;
			}
			for (int i = 0; i < 3; i++)
			{
				if (keys[i].Value.IsDown() || ButtonDown(buttons[i].Value))
				{
					presses[i]++;
				}
			}
		}

		private void Learn()
		{
			if (Time.unscaledTime > learnUntil || Input.GetKeyDown(KeyCode.Escape))
			{
				lastResult = Time.unscaledTime > learnUntil ? "Nothing was pressed, nothing changed." : "Cancelled.";
				learning = -1;
				BuildState();
				return;
			}
			int i = learning;
			// a controller button
			if (ReInput.isReady)
			{
				foreach (Joystick j in ReInput.controllers.Joysticks)
				{
					if (j == null || !j.isConnected) continue;
					for (int b = 0; b < j.buttonCount; b++)
					{
						if (!j.GetButtonDown(b)) continue;
						string buttonName = ButtonName(j, b);
						buttons[i].Value = string.Join("|", j.hardwareTypeGuid.ToString(), b.ToString(CultureInfo.InvariantCulture), Clean(j.name), Clean(buttonName));
						keys[i].Value = KeyboardShortcut.Empty;
						Done(i, Clean(j.name) + ", " + buttonName);
						return;
					}
				}
			}
			// a keyboard key, with Ctrl, Alt or Shift if held
			foreach (KeyCode k in KeyboardKeys())
			{
				if (!Input.GetKeyDown(k)) continue;
				var mods = new List<KeyCode>();
				foreach (KeyCode m in new[] { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftShift, KeyCode.RightShift })
				{
					if (Input.GetKey(m)) mods.Add(m);
				}
				var shortcut = new KeyboardShortcut(k, mods.ToArray());
				keys[i].Value = shortcut;
				buttons[i].Value = "";
				Done(i, shortcut.ToString());
				return;
			}
		}

		private void Done(int i, string what)
		{
			config.Save();
			lastResult = ActionNames[i] + " is now " + what + ".";
			learning = -1;
			BuildState();
		}

		// "guid|button|controller name|button name". Matched by the controller model, so it survives replugging.
		private static bool ButtonDown(string binding)
		{
			if (string.IsNullOrEmpty(binding) || !ReInput.isReady)
			{
				return false;
			}
			string[] parts = binding.Split('|');
			if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int button))
			{
				return false;
			}
			foreach (Joystick j in ReInput.controllers.Joysticks)
			{
				if (j != null && j.isConnected && j.hardwareTypeGuid.ToString() == parts[0] && button < j.buttonCount && j.GetButtonDown(button))
				{
					return true;
				}
			}
			return false;
		}

		private static string ButtonName(Joystick j, int b)
		{
			try
			{
				string n = j.ButtonElementIdentifiers[b].name;
				if (!string.IsNullOrEmpty(n)) return n;
			}
			catch (Exception)
			{
				// some controllers have no names for their buttons
			}
			return "button " + (b + 1).ToString(CultureInfo.InvariantCulture);
		}

		private static string Clean(string s) => (s ?? "").Replace("|", "/").Trim();

		private static KeyCode[] KeyboardKeys()
		{
			if (keyboardKeys == null)
			{
				var list = new List<KeyCode>();
				foreach (KeyCode k in (KeyCode[])Enum.GetValues(typeof(KeyCode)))
				{
					string n = k.ToString();
					if (k == KeyCode.None || k == KeyCode.Escape || n.StartsWith("Mouse", StringComparison.Ordinal) || n.StartsWith("Joystick", StringComparison.Ordinal)
						|| n.EndsWith("Control", StringComparison.Ordinal) || n.EndsWith("Alt", StringComparison.Ordinal) || n.EndsWith("Shift", StringComparison.Ordinal)
						|| n.EndsWith("Command", StringComparison.Ordinal) || n.EndsWith("Windows", StringComparison.Ordinal) || n.EndsWith("Apple", StringComparison.Ordinal))
					{
						continue;
					}
					list.Add(k);
				}
				keyboardKeys = list.ToArray();
			}
			return keyboardKeys;
		}

		// {"learning": "combat" | null, "secondsLeft": 12, "result": "...", "controls": [{"action", "name", "key", "button"}]}
		private void BuildState()
		{
			var sb = new StringBuilder("{\"learning\":");
			sb.Append(learning >= 0 ? Newtonsoft.Json.JsonConvert.ToString(Actions[learning]) : "null");
			sb.Append(",\"seconds\":").Append(((int)LearnSeconds).ToString(CultureInfo.InvariantCulture));
			if (lastResult != null) sb.Append(",\"result\":").Append(Newtonsoft.Json.JsonConvert.ToString(lastResult));
			sb.Append(",\"controls\":[");
			for (int i = 0; i < 3; i++)
			{
				if (i > 0) sb.Append(',');
				KeyboardShortcut k = keys[i].Value;
				string key = k.MainKey == KeyCode.None ? "" : k.ToString();
				string[] parts = (buttons[i].Value ?? "").Split('|');
				string button = parts.Length >= 4 ? parts[2] + ", " + parts[3] : "";
				sb.Append("{\"action\":").Append(Newtonsoft.Json.JsonConvert.ToString(Actions[i]))
					.Append(",\"name\":").Append(Newtonsoft.Json.JsonConvert.ToString(ActionNames[i]))
					.Append(",\"key\":").Append(Newtonsoft.Json.JsonConvert.ToString(key))
					.Append(",\"button\":").Append(Newtonsoft.Json.JsonConvert.ToString(button)).Append('}');
			}
			sb.Append("]}");
			stateJson = sb.ToString();
		}
	}
}

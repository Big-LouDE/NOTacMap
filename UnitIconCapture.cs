using System.Collections.Generic;
using UnityEngine;

namespace NOTacMap
{
	// The map icon of each kind of unit, taken from the running game (UnitDefinition.mapIcon, the picture the game's own map uses).
	// Nothing of the game's artwork is shipped with the mod: the first time a kind of unit shows up, its icon is cut out of the
	// game's own texture, kept in memory and handed to the player's own browser. Mods work the same way, any unit the plugin
	// sees brings its own icon.
	internal static class UnitIconCapture
	{
		internal class Icon
		{
			public byte[] Png;
			public bool Orient;   // the game turns this icon with the unit (UnitDefinition.mapOrient)
			public float Size;    // UnitDefinition.mapIconSize
			public string Sprite; // the name of the game's sprite, a hint for what kind of unit it is
		}

		// Read by the web server thread, written on the main thread: always under the lock.
		private static readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
		private static readonly HashSet<string> without = new HashSet<string>();   // kinds with no icon we can use
		private static readonly Queue<UnitDefinition> queue = new Queue<UnitDefinition>();
		private static readonly HashSet<string> known = new HashSet<string>();     // queued, done or without
		private const int MaxPerCall = 3;   // a mission start brings dozens of kinds at once, they are spread over a few updates
		private const int MaxSide = 128;
		private static bool errorLogged;

		// Main thread: a unit of this kind is on the map.
		internal static void Want(UnitDefinition definition)
		{
			if (definition == null || string.IsNullOrEmpty(definition.unitName))
			{
				return;
			}
			lock (icons)
			{
				if (!known.Add(definition.unitName))
				{
					return;
				}
				queue.Enqueue(definition);
			}
		}

		// A frame that is already slow (a big explosion scene, a loading hitch) gets no extra GPU work from here: the icons wait, with one
		// let through every two seconds so a machine that always runs slow still gets them in the end.
		private const float SlowFrame = 0.04f;
		private static float lastSlowCapture;

		// Main thread, once per map update: cuts out a few of the waiting icons.
		internal static void Process()
		{
			int allowed = MaxPerCall;
			if (Time.unscaledDeltaTime > SlowFrame)
			{
				if (Time.realtimeSinceStartup - lastSlowCapture < 2f)
				{
					return;
				}
				lastSlowCapture = Time.realtimeSinceStartup;
				allowed = 1;
			}
			for (int n = 0; n < allowed; n++)
			{
				UnitDefinition definition;
				lock (icons)
				{
					if (queue.Count == 0)
					{
						return;
					}
					definition = queue.Dequeue();
				}
				string key = definition != null ? definition.unitName : null;
				if (key == null)
				{
					continue;
				}
				Icon icon = null;
				try
				{
					icon = Capture(definition);
				}
				catch (System.Exception ex)
				{
					if (!errorLogged)
					{
						errorLogged = true;
						Plugin.Log.LogWarning("NOTacMap: could not take the map icon of a unit, it keeps its own symbol on the map: " + ex.Message);
					}
				}
				lock (icons)
				{
					if (icon != null)
					{
						icons[key] = icon;
					}
					else
					{
						without.Add(key);
					}
				}
			}
		}

		// Web server thread. 1 = ready, 2 = this kind has no usable icon, 0 = not taken yet.
		internal static int Lookup(string key, out Icon icon)
		{
			lock (icons)
			{
				if (icons.TryGetValue(key, out icon))
				{
					return 1;
				}
				return without.Contains(key) ? 2 : 0;
			}
		}

		private static Icon Capture(UnitDefinition definition)
		{
			Sprite sprite = definition.mapIcon;
			if (sprite == null || sprite.texture == null)
			{
				return null;
			}
			// A sprite that is turned inside its atlas is not handled, the unit keeps its own symbol.
			if (sprite.packed && sprite.packingRotation != SpritePackingRotation.None)
			{
				return null;
			}
			Texture2D source = sprite.texture;
			Rect rect = sprite.textureRect;
			if (rect.width < 1f || rect.height < 1f || source.width < 1 || source.height < 1)
			{
				return null;
			}
			float shrink = Mathf.Min(1f, MaxSide / Mathf.Max(rect.width, rect.height));
			int width = Mathf.Max(1, Mathf.RoundToInt(rect.width * shrink));
			int height = Mathf.Max(1, Mathf.RoundToInt(rect.height * shrink));

			// Through the GPU like the map picture, so it works whatever the source texture's own readability or format. The piece of
			// the atlas that belongs to this sprite is picked with the scale and offset of the blit.
			RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(source, target,
				new Vector2(rect.width / source.width, rect.height / source.height),
				new Vector2(rect.x / source.width, rect.y / source.height));
			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = target;
			var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
			readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);

			// Some of the game's icons are a white shape on black with no transparency at all. Tinted as they are they would be solid
			// squares, so for those the brightness becomes the transparency (the shape stays, the black goes).
			Color32[] pixels = readable.GetPixels32();
			bool opaque = true;
			for (int i = 0; i < pixels.Length; i++)
			{
				if (pixels[i].a < 250)
				{
					opaque = false;
					break;
				}
			}
			if (opaque)
			{
				for (int i = 0; i < pixels.Length; i++)
				{
					byte bright = (byte)Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b));
					pixels[i] = new Color32(255, 255, 255, bright);
				}
				readable.SetPixels32(pixels);
			}

			// An icon that is a solid block (nearly every pixel filled) or nearly empty is no use as a symbol, the unit keeps its own.
			int filled = 0;
			for (int i = 0; i < pixels.Length; i++)
			{
				if (pixels[i].a > 128)
				{
					filled++;
				}
			}
			if (filled > pixels.Length * 0.92f || filled < pixels.Length * 0.02f)
			{
				Object.Destroy(readable);
				return null;
			}
			readable.Apply();

			byte[] png = readable.EncodeToPNG();
			Object.Destroy(readable);
			if (png == null || png.Length == 0)
			{
				return null;
			}
			return new Icon
			{
				Png = png,
				Orient = definition.mapOrient,
				Size = definition.mapIconSize > 0f ? definition.mapIconSize : 1f,
				Sprite = sprite.name
			};
		}
	}
}

using UnityEngine;

namespace NOTacMap
{
	// Captures the loaded map (Heartland, Ignus Archipelago or any future map) from
	// the game's own MapSettings component, so there is no separate image or
	// calibration to maintain per map. MapSettings.GetTerrainColorAtCoordinate()
	// samples this texture with a centered mapping (world position normalized by
	// MapSize), so MapSize is all that is needed.
	internal static class MapImageCapture
	{
		internal static byte[] Png;
		// The game's ocean depth map (where the sea is and how deep), same area as the map picture. Null if the map has none.
		internal static byte[] WaterPng;
		internal static float MapSizeX;
		internal static float MapSizeY;
		internal static string MapName;
		// Each map sets its own grid offsets (the cockpit grid reference counts from them). 80000 is the
		// game's default and what the 81920 m maps use, a wider map has more.
		internal static int OffsetX = 80000;
		internal static int OffsetY = 80000;

		private static MapSettings lastCaptured;

		private static float nextRecheck;

		// Call this every couple of seconds from Plugin.Update(). FindObjectOfType is not cheap (about
		// 5 ms in a running mission, measured), so once a map is captured it only looks again every 30
		// seconds. A mission change destroys the old MapSettings, and then it looks on every call.
		public static void CaptureIfNeeded()
		{
			if (lastCaptured != null && Time.realtimeSinceStartup < nextRecheck)
			{
				return;
			}
			nextRecheck = Time.realtimeSinceStartup + 30f;
			MapSettings settings = Object.FindObjectOfType<MapSettings>();
			if (settings == null || settings == lastCaptured)
			{
				return;
			}
			lastCaptured = settings;

			if (settings.TerrainColorMap == null)
			{
				Plugin.Log.LogWarning("NOTacMap: active MapSettings has no TerrainColorMap, no background image this session.");
				return;
			}

			Texture2D source = settings.TerrainColorMap;

			Png = ToPng(source);
			WaterPng = settings.OceanDepthmap != null ? ToPng(settings.OceanDepthmap) : null;
			MapSizeX = settings.MapSize.x;
			MapSizeY = settings.MapSize.y;
			MapName = settings.name;
			OffsetX = settings.OffsetX;
			OffsetY = settings.OffsetY;

			Plugin.Log.LogInfo($"NOTacMap: captured map image '{MapName}' ({source.width}x{source.height}, MapSize={MapSizeX}x{MapSizeY}, grid offset {OffsetX},{OffsetY}, ocean depth map {(WaterPng == null ? "none" : settings.OceanDepthmap.width + "x" + settings.OceanDepthmap.height)})");
		}

		// Blit through a RenderTexture rather than calling GetPixels/EncodeToPNG on the source directly:
		// that would only work if the source happens to be marked readable AND in a format EncodeToPNG
		// accepts. Going through the GPU like this works regardless of the source texture's own
		// readability or compression format.
		private static byte[] ToPng(Texture2D source)
		{
			RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(source, rt);
			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = rt;

			var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
			readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
			readable.Apply();

			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(rt);

			byte[] png = readable.EncodeToPNG();
			Object.Destroy(readable);
			return png;
		}
	}
}

using UnityEngine;

namespace NOTacMap
{
	// Captures whatever map is actually loaded (Heartland, Ignus Archipelago,
	// or any future map) straight from the game's own MapSettings component,
	// instead of us maintaining a separate downloaded image + manual
	// calibration per map. MapSettings.GetTerrainColorAtCoordinate() shows
	// the game already uses a simple centered mapping (world position ->
	// normalized by MapSize, centered on origin) to sample this same
	// texture, so once we have MapSize there's nothing left to calibrate.
	internal static class MapImageCapture
	{
		internal static byte[] Png;
		internal static float MapSizeX;
		internal static float MapSizeY;
		internal static string MapName;

		private static MapSettings lastCaptured;

		// Cheap check; call this occasionally (not every frame) from Plugin.Update().
		public static void CaptureIfNeeded()
		{
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

			// Blit through a RenderTexture rather than calling GetPixels/
			// EncodeToPNG on the source directly: that would only work if the
			// source happens to be marked readable AND in a format EncodeToPNG
			// accepts. Going through the GPU like this works regardless of the
			// source texture's own readability or compression format.
			RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(source, rt);
			RenderTexture previous = RenderTexture.active;
			RenderTexture.active = rt;

			var readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
			readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
			readable.Apply();

			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(rt);

			Png = readable.EncodeToPNG();
			MapSizeX = settings.MapSize.x;
			MapSizeY = settings.MapSize.y;
			MapName = settings.name;

			Object.Destroy(readable);
			Plugin.Log.LogInfo($"NOTacMap: captured map image '{MapName}' ({source.width}x{source.height}, MapSize={MapSizeX}x{MapSizeY})");
		}
	}
}

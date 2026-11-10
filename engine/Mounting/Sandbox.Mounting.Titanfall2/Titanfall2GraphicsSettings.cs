using Sandbox.Engine.Settings;
using System.Reflection;
using System.Threading;

/// <summary>
/// Adapts applied player settings to import limits. Rendering quality (shadows, fog,
/// native water and post processing) remains owned by the engine's quality profiles.
/// Workers only read an immutable snapshot; they never read the video settings cookie.
/// </summary>
static class Titanfall2GraphicsSettings
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Graphics" );

	internal sealed record Snapshot( string TextureQuality, int TextureMaxDimension, string Source );

	static readonly int? TextureOverride = Titanfall2StreamingSettings.GetOptionalInt(
		"SBOX_TITANFALL2_TEXTURE_MAX", 64, 16384 );
	static Snapshot _current = Resolve( null, TextureOverride );
	static RenderSettings _settings;

	internal static Snapshot Current => Volatile.Read( ref _current );

	internal static Snapshot Resolve( TextureQuality? quality, int? textureOverride ) => new(
		quality?.ToString() ?? "Unavailable",
		textureOverride ?? quality switch
		{
			TextureQuality.Low => 1024,
			TextureQuality.Medium => 2048,
			TextureQuality.High => 4096,
			TextureQuality.Ultra => int.MaxValue,
			_ => 1024
		},
		textureOverride.HasValue ? "SBOX_TITANFALL2_TEXTURE_MAX" : quality.HasValue ? "Game" : "Fallback" );

	internal static void Connect() => Connect( FindRenderSettings() );

	internal static void Connect( RenderSettings settings )
	{
		Disconnect();
		_settings = settings;
		Refresh();
		if ( _settings is not null ) _settings.OnVideoSettingsChanged += Refresh;
	}

	internal static void Disconnect()
	{
		if ( _settings is not null ) _settings.OnVideoSettingsChanged -= Refresh;
		_settings = null;
	}

	static void Refresh()
	{
		var next = Resolve( _settings?.TextureQuality, TextureOverride );
		var previous = Interlocked.Exchange( ref _current, next );
		if ( previous.TextureMaxDimension != next.TextureMaxDimension )
		{
			var limit = next.TextureMaxDimension == int.MaxValue ? "source dimensions" : $"{next.TextureMaxDimension}px";
			Log.Info( $"Titanfall 2 texture quality: {next.TextureQuality}, limit {limit} ({next.Source}). "
				+ "Already loaded textures retain their resolution; leave and reload the map to apply the new import limit." );
		}
	}

	static RenderSettings FindRenderSettings()
	{
		// The public accessor is standalone-only. The menu and editor use the same engine
		// singleton, but do not expose it to mounts. Keep that compatibility access here,
		// once per mount, without adding menu/editor assembly dependencies or engine edits.
		if ( Application.IsDedicatedServer ) return null;
		try
		{
			if ( Application.RenderSettings is { } settings ) return settings;
			return typeof( RenderSettings ).GetField( "Instance", BindingFlags.Static | BindingFlags.NonPublic )
				?.GetValue( null ) as RenderSettings;
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Titanfall 2 player graphics settings unavailable; using texture fallback: {exception.Message}" );
			return null;
		}
	}
}

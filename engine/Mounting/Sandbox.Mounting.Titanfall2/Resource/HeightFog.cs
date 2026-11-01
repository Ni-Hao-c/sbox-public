using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Titanfall2;

/// <summary>Restores the map's height-dependent, directional fog for each scene view.</summary>
[Library, Hide]
public sealed class Titanfall2HeightFog : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, Sandbox.Internal.IPreRenderSubscriber
{
	[Property, Hide] public string SourceName { get; set; }
	[Property, Hide] public Vector4 Density { get; set; }
	[Property, Hide] public Vector4 FogColor { get; set; }
	[Property, Hide] public Vector4 DirectionalColor { get; set; }
	[Property, Hide] public Vector4 Direction { get; set; }
	[Property, Hide] public Vector4 Height { get; set; }
	[Property, Hide] public string EncodedEnvironment { get; set; }
	Titanfall2FogEnvironment _environment;
	IReadOnlyDictionary<string, string> _current;
	bool _hasSelection;

	internal void ConfigureEnvironment( IReadOnlyList<Dictionary<string, string>> entities )
	{
		EncodedEnvironment = JsonSerializer.Serialize( entities );
		_environment = new Titanfall2FogEnvironment( entities );
		_hasSelection = false;
		SelectForView();
		Apply();
	}

	internal void SelectForView()
	{
		if ( _environment is null && !string.IsNullOrEmpty( EncodedEnvironment ) )
			_environment = new Titanfall2FogEnvironment( JsonSerializer.Deserialize<Dictionary<string, string>[]>( EncodedEnvironment ) ?? [] );
		if ( _environment is null ) return;
		var camera = Scene?.Camera;
		var position = camera.IsValid() ? camera.View.Position : Vector3.Zero;
		var selected = _environment.Select( new System.Numerics.Vector3( position.x, position.y, position.z ) );
		if ( _hasSelection && ReferenceEquals( selected, _current ) ) return;
		_hasSelection = true;
		_current = selected;
		SourceName = selected?.GetValueOrDefault( "targetname", "env_fog_controller" );
		SetParameters( selected is null ? default : Titanfall2FogParameters.FromEntity( selected ) );
	}

	internal void Configure( IReadOnlyDictionary<string, string> entity )
	{
		EncodedEnvironment = null;
		_environment = null;
		_current = null;
		_hasSelection = false;
		SourceName = entity.GetValueOrDefault( "targetname", "env_fog_controller" );
		SetParameters( Titanfall2FogParameters.FromEntity( entity ) );
		Apply();
	}

	internal void SetParameters( Titanfall2FogParameters value )
	{
		Density = value.Density;
		FogColor = value.Color;
		DirectionalColor = value.DirectionalColor;
		Direction = value.Direction;
		Height = value.Height;
	}

	internal Titanfall2FogParameters Parameters => new( Density, FogColor, DirectionalColor, Direction, Height );
	protected override void OnEnabled() { SelectForView(); Apply(); }
	protected override void OnPreRender() { SelectForView(); Apply(); }
	protected override void OnDisabled() => default( Titanfall2FogParameters ).Apply( Scene?.RenderAttributes );
	void Apply() => Parameters.Apply( Scene?.RenderAttributes );
}

[StructLayout( LayoutKind.Sequential )]
readonly record struct Titanfall2FogParameters( Vector4 Density, Vector4 Color, Vector4 DirectionalColor, Vector4 Direction, Vector4 Height )
{
	internal void Apply( RenderAttributes attributes )
	{
		if ( attributes is null ) return;
		attributes.SetData( "ImportedHeightFogConstants", this );
		// Tells engine sprite batches the scene supplies the buffer; without the flag they
		// bind neutral zeros so the shader never reads an unbound buffer.
		attributes.Set( "ImportedHeightFogBound", true );
	}

	internal static Titanfall2FogParameters FromEntity( IReadOnlyDictionary<string, string> entity )
	{
		float Number( string name, float fallback ) => entity.TryGetValue( name, out var text )
			&& float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value ) && float.IsFinite( value ) ? value : fallback;
		Vector3 Vector( string name, Vector3 fallback )
		{
			if ( !entity.TryGetValue( name, out var text ) ) return fallback;
			var values = text.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
			if ( values.Length < 3 ) return fallback;
			var result = new float[3];
			for ( var i = 0; i < 3; i++ )
				if ( !float.TryParse( values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i] ) || !float.IsFinite( result[i] ) ) return fallback;
			return new Vector3( result[0], result[1], result[2] );
		}
		static float Linear( float value ) => value <= 0.04045f ? value / 12.92f : MathF.Pow( (value + 0.055f) / 1.055f, 2.4f );
		static Vector3 LinearColor( Vector3 value ) => new( Linear( Math.Clamp( value.x / 255f, 0, 1 ) ),
			Linear( Math.Clamp( value.y / 255f, 0, 1 ) ), Linear( Math.Clamp( value.z / 255f, 0, 1 ) ) );
		var bottom = Number( "fogzbottom", 0 );
		var top = MathF.Max( bottom + 0.001f, Number( "fogztop", bottom + 1 ) );
		var bottomDensity = 1f / MathF.Max( 0.001f, Number( "foghalfdistbottom", Number( "fogend", 6000 ) ) );
		var topDensity = 1f / MathF.Max( 0.001f, Number( "foghalfdisttop", Number( "fogend", 6000 ) ) );
		var color = LinearColor( Vector( "fogcolor", new Vector3( 255 ) ) ) * MathF.Max( 0, Number( "fogcolorstrength", 1 ) );
		var directional = LinearColor( Vector( "fogdircolor", Vector3.Zero ) ) * MathF.Max( 0, Number( "fogdircolorstrength", 0 ) );
		var angles = Number( "use_angles", 0 ) != 0 ? Vector( "angles", Vector3.Zero ) : Vector( "fogangles", Vector3.Zero );
		// Entity angles point along the incoming light. Inscattering peaks
		// when looking towards its source, opposite that forward vector.
		var direction = -new Angles( angles.x, angles.y, angles.z ).Forward;
		var cosine = MathF.Cos( Math.Clamp( Number( "fogdirhalfangle", 90 ), 0.01f, 180 ) * MathF.PI / 180 );
		var opacity = Math.Clamp( Number( "fogdensity", Number( "fogmaxdensity", 1 ) ), 0, 1 );
		if ( entity.TryGetValue( "fogenable", out var enabled ) && (enabled == "0" || enabled.Equals( "false", StringComparison.OrdinalIgnoreCase )) ) opacity = 0;
		return new( new Vector4( bottomDensity, topDensity, topDensity - bottomDensity, opacity ),
			new Vector4( color, -MathF.Max( 0, Number( "fogdistoffset", Number( "fogstart", 0 ) ) ) ),
			new Vector4( directional, cosine ), new Vector4( direction, 1f / MathF.Max( 0.00001f, 1 - cosine ) ),
			new Vector4( bottom, top, 1f / (top - bottom), 0 ) );
	}
}

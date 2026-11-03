using Titanfall2;

partial class MapLoader
{
	// Multiplier for authored _ambient sky colour when no compiled sky light exists.
	const float AmbientScale = 1.35f;

	void CreateEnvironment( Titanfall2BspReader.ParsedBsp parsed )
	{
		var entities = ReadEntities( _environmentEntitySource );
		// Repacks that omit the environment partition still ship the rBSP entity lump.
		if ( entities.Count == 0 ) entities = parsed.Entities;
		var lightValues = Titanfall2LightEnvironment.Default( entities );

		var gameObject = new GameObject( true, "titanfall2_environment_light" );
		gameObject.WorldRotation = new Angles( 50f, -35f, 0f );
		if ( lightValues is not null
			&& lightValues.TryGetValue( "angles", out var angleText )
			&& TryParseVector3( angleText, out var angles ) )
			gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );

		var light = gameObject.AddComponent<DirectionalLight>();
		light.LightColor = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_light" ), 0.78f,
			out var lightColor ) ? lightColor : new Color( 0.78f, 0.74f, 0.68f );
		light.SkyColor = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_ambient" ), 0.30f,
			out var skyColor ) ? skyColor : new Color( 0.22f, 0.28f, 0.36f );
		// Without baked lightmaps the ambient term is the only fill light on
		// surfaces the sun does not reach. The raw _ambient value reads as too dark without
		// Titanfall's exposure, hence the explicit multiplier.
		light.SkyColor *= AmbientScale;
		if ( Titanfall2StreamingSettings.UseBspLightmaps )
		{
			// WORLD_LIGHTS contains the compiler's linear HDR values. Entity RGB
			// and brightness are encoded inputs and must not be limited as LDR fill.
			var sun = SelectBakedEnvironmentLight( parsed, lightValues, Titanfall2WorldLight.SunType );
			if ( sun is not null )
			{
				light.LightColor = new Color( sun.Intensity.X, sun.Intensity.Y, sun.Intensity.Z );
				if ( sun.Direction.LengthSquared() > 0.00001f ) gameObject.WorldRotation = Rotation.LookAt( ToSandbox( sun.Direction ) );
			}
			light.SkyColor = ReadBakedSkyColor( parsed );
			var definitions = entities.Where( e => e.GetValueOrDefault( "classname" ) is "light_environment" or "light_environment_volume" ).ToArray();
			var states = new List<float[]>();
			foreach ( var definition in definitions.Where( e => e.GetValueOrDefault( "classname" ) == "light_environment" ) )
			{
				var index = Titanfall2LightEnvironment.Index( definition );
				var sourceSun = SelectBakedEnvironmentLight( parsed, definition, Titanfall2WorldLight.SunType );
				var sourceSky = SelectBakedEnvironmentLight( parsed, definition, Titanfall2WorldLight.SkyAmbientType );
				if ( index < 0 || sourceSun is null || sourceSky is null ) continue;
				states.Add( [index, sourceSun.Intensity.X, sourceSun.Intensity.Y, sourceSun.Intensity.Z,
					sourceSun.Direction.X, sourceSun.Direction.Y, sourceSun.Direction.Z,
					sourceSky.Intensity.X, sourceSky.Intensity.Y, sourceSky.Intensity.Z] );
			}
			if ( states.Count > 0 ) gameObject.AddComponent<Titanfall2EnvironmentController>().Configure( definitions, states.ToArray() );
		}
		light.ShadowCascadeCount = Titanfall2StreamingSettings.ShadowCascadeCount;
		light.ContactShadows = Titanfall2StreamingSettings.NativeContactShadows;

		var fogEntities = ReferenceEquals( entities, parsed.Entities ) ? entities.ToArray()
			: entities.Concat( parsed.Entities.Where( e => e.GetValueOrDefault( "classname" ) == "fog_volume" ) ).ToArray();
		var fogValues = new Titanfall2FogEnvironment( fogEntities ).Select( System.Numerics.Vector3.Zero );
		if ( fogValues is not null )
		{
			var fogObject = new GameObject( true, "titanfall2_environment_fog" );
			fogObject.AddComponent<Titanfall2HeightFog>().ConfigureEnvironment( fogEntities );
			Log.Info( $"Titanfall 2 height/directional fog: {fogValues.GetValueOrDefault( "targetname" )} ({Path})." );
		}

		// Post-process effects only apply on a camera or a volume. A standalone
		// root effect is ignored by PostProcessSystem. Keep this map baseline
		// below game-authored volumes. Retail exposure owns its camera history.
		var postProcess = new GameObject( true, "titanfall2_map_display" );
		var volume = postProcess.AddComponent<PostProcessVolume>();
		volume.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Infinite };
		volume.Priority = -100;
		if ( Titanfall2StreamingSettings.UseBspLightmaps )
		{
			var tone = postProcess.AddComponent<Titanfall2Tonemapping>();
			// Retail display and exposure are distinct from Source 2's Hable.
			// Defaults come from the original client cvars; script overrides
			// and local controller volumes still need their own entity/IO path.
			tone.ExposureCompensation = Titanfall2StreamingSettings.BakedExposureCompensation;
		}
		// Global enabled correction entities are explicit authored map data.
		// Local volumes and scripted fades need their own spatial/IO handling.
		var correction = entities.FirstOrDefault( values => values.GetValueOrDefault( "classname" ) == "color_correction"
			&& !IsEntityTrue( values.GetValueOrDefault( "StartDisabled" ) )
			&& GetEntityFloat( values, "minfalloff", -1 ) < 0 && GetEntityFloat( values, "maxfalloff", -1 ) < 0 );
		if ( correction?.TryGetValue( "filename", out var lookupPath ) == true )
		{
			// The table is mounted as its own volume texture, so the scene stores the
			// resource path instead of the 98,304 bytes of samples.
			var weight = GetEntityFloat( correction, "maxweight", 1 );
			var gradingPath = $"mount://{Host.Ident}/{lookupPath.Replace( '\\', '/' ).TrimStart( '/' )}.vtex";
			postProcess.AddComponent<Titanfall2ColorCorrection>().Configure( gradingPath, lookupPath, weight );
			Log.Info( $"Titanfall 2 map color correction: {lookupPath}, weight {weight} ({Path})." );
		}

		Log.Info( $"Titanfall 2 environment created from map entities: "
			+ $"{(lightValues is null ? "fallback light" : "light_environment")}, "
			+ $"{(fogValues is null ? "no fog entity" : "env_fog_controller")}, "
			+ $"{light.ShadowCascadeCount} shadow cascade(s), contact shadows {light.ContactShadows} ({Path})." );
	}

	/// <summary>
	/// The rBSP ENTITIES lump is the map's own entity source. Retail keeps most authored
	/// entities in the external partitions, so this lump is small on MP maps but always
	/// contains worldspawn plus volume entities the partition files do not repeat.
	/// </summary>
	void LogBspEntities( Titanfall2BspReader.ParsedBsp parsed )
	{
		if ( parsed.Entities.Count == 0 && parsed.EntityPartitions.Count == 0 ) return;

		var groups = parsed.Entities
			.Select( static values => values.GetValueOrDefault( "classname", "unknown" ) )
			.GroupBy( static name => name, StringComparer.OrdinalIgnoreCase )
			.OrderByDescending( static group => group.Count() )
			.Take( 6 )
			.Select( static group => $"{group.Key} {group.Count()}" );
		var worldspawn = parsed.Entities.FirstOrDefault( static values =>
			values.TryGetValue( "classname", out var className ) && className.Equals( "worldspawn", StringComparison.OrdinalIgnoreCase ) );
		var worldspawnText = worldspawn is null
			? "no worldspawn"
			: $"worldspawn mapversion {worldspawn.GetValueOrDefault( "mapversion", "?" )}";
		var partitions = parsed.EntityPartitions.Count > 0
			? string.Join( "/", parsed.EntityPartitions )
			: "none declared";

		Log.Info( $"Titanfall 2 BSP entities: {Path} ({parsed.Entities.Count} entities: {string.Join( ", ", groups )}; "
			+ $"{worldspawnText}; partitions {partitions})." );
	}

	static float EntityDistanceSquared( IReadOnlyDictionary<string, string> values, Vector3 center )
	{
		return values.TryGetValue( "origin", out var originText ) && TryParseVector3( originText, out var origin )
			? origin.DistanceSquared( center )
			: float.MaxValue;
	}

	static Titanfall2WorldLight SelectBakedEnvironmentLight( Titanfall2BspReader.ParsedBsp parsed,
		IReadOnlyDictionary<string, string> entity, int type )
	{
		var position = (ToSandbox( parsed.World.Mins ) + ToSandbox( parsed.World.Maxs )) * 0.5f;
		if ( entity is not null && entity.TryGetValue( "origin", out var origin ) && TryParseVector3( origin, out var authored ) ) position = authored;
		return parsed.WorldLights.Where( light => light.Type == type
			&& float.IsFinite( light.Intensity.X ) && float.IsFinite( light.Intensity.Y ) && float.IsFinite( light.Intensity.Z )
			&& light.Intensity.X >= 0 && light.Intensity.Y >= 0 && light.Intensity.Z >= 0 )
			.OrderBy( light => ToSandbox( light.Origin ).DistanceSquared( position ) ).FirstOrDefault();
	}

	static float GetEntityFloat( IReadOnlyDictionary<string, string> values, string key, float fallback )
		=> values.TryGetValue( key, out var text ) && TryParseFloat( text, out var value ) ? value : fallback;

	static Titanfall2SkyboxFog ResolveSkyboxFog(
		IReadOnlyDictionary<string, string> cameraValues,
		IReadOnlyList<Dictionary<string, string>> environmentEntities )
	{
		IReadOnlyDictionary<string, string> fogValues = cameraValues;
		var useWorldFog = cameraValues.TryGetValue( "useworldfog", out var worldFogText ) && IsEntityTrue( worldFogText );
		if ( useWorldFog )
		{
			fogValues = new Titanfall2FogEnvironment( environmentEntities ).Select( System.Numerics.Vector3.Zero ) ?? cameraValues;
		}

		if ( fogValues.TryGetValue( "fogenable", out var fogEnabled ) && IsEntityFalse( fogEnabled ) )
			return Titanfall2SkyboxFog.Disabled with { UseWorldFog = useWorldFog };

		var opacity = Math.Clamp( GetEntityFloat( fogValues, "fogdensity",
			GetEntityFloat( fogValues, "fogmaxdensity", 0.25f ) ), 0f, 0.65f );
		if ( opacity <= 0.001f ) return Titanfall2SkyboxFog.Disabled with { UseWorldFog = useWorldFog };

		return new Titanfall2SkyboxFog( Titanfall2FogParameters.FromEntity( fogValues ), useWorldFog );
	}

	static bool TryParseSourceLightColor( string value, float maximumIntensity, out Color result )
	{
		result = default;
		if ( !TryParseFloatComponents( value, out var components ) || components.Length < 3 ) return false;
		var authoredIntensity = components.Length >= 4 ? components[3] : 255f;
		var intensity = Math.Clamp( authoredIntensity / 1020f, maximumIntensity * 0.35f, maximumIntensity );
		result = new Color(
			Math.Clamp( components[0] / 255f, 0f, 1f ) * intensity,
			Math.Clamp( components[1] / 255f, 0f, 1f ) * intensity,
			Math.Clamp( components[2] / 255f, 0f, 1f ) * intensity );
		return true;
	}

	static bool TryParseFloatComponents( string value, out float[] components )
	{
		components = Array.Empty<float>();
		if ( string.IsNullOrWhiteSpace( value ) ) return false;
		var parts = value.Split( [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length == 0 ) return false;
		components = new float[parts.Length];
		for ( var index = 0; index < parts.Length; index++ )
		{
			if ( !TryParseFloat( parts[index], out components[index] ) )
			{
				components = Array.Empty<float>();
				return false;
			}
		}
		return true;
	}

	static bool IsEntityTrue( string value ) => value is not null
		&& (value == "1" || value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase ));

	static bool IsEntityFalse( string value ) => value is not null
		&& (value == "0" || value.Equals( "false", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "no", StringComparison.OrdinalIgnoreCase ));
}

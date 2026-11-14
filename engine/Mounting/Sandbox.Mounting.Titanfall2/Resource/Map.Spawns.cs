using Titanfall2;

partial class MapLoader
{
	int CreateCubemapProbes( Titanfall2BspReader.ParsedBsp parsed )
	{
		if ( Application.IsHeadless ) return 0;
		var bounds = new BBox( ToSandbox( parsed.World.Mins ), ToSandbox( parsed.World.Maxs ) );
		var center = (bounds.Mins + bounds.Maxs) * 0.5f;
		var controllerObject = new GameObject( true, "titanfall2_cubemap_controller" );
		var controller = controllerObject.AddComponent<Titanfall2CubemapController>();

		if ( parsed.Cubemaps.Count > 0 && parsed.EmbeddedCubemapVtf.Length > 0 )
		{
			if ( VtfTextureDecoder.TryCreateCubeFrames( parsed.EmbeddedCubemapVtf, out var textures, out var error ) )
			{
				var positions = parsed.Cubemaps.Select( cubemap => ToSandbox( cubemap.Origin ) ).ToArray();
				var count = controller.ConfigureCustom( textures, positions, bounds, parsed.EmbeddedCubemapVtf );
				if ( textures.Length != parsed.Cubemaps.Count )
					Log.Warning( $"Titanfall 2 cubemap frame/location mismatch: {Path} "
						+ $"({textures.Length} frames, {parsed.Cubemaps.Count} locations; using {count})." );
				Log.Info( $"Titanfall 2 embedded cubemaps loaded: {Path} ({count} probes from "
					+ $"{parsed.EmbeddedCubemapVtf.Length / (1024.0 * 1024.0):0.00} MB VTF)." );
				return count;
			}
			Log.Warning( $"Unable to decode Titanfall 2 embedded cubemaps for '{Path}': {error}" );
		}

		controller.ConfigureDelayedFallback( center, bounds );
		Log.Info( $"Titanfall 2 map has no usable embedded cubemap; a delayed render-once fallback was queued: {Path}." );
		return 1;
	}

	internal static SkyboxCandidate SelectSkybox( Titanfall2BspReader.ParsedBsp parsed,
		IReadOnlyList<Dictionary<string, string>> environmentEntities )
	{
		var vistaProps = parsed.StaticProps.Where( static prop => IsVistaModelPath( prop.ModelPath ) ).ToArray();
		if ( vistaProps.Length == 0 ) return null;

		var candidates = new List<SkyboxCandidate>();
		foreach ( var values in environmentEntities )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !className.Equals( "sky_camera", StringComparison.OrdinalIgnoreCase )
				|| !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var origin ) ) continue;

			var rotation = default( Angles );
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angleVector ) )
				rotation = new Angles( angleVector.x, angleVector.y, angleVector.z );

			// Keep the authored miniature-world scale. Model extents do not determine
			// camera parallax, and "scale" is not the same field as "skyscale".
			var skyScale = 16f;
			if ( values.TryGetValue( "skyscale", out var skyScaleText ) && TryParseFloat( skyScaleText, out var parsedSkyScale ) && parsedSkyScale > 0f )
				skyScale = parsedSkyScale;
			else if ( values.TryGetValue( "scale", out var scaleText ) && TryParseFloat( scaleText, out var parsedScale ) && parsedScale > 0f )
				skyScale = parsedScale;

			candidates.Add( new SkyboxCandidate(
				values.GetValueOrDefault( "targetname", "sky_camera" ),
				origin,
				rotation,
				Math.Clamp( skyScale, 1f, 100000f ),
				ResolveSkyboxFog( values, environmentEntities ),
				[] ) );
		}

		SkyboxCandidate selected;
		if ( candidates.Count > 0 )
		{
			// A vista need not share the camera's pivot: Eden's level vista is 152
			// units below it. Assign each vista to its nearest authored camera before
			// choosing the level group, keeping the distant intro panorama separate.
			selected = vistaProps.GroupBy( prop => candidates.MinBy( candidate => DistanceSquared( prop.Origin, candidate.Origin ) ) )
				.Select( group => group.Key with { Models = group.ToArray() } )
				.OrderByDescending( static candidate => candidate.Name.Contains( "level", StringComparison.OrdinalIgnoreCase ) )
				.ThenByDescending( static candidate => candidate.Models.Count( prop => !IsPlanetVista( prop.ModelPath ) ) )
				.ThenBy( static candidate => candidate.Name.Contains( "intro", StringComparison.OrdinalIgnoreCase ) )
				.ThenByDescending( static candidate => candidate.Models.Length )
				.First();
		}
		else
		{
			// Some community VPKs omit the environment entity partition. Vista props are
			// still authored at the sky_camera origin, so retain a deterministic fallback.
			var group = vistaProps
				.GroupBy( static prop => VistaOriginKey.FromPosition( prop.Origin ) )
				.OrderByDescending( static group => group.Count( prop => !IsPlanetVista( prop.ModelPath ) ) )
				.ThenByDescending( static group => group.Count() )
				.First();
			var models = group.ToArray();
			selected = new SkyboxCandidate(
				"inferred_sky_camera",
				ToSandbox( models[0].Origin ),
				Angles.Zero,
				1000f,
				Titanfall2SkyboxFog.Disabled,
				models );
		}
		return selected;
	}

	int CreateSkybox( Titanfall2BspReader.ParsedBsp parsed )
	{
		var selected = SelectSkybox( parsed, ReadEnvironmentEntities( parsed ) );
		if ( selected is null ) return 0;

		var skyboxModels = selected.Models.Select( static prop => new Titanfall2SkyboxModel(
			prop.ModelPath,
			ToSandbox( prop.Origin ),
			new Angles( prop.Angles.X, prop.Angles.Y, prop.Angles.Z ),
			MathF.Max( prop.Scale, 0.001f ) ) ).ToArray();
		var skyboxObject = new GameObject( true, "titanfall2_3d_skybox" );
		skyboxObject.AddComponent<Titanfall2Skybox3D>().Configure(
			Host.Ident,
			Path,
			selected.Name,
			selected.Origin,
			selected.Rotation,
			selected.Scale,
			selected.Fog,
			skyboxModels );

		Log.Info( $"Titanfall 2 3D skybox selected: {Path} ({selected.Name}, origin {selected.Origin}, "
			+ $"scale {selected.Scale:0.##}, {skyboxModels.Length} vista models)" );
		return skyboxModels.Length;
	}

	int CreateSpawnPoints( Titanfall2BspReader.ParsedBsp parsed, out Vector3 firstPosition )
	{
		var count = 0;
		firstPosition = (ToSandbox( parsed.World.Mins ) + ToSandbox( parsed.World.Maxs )) * 0.5f + Vector3.Up * 128f;
		var spawnEntities = ReadEntities( _spawnEntitySource );
		if ( spawnEntities.Count == 0 ) spawnEntities = parsed.Entities;
		foreach ( var values in spawnEntities )
		{
			if ( !values.TryGetValue( "classname", out var className ) || className is not ("info_spawnpoint_human_start" or "info_spawnpoint_human" or "info_player_start") ) continue;
			if ( !values.TryGetValue( "origin", out var originText ) || !TryParseVector3( originText, out var origin ) ) continue;

			var gameObject = new GameObject( true, values.GetValueOrDefault( "targetname", className ) );
			gameObject.WorldPosition = origin;
			if ( count == 0 ) firstPosition = origin;
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angles ) )
				gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );
			gameObject.AddComponent<SpawnPoint>();
			gameObject.Tags.Add( "spawn", "info_player_start", className );
			count++;
		}

		if ( count > 0 ) return count;
		CreateFallbackSpawn( firstPosition );
		return 1;
	}

	int CreateParticleSystems( Titanfall2BspReader.ParsedBsp parsed, Vector3 streamingAnchor )
	{
		var result = Titanfall2ParticleMapEntities.Read( ReadEntities( _particleEntitySource ), parsed.Entities,
			ReadEntities( _scriptEntitySource ), ReadEntities( _spawnEntitySource ),
			ReadEntities( _environmentEntitySource ), ReadEntities( _soundEntitySource ) );
		if ( result.SkyboxCount > 0 )
			Log.Info( $"Titanfall 2 map FX deferred {result.SkyboxCount} 3D-skybox particle systems: {Path}." );
		if ( result.UnresolvedControlPoints > 0 || result.UnresolvedParents > 0 )
			Log.Warning( $"Titanfall 2 map FX has {result.UnresolvedControlPoints} unresolved control points and "
				+ $"{result.UnresolvedParents} unresolved brush parents: {Path}." );
		if ( result.Entities.Count == 0 ) return 0;
		var streamerObject = new GameObject( true, "titanfall2_particle_streamer" );
		streamerObject.AddComponent<Titanfall2ParticleStreamer>()
			.Configure( Host.Ident, Path, streamingAnchor, result.Entities );
		return result.Entities.Count;
	}

	// Hearing radius for ambient emitters that author none, and how many play at once.
	const float MapAudioEmitterRadius = 1024f;
	const int MapAudioMaxEmitters = 12;

	int CreateMapAudio( Titanfall2BspReader.ParsedBsp parsed, Vector3 streamingAnchor )
	{
		if ( Application.IsHeadless || !Titanfall2StreamingSettings.MapAudio ) return 0;

		var entities = ReadEntities( _soundEntitySource );
		if ( entities.Count == 0 ) entities = parsed.Entities;

		var emitters = new List<Titanfall2MapAudio.Emitter>();
		var zones = new List<Titanfall2MapAudio.SoundscapeZone>();
		foreach ( var values in entities )
		{
			if ( !values.TryGetValue( "classname", out var className ) ) continue;
			if ( !values.TryGetValue( "origin", out var originText ) || !TryParseVector3( originText, out var origin ) ) continue;

			if ( className.Equals( "ambient_generic", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( values.TryGetValue( "enabled", out var enabled ) && IsEntityFalse( enabled ) ) continue;
				if ( !values.TryGetValue( "soundName", out var soundName ) || string.IsNullOrWhiteSpace( soundName ) ) continue;

				var radius = GetEntityFloat( values, "radius", 0f );
				if ( radius <= 0f ) radius = MapAudioEmitterRadius;
				// ambient_generic volume is the Source 0-10 scale (default 10).
				var volume = Math.Clamp( GetEntityFloat( values, "volume", 10f ) / 10f, 0.05f, 1f );
				emitters.Add( new Titanfall2MapAudio.Emitter( soundName.Trim(), origin, radius, volume ) );
			}
			else if ( className.Equals( "trigger_soundscape", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( values.TryGetValue( "StartDisabled", out var disabled ) && IsEntityTrue( disabled ) ) continue;
				if ( !values.TryGetValue( "play_event", out var playEvent ) || string.IsNullOrWhiteSpace( playEvent ) ) continue;

				var radius = GetEntityFloat( values, "triggerNearbyRadius", 0f );
				if ( radius <= 0f ) radius = MapAudioEmitterRadius * 2f;
				// Retail references a soundscape by name; the looping sound it declares is the
				// actual audio, and the soundscape name is used as a fallback sound name.
				var soundName = Host.ResolveSoundscapeLoop( playEvent.Trim() );
				zones.Add( new Titanfall2MapAudio.SoundscapeZone( soundName, origin, radius ) );
			}
		}

		if ( emitters.Count == 0 && zones.Count == 0 ) return 0;

		var audioObject = new GameObject( true, "titanfall2_map_audio" );
		audioObject.AddComponent<Titanfall2MapAudio>().Configure(
			Host,
			Path,
			emitters,
			zones,
			Titanfall2StreamingSettings.MapAudioVolume,
			MapAudioMaxEmitters );

		Log.Info( $"Titanfall 2 map audio created: {Path} ({emitters.Count} ambient emitters, {zones.Count} soundscape zones; "
			+ $"radius {MapAudioEmitterRadius:0}, volume {Titanfall2StreamingSettings.MapAudioVolume:0.##}, "
			+ $"up to {MapAudioMaxEmitters} concurrent emitters; "
			+ $"{Host.MilesSoundCount} bank/{Host.WavSoundCount} wav names indexed)." );
		return emitters.Count + zones.Count;
	}

	int CreateDynamicProps( GameObject world, Titanfall2BspReader.ParsedBsp parsed )
	{
		var entities = ReadEntities( _scriptEntitySource );
		if ( entities.Count == 0 ) return 0;
		var probes = Application.IsDedicatedServer ? null : parsed.LightProbes;
		var skyColor = probes is null ? default : ReadBakedSkyColor( parsed );
		var probed = 0;

		var root = new GameObject( world, true, "titanfall2_dynamic_props" );
		root.IsStatic = false;
		var created = 0;
		var failed = 0;
		var animated = 0;
		var colliders = 0;
		// Collected so the first animation update can run during loading instead of on the first
		// frame these props are visible (see WarmSkinnedAnimationFirstUpdate).
		var skinnedRenderers = new List<SkinnedModelRenderer>();
		var propsByGuid = new Dictionary<string, GameObject>( StringComparer.OrdinalIgnoreCase );
		foreach ( var values in entities )
		{
			if ( !values.TryGetValue( "classname", out var className )
				|| !(className.Equals( "prop_dynamic", StringComparison.OrdinalIgnoreCase )
					|| className.Equals( "prop_dynamic_lightweight", StringComparison.OrdinalIgnoreCase )
					|| className.Equals( "prop_dynamic_override", StringComparison.OrdinalIgnoreCase )) )
				continue;
			if ( IsEntityTrue( values.GetValueOrDefault( "StartDisabled" ) ) ) continue;
			if ( !values.TryGetValue( "model", out var modelPath )
				|| string.IsNullOrWhiteSpace( modelPath )
				|| modelPath[0] == '*' ) continue;
			if ( !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var position ) ) continue;

			var normalizedModelPath = NormalizeModelPath( modelPath );
			var resourcePath = $"mount://{Host.Ident}/{normalizedModelPath}.vmdl";
			var skin = int.TryParse( values.GetValueOrDefault( "skin" ), out var skinValue ) ? Math.Max( 0, skinValue ) : 0;
			var body = int.TryParse( values.GetValueOrDefault( "body" ), out var bodyValue ) ? Math.Max( 0, bodyValue ) : 0;
			var model = Host.LoadModelVariant( resourcePath, skin, body, Application.IsDedicatedServer );
			if ( !ModelLoader.IsUsableModel( model ) )
			{
				if ( failed++ < 12 )
					Log.Warning( $"Unable to load Titanfall 2 dynamic prop '{resourcePath}'." );
				continue;
			}

			var gameObject = new GameObject( root, true,
				values.GetValueOrDefault( "targetname", System.IO.Path.GetFileNameWithoutExtension( normalizedModelPath ) ) );
			gameObject.IsStatic = false;
			gameObject.WorldPosition = position;
			if ( values.TryGetValue( "angles", out var anglesText ) && TryParseVector3( anglesText, out var angles ) )
				gameObject.WorldRotation = new Angles( angles.x, angles.y, angles.z );
			if ( values.TryGetValue( "scale", out var scaleText ) && TryParseFloat( scaleText, out var scale ) )
				gameObject.WorldScale = Vector3.One * Math.Clamp( scale, 0.001f, 1000f );

			var disableShadows = !Titanfall2StreamingSettings.PropShadows
				|| IsEntityTrue( values.GetValueOrDefault( "disableshadows" ) );
			if ( !Application.IsDedicatedServer && (model.BoneCount > 0 || model.AnimationCount > 0) )
			{
				var renderer = gameObject.AddComponent<SkinnedModelRenderer>();
				renderer.Model = model;
				renderer.UseAnimGraph = false;
				renderer.RenderType = disableShadows
					? ModelRenderer.ShadowRenderType.Off
					: ModelRenderer.ShadowRenderType.On;
				var animation = ResolveDynamicAnimation( values, model );
				if ( !string.IsNullOrWhiteSpace( animation ) )
				{
					renderer.Sequence.Name = animation;
					renderer.Sequence.Looping = !IsEntityTrue( values.GetValueOrDefault( "HoldAnimation" ) );
					renderer.PlaybackRate = 1f;
				}
				animated++;
				skinnedRenderers.Add( renderer );
			}
			else if ( !Application.IsDedicatedServer )
			{
				var renderer = gameObject.AddComponent<ModelRenderer>();
				renderer.Model = model;
				renderer.RenderType = disableShadows
					? ModelRenderer.ShadowRenderType.Off
					: ModelRenderer.ShadowRenderType.On;
			}

			if ( !Application.IsDedicatedServer )
			{
				// Retail samples entity lighting at the bounds centre, not the (often floor) origin.
				var lightingOrigin = gameObject.WorldTransform.PointToWorld( model.Bounds.Center );
				var probe = probes?.Find( new System.Numerics.Vector3( lightingOrigin.x, lightingOrigin.y, lightingOrigin.z ) );
				gameObject.AddComponent<Titanfall2DynamicPropLighting>().Configure( probe, skyColor );
				if ( probe is not null ) probed++;
			}

			var variant = gameObject.AddComponent<Titanfall2ModelVariant>();
			variant.MountIdent = Host.Ident; variant.ModelPath = normalizedModelPath; variant.Skin = skin; variant.Body = body;
			var solid = values.TryGetValue( "solid", out var solidText )
				&& int.TryParse( solidText, out var solidType ) && solidType != 0;
			var collidable = solid
				|| IsEntityTrue( values.GetValueOrDefault( "collide_human" ) )
				|| IsEntityTrue( values.GetValueOrDefault( "collide_titan" ) );
			if ( collidable )
			{
				var collider = gameObject.AddComponent<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
				// Entities take the studio header contents directly (no static-prop expansion);
				// studiomdl writes CONTENTS_SOLID when $contents is absent, so 0 is treated as solid.
				var modelContents = model.GetData<Titanfall2ModelCollisionData>()?.ModelContents ?? 0u;
				var contents = modelContents == 0 ? (uint)Titanfall2Contents.Solid : modelContents;
				var source = gameObject.AddComponent<Titanfall2CollisionSource>();
				source.MountIdent = Host.Ident; source.MapPath = Path; source.ModelPath = normalizedModelPath;
				source.GeometrySource = model.GetData<Titanfall2ModelCollisionData>()?.GeometrySource;
				source.HasEffectiveContents = true; source.EffectiveContents = contents;
				Titanfall2CollisionQueries.Classify( gameObject, collider, contents );
				colliders++;
			}
			if ( values.TryGetValue( "link_guid", out var linkGuid ) && !string.IsNullOrWhiteSpace( linkGuid ) )
				propsByGuid.TryAdd( linkGuid.Trim(), gameObject );
			created++;
		}

		if ( created == 0 )
		{
			root.Destroy();
			return 0;
		}

		var rotators = CreateRotatingMovers( root, entities, propsByGuid );
		// Opportunistic only: the scene animation pass creates each SceneModel lazily, so at load
		// time there is usually nothing to warm yet (the log line stays quiet when it warmed none).
		var (warmedRenderers, warmupMilliseconds) = WarmSkinnedAnimationFirstUpdate( skinnedRenderers );
		var warmup = warmedRenderers > 0
			? $", {warmedRenderers} animation warmups in {warmupMilliseconds:0.00} ms"
			: string.Empty;
		Log.Info( $"Titanfall 2 scripted dynamic props created: {created} models, "
			+ $"{animated} skinned, {rotators} rotating movers, {colliders} colliders, "
			+ $"{probed} light probes, {failed} failures{warmup} ({Path})." );
		return created;
	}

	/// <summary>
	/// The scene animation pass first calls <c>SkinnedModelRenderer.AnimationUpdate</c> on the frame a
	/// renderer becomes visible, so several animated props coming into view at once each pay their
	/// one-time bone sampling and bone-buffer upload inside a single game frame. This runs that first
	/// update during loading instead, which only moves the cost: playback is paused for the warmup
	/// call (PlaybackRate is restored immediately), so the sequence keeps its position and the first
	/// real update samples exactly the same pose it would have.
	/// <para>
	/// The engine method is internal, so it is resolved by reflection once and skipped silently when
	/// the name or signature changes - a warmup is never allowed to break map loading.
	/// </para>
	/// </summary>
	static (int Renderers, double Milliseconds) WarmSkinnedAnimationFirstUpdate( List<SkinnedModelRenderer> renderers )
	{
		if ( renderers.Count == 0 ) return (0, 0);
		var update = typeof( SkinnedModelRenderer ).GetMethod( "AnimationUpdate",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic );
		if ( update is null ) return (0, 0);

		var probe = System.Diagnostics.Stopwatch.StartNew();
		var warmed = 0;
		// One reusable boxed argument array; the parameter is "previewBindPose" and false is the
		// normal (non editor preview) path.
		var arguments = new object[] { false };
		foreach ( var renderer in renderers )
		{
			if ( !renderer.IsValid() || !renderer.SceneModel.IsValid() ) continue;
			var playbackRate = renderer.PlaybackRate;
			try
			{
				renderer.PlaybackRate = 0f;
				update.Invoke( renderer, arguments );
				warmed++;
			}
			catch ( Exception exception )
			{
				Log.Warning( $"Titanfall 2 animation warmup failed: {exception.Message}" );
				break;
			}
			finally
			{
				if ( renderer.IsValid() ) renderer.PlaybackRate = playbackRate;
			}
		}
		probe.Stop();
		return (warmed, probe.Elapsed.TotalMilliseconds);
	}

	static string ResolveDynamicAnimation(
		IReadOnlyDictionary<string, string> values,
		Model model )
	{
		foreach ( var key in new[] { "DefaultAnim", "animation", "sequence" } )
		{
			if ( values.TryGetValue( key, out var explicitAnimation )
				&& !string.IsNullOrWhiteSpace( explicitAnimation ) )
				return explicitAnimation.Trim();
		}

		if ( model is null || model.AnimationCount <= 0 ) return null;
		var names = model.AnimationNames
			.Where( static name => !string.IsNullOrWhiteSpace( name )
				&& !name.Equals( "bindpose", StringComparison.OrdinalIgnoreCase ) )
			.ToArray();
		return names.FirstOrDefault( static name =>
				name.Contains( "idle", StringComparison.OrdinalIgnoreCase )
				|| name.Contains( "spin", StringComparison.OrdinalIgnoreCase )
				|| name.Contains( "loop", StringComparison.OrdinalIgnoreCase ) )
			?? names.FirstOrDefault();
	}

	static int CreateRotatingMovers(
		GameObject root,
		IReadOnlyList<Dictionary<string, string>> entities,
		IReadOnlyDictionary<string, GameObject> propsByGuid )
	{
		var created = 0;
		foreach ( var values in entities )
		{
			if ( !values.TryGetValue( "rotate_forever_speed", out var speedText )
				|| !TryParseFloat( speedText, out var speed )
				|| MathF.Abs( speed ) <= 0.001f )
				continue;

			var isRotator = values.TryGetValue( "editorclass", out var editorClass )
				&& editorClass.Equals( "script_rotator", StringComparison.OrdinalIgnoreCase );
			isRotator |= values.TryGetValue( "classname", out var className )
				&& className.Equals( "script_mover_lightweight", StringComparison.OrdinalIgnoreCase )
				&& values.ContainsKey( "rotation_axis" );
			if ( !isRotator ) continue;

			if ( !values.TryGetValue( "origin", out var originText )
				|| !TryParseVector3( originText, out var pivot ) )
				continue;

			var moverRotation = Rotation.Identity;
			if ( values.TryGetValue( "angles", out var anglesText )
				&& TryParseVector3( anglesText, out var angles ) )
				moverRotation = new Angles( angles.x, angles.y, angles.z ).ToRotation();

			var axisName = values.GetValueOrDefault( "rotation_axis", "yaw" );
			var delay = GetEntityFloat( values, "start_delay", 0f );
			foreach ( var link in values.Where( static pair =>
				pair.Key.StartsWith( "link_to_guid_", StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( string.IsNullOrWhiteSpace( link.Value )
					|| !propsByGuid.TryGetValue( link.Value.Trim(), out var target )
					|| !target.IsValid() )
					continue;

				var basis = IsEntityTrue( values.GetValueOrDefault( "use_local_rotation" ) )
					? target.WorldRotation
					: moverRotation;
				var axis = axisName.ToLowerInvariant() switch
				{
					"pitch" => basis.Right,
					"roll" => basis.Forward,
					_ => basis.Up
				};
				target.AddComponent<Titanfall2RotatingMover>()
					.Configure( pivot, axis, speed, delay );
				created++;
			}
		}
		return created;
	}

	static IReadOnlyList<Dictionary<string, string>> ReadEntities( ITitanfall2AssetSource source )
	{
		if ( source is null || !source.TryReadAllBytes( out var bytes, out _ ) || bytes.Length == 0 )
			return Array.Empty<Dictionary<string, string>>();

		return Titanfall2EntityText.Parse( bytes );
	}

	static bool TryParseVector3( string value, out Vector3 result )
	{
		result = default;
		var components = value.Split( [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries );
		if ( components.Length != 3 ) return false;
		var style = System.Globalization.NumberStyles.Float;
		var culture = System.Globalization.CultureInfo.InvariantCulture;
		if ( !float.TryParse( components[0], style, culture, out var x )
			|| !float.TryParse( components[1], style, culture, out var y )
			|| !float.TryParse( components[2], style, culture, out var z )
			|| !float.IsFinite( x ) || !float.IsFinite( y ) || !float.IsFinite( z ) ) return false;
		result = new Vector3( x, y, z );
		return true;
	}

	static bool TryParseFloat( string value, out float result ) => float.TryParse(
		value,
		System.Globalization.NumberStyles.Float,
		System.Globalization.CultureInfo.InvariantCulture,
		out result ) && float.IsFinite( result );

	static bool IsVistaModelPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.StartsWith( "models/vistas/", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "vistas/", StringComparison.OrdinalIgnoreCase );
	}

	static bool IsPlanetVista( string path ) => path?.Contains( "planet_blue_sun", StringComparison.OrdinalIgnoreCase ) == true;

	static void CreateFallbackSpawn( Vector3 position )
	{
		var spawn = new GameObject( true, "info_player_start" );
		spawn.WorldPosition = position;
		spawn.AddComponent<SpawnPoint>();
		spawn.Tags.Add( "spawn", "info_player_start" );
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).Trim().TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase )
			? normalized[..^5]
			: normalized;
	}
}

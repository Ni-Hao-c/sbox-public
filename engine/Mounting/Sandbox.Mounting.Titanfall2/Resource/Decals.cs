/// <summary>
/// Builds native <see cref="Decal"/> effects from Titanfall 2 decal materials so game code can
/// use them for bullet holes, scorch marks, tags and other runtime effects.
/// <para>
/// Native decals take textures, not materials, so the TF2 shader material is unpacked into its
/// baked colour, normal and emissive maps. Titanfall 2's per-material layer blending and colour
/// LUT are not reproduced; the mask comes from the colour texture's alpha. Static map decals
/// keep using the mounted BSP decal geometry instead.
/// </para>
/// </summary>
public static class Titanfall2DecalFactory
{
	/// <summary>Extracts a native decal definition from a mounted Titanfall 2 decal material.</summary>
	/// <param name="materialName">Material name, with or without the <c>materials/</c> prefix or
	/// <c>.vmt</c>/<c>.vmat</c> extension. A full <c>mount://</c> path is also accepted.</param>
	/// <param name="definition">The created definition; null when the material cannot be used.</param>
	/// <param name="error">Why the definition could not be created, or null on success.</param>
	public static bool TryCreateDefinition( string materialName, out DecalDefinition definition, out string error )
	{
		definition = null;
		error = null;

		var path = ResolveMaterialPath( materialName );
		if ( path is null )
		{
			error = "A Titanfall 2 material name is required.";
			return false;
		}

		var material = Material.Load( path );
		if ( material is null || !material.IsValid )
		{
			error = $"Material '{path}' could not be loaded. Is the Titanfall 2 mount installed and mounted?";
			return false;
		}

		return TryCreateDefinition( material, out definition, out error );
	}

	/// <summary>Extracts a native decal definition from an already loaded material.</summary>
	public static bool TryCreateDefinition( Material material, out DecalDefinition definition, out string error )
	{
		definition = null;
		error = null;

		if ( material is null || !material.IsValid )
		{
			error = "The decal material is not valid.";
			return false;
		}

		var color = GetTexture( material, "g_tAlbedo" );
		if ( color is null )
		{
			error = "The material has no base colour texture; a native decal needs an albedo mask.";
			return false;
		}

		definition = new DecalDefinition
		{
			ColorTexture = color,
			NormalTexture = GetTexture( material, "g_tNormal" ),
			EmissiveTexture = GetTexture( material, "g_tEmissive" ),
		};

		if ( GetTexture( material, "g_tOpacity" ) is not null )
		{
			// The native decal only has one mask: the colour texture's alpha. Keep going,
			// but leave a trace so a fully opaque TF2 opacity map is diagnosable.
			Titanfall2Log.Trace( $"{material.ResourcePath}: opacity texture is not used by native decals; the colour alpha is the mask." );
		}

		return true;
	}

	/// <summary>
	/// Spawns a decal as a child of <paramref name="parent"/>. The transform is expected to sit on
	/// the surface, oriented like the native decal component (see <see cref="RotationForNormal"/>).
	/// </summary>
	public static Decal Spawn( GameObject parent, string materialName, Vector3 position, Rotation rotation,
		Vector2 size, float depth = 8f, bool transient = false )
	{
		if ( !TryCreateDefinition( materialName, out var definition, out var error ) )
		{
			Titanfall2Log.Warning( $"Titanfall 2 decal '{materialName}' was not created: {error}" );
			return null;
		}

		var host = parent.IsValid() ? parent : Game.ActiveScene;
		if ( host is null )
			return null;

		var child = new GameObject( true, "titanfall2_decal" );
		child.Flags |= GameObjectFlags.NotSaved;
		child.Parent = host;
		child.WorldPosition = position;
		child.WorldRotation = rotation;

		var decal = child.AddComponent<Decal>( false );
		decal.Decals = [definition];
		decal.Rotation = 0;
		decal.Size = size;
		decal.Depth = depth;
		decal.Transient = transient;
		if ( transient )
			child.AddComponent<TemporaryEffect>();
		decal.Enabled = true;
		return decal;
	}

	/// <summary>
	/// Builds the decal orientation for a surface normal. Decals project along their local
	/// forward axis, so it points into the surface and the roll is derived from world up.
	/// </summary>
	public static Rotation RotationForNormal( Vector3 normal )
	{
		normal = normal.IsFinite && normal.LengthSquared > 1e-8f ? normal.Normal : Vector3.Up;
		var s = MathF.Abs( normal.z ) > 0.70710678f
			? Vector3.Cross( normal, Vector3.Cross( Vector3.Forward, normal ) ).Normal
			: Vector3.Cross( normal, Vector3.Down ).Normal;
		var t = Vector3.Cross( s, normal ).Normal;
		return Rotation.LookAt( -normal, -t );
	}

	/// <summary>
	/// Spawns a decal where the active scene camera is looking. Useful to try a material without
	/// writing game code first.
	/// </summary>
	[ConCmd( "titanfall2_decal", Help = "Spawn a Titanfall 2 decal under the camera: material, size (default 32), depth (default 8)." )]
	public static void SpawnAtView( string materialName, float size = 32f, float depth = 8f )
	{
		var scene = Game.ActiveScene;
		if ( scene is null || !scene.IsValid() || !scene.Camera.IsValid() )
		{
			Titanfall2Log.Warning( "titanfall2_decal needs a running scene with an active camera. Enter Play mode first." );
			return;
		}

		var camera = scene.Camera;
		var ray = camera.View.FieldOfView > 0f ? camera.View.ForwardRay : camera.WorldTransform.ForwardRay;
		var hit = scene.Trace.Ray( ray.Position, ray.Position + ray.Forward * 4096f ).Run();
		if ( !hit.Hit )
		{
			Titanfall2Log.Warning( "titanfall2_decal found no surface within 4096 units." );
			return;
		}

		Spawn( hit.GameObject, materialName, hit.HitPosition, RotationForNormal( hit.Normal ),
			new Vector2( MathF.Max( 1f, size ) ), MathF.Max( 0.1f, depth ) );
	}

	static string ResolveMaterialPath( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) )
			return null;

		var name = materialName.Replace( '\\', '/' ).Trim().Trim( '"' ).Trim( '\'' ).Trim();
		if ( name.Length == 0 )
			return null;

		if ( name.StartsWith( "mount://", StringComparison.OrdinalIgnoreCase ) )
			return name;

		name = name.TrimStart( '/' );
		if ( !name.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) )
			name = "materials/" + name;

		if ( name.EndsWith( ".vmt", StringComparison.OrdinalIgnoreCase ) )
			name = name[..^4] + ".vmat";
		else if ( !name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
			name += ".vmat";

		return $"mount://titanfall2/{name}";
	}

	static Texture GetTexture( Material material, string name )
	{
		var texture = material.GetTexture( name );
		return texture.IsValid() ? texture : null;
	}
}

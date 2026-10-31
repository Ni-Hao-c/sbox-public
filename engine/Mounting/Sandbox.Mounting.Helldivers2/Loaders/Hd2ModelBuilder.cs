namespace Sandbox.Mounting.Helldivers2;

/// <summary>
/// Assembles a runtime <see cref="Model"/> for one <c>unit</c> resource.
///
/// Geometry resolution follows the order the reference implementation uses: a unit either carries
/// its own meshes in its own <c>.gpu_resources</c> stream, or (much more often) names a shared
/// <c>geometry_group</c> resource that owns both the vertex layouts and the geometry, in which case
/// the geometry group's GPU stream is the one that holds the buffers.
/// </summary>
internal static class Hd2ModelBuilder
{
	/// <summary>
	/// The engine material every Helldivers 2 mesh gets until the game's own materials are
	/// translated. Held in a static field because a runtime material with no strong reference can be
	/// collected while native geometry still points at it, which renders as the error material.
	/// </summary>
	static Material _fallbackMaterial;

	public static bool TryBuild( Hd2DataDirectory directory, in Hd2Resource resource, out Model model, out string error )
		=> TryBuild( directory, resource, out model, out error, out _ );

	public static bool TryBuild( Hd2DataDirectory directory, in Hd2Resource resource, out Model model, out string error, out Hd2ModelStats stats )
	{
		model = null;
		error = null;
		stats = default;

		if ( !directory.TryRead( resource, Hd2DataType.Main, out var unitMain ) )
		{
			error = "the unit's main stream could not be read";
			return false;
		}

		if ( !Hd2UnitReader.TryRead( unitMain, out var unit, out error ) ) return false;

		if ( unit.HasTerrain )
		{
			error = "terrain units carry a heightmap instead of meshes";
			return false;
		}

		Hd2UnitMesh[] meshes;
		Hd2MeshLayout[] layouts;
		byte[] gpu;

		if ( unit.UsesExternalGeometry )
		{
			if ( !directory.TryGetResource( unit.Header.GeometryGroup, Hd2GeometryGroup.TypeId, out var groupResource ) )
			{
				error = $"the geometry group 0x{unit.Header.GeometryGroup} is not present in the mounted data";
				return false;
			}

			if ( !directory.TryRead( groupResource, Hd2DataType.Main, out var groupMain ) )
			{
				error = $"the geometry group 0x{unit.Header.GeometryGroup} has no readable main stream";
				return false;
			}

			if ( !Hd2GeometryGroupReader.TryRead( groupMain, out var group, out error ) ) return false;

			if ( !Hd2UnitReader.TryCreateGeometryGroupMeshes( unit, group, resource.Name, out meshes, out error ) ) return false;

			layouts = group.MeshLayouts;

			if ( !directory.TryRead( groupResource, Hd2DataType.Gpu, out gpu ) )
			{
				error = $"the geometry group 0x{unit.Header.GeometryGroup} has no GPU stream";
				return false;
			}
		}
		else
		{
			meshes = unit.Meshes;
			layouts = unit.MeshLayouts;

			if ( !directory.TryRead( resource, Hd2DataType.Gpu, out gpu ) )
			{
				error = "the unit has no GPU stream";
				return false;
			}
		}

		if ( meshes.Length == 0 )
		{
			error = "the unit resolved to no meshes";
			return false;
		}

		var material = GetFallbackMaterial();
		var name = resource.Name.ToString();
		var builder = Model.Builder.WithName( name );

		// Each group names its own material slot; the unit maps that thin hash to the full material
		// resource hash. A group whose material is missing falls back to the shared default.
		// SBOX_HELLDIVERS2_NO_MATERIALS=1 forces that fallback everywhere, which separates "the
		// geometry is fine" from "the bound material is what renders wrong".
		var useMaterials = Environment.GetEnvironmentVariable( "SBOX_HELLDIVERS2_NO_MATERIALS" ) != "1";
		var slots = 0;
		var bound = 0;

		var options = new Hd2MeshBuildOptions
		{
			ResolveMaterial = thin =>
			{
				if ( !useMaterials ) return null;

				slots++;
				var resolved = ResolveGroupMaterial( directory, unit, thin );
				if ( resolved is not null ) bound++;
				return resolved;
			}
		};

		var built = 0;
		var vertices = 0L;
		var indices = 0L;

		for ( var meshIndex = 0; meshIndex < meshes.Length; meshIndex++ )
		{
			var source = meshes[meshIndex];
			if ( source.Groups.Length == 0 || source.VertexCount == 0 || source.IndexCount < 3 ) continue;

			if ( !Hd2MeshBuilder.TryBuild( source, layouts, gpu, material, $"{name}_mesh{meshIndex}", options, out var mesh, out var meshError ) )
			{
				Hd2Log.Trace( $"Helldivers 2 unit {resource.Name} mesh {meshIndex} was skipped: {meshError}" );
				continue;
			}

			builder.AddMesh( mesh );
			built++;
			vertices += source.VertexCount;
			indices += source.IndexCount;
		}

		if ( built == 0 )
		{
			error = $"none of the {meshes.Length} resolved meshes could be built";
			return false;
		}

		model = builder.Create();
		stats = new Hd2ModelStats( built, meshes.Length, vertices, indices, unit.UsesExternalGeometry, slots, bound );

		Hd2Log.Trace( $"Helldivers 2 unit {resource.Name} built: {built}/{meshes.Length} meshes, "
			+ $"{vertices} vertices, {indices} indices, geometry {( unit.UsesExternalGeometry ? "external" : "own" )}." );

		return model is not null;
	}

	/// <summary>
	/// The material for one of a unit's group slots, or null to let the engine's error material show
	/// that the slot could not be resolved. Materials are created through the material reader, which
	/// pools them, so repeating a lookup across a unit's meshes is cheap.
	/// </summary>
	static Material ResolveGroupMaterial( Hd2DataDirectory directory, Hd2Unit unit, Hd2ThinHash slot )
	{
		if ( !unit.MaterialLookup.TryGetValue( slot, out var materialHash ) ) return null;
		if ( !directory.TryGetResource( materialHash, Hd2ResourceType.Material, out var resource ) ) return null;

		return Hd2MaterialReader.TryCreate( directory, resource, out var material, out _ ) ? material : null;
	}

	/// <summary>
	/// The placeholder material, and the material groups fall back to. Loading the engine's default
	/// material works and renders a lit surface; creating one from <c>shaders/complex.shader</c> at
	/// runtime did not (it rendered blank in the asset thumbnail), so the loaded material stays until
	/// the material path is understood better.
	/// </summary>
	public static Material GetFallbackMaterial()
	{
		if ( _fallbackMaterial is not null && _fallbackMaterial.IsValid() && !_fallbackMaterial.IsError ) return _fallbackMaterial;

		_fallbackMaterial = Material.Load( "materials/default.vmat" );

		if ( _fallbackMaterial is null || _fallbackMaterial.IsError )
			Hd2Log.Warning( "Unable to load the fallback material 'materials/default.vmat'; Helldivers 2 meshes will render with the engine's error material." );

		return _fallbackMaterial;
	}
}

/// <summary>What one unit expanded into, for diagnostics and for measuring load cost.</summary>
internal readonly struct Hd2ModelStats
{
	public readonly int MeshesBuilt;
	public readonly int MeshesResolved;
	public readonly long Vertices;
	public readonly long Indices;
	public readonly bool ExternalGeometry;
	public readonly int MaterialSlots;
	public readonly int MaterialsBound;

	public Hd2ModelStats( int meshesBuilt, int meshesResolved, long vertices, long indices, bool externalGeometry, int materialSlots, int materialsBound )
	{
		MeshesBuilt = meshesBuilt;
		MeshesResolved = meshesResolved;
		Vertices = vertices;
		Indices = indices;
		ExternalGeometry = externalGeometry;
		MaterialSlots = materialSlots;
		MaterialsBound = materialsBound;
	}

	public override string ToString() =>
		$"{MeshesBuilt}/{MeshesResolved} meshes, {Vertices:n0} vertices, {Indices:n0} indices, "
		+ $"{MaterialsBound}/{MaterialSlots} group materials, {( ExternalGeometry ? "external" : "own" )} geometry";
}

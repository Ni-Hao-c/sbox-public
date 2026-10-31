/// <summary>
/// Runtime mount for the locally installed Steam release of HELLDIVERS™ 2.
///
/// The game runs Autodesk Stingray 1.8, whose containers address every resource by a 64-bit
/// MurmurHash64A of its name and split each one into a table entry plus up to three data
/// streams (main, .stream and .gpu_resources). Almost all of that lives inside 30 multi-gigabyte
/// bundles.NN.nxa files, with loose DSAR files and .patch_N containers overriding them.
///
/// This mount indexes those containers, recovers names from the community name database shipped
/// next to the assembly, and exposes units as models and textures as textures under
/// <c>mount://helldivers2/</c>.
/// </summary>
public sealed class Helldivers2Mount : BaseGameMount
{
	public const long AppId = Hd2DataDirectory.AppId;

	/// <summary>
	/// The folder this mount was loaded from (<c>game/mount/helldivers2</c>), which is where the
	/// optional name database lives next to the assembly.
	/// </summary>
	internal static string MountDirectory => Path.GetDirectoryName( typeof( Helldivers2Mount ).Assembly.Location );

	Hd2DataDirectory _directory;
	string _dataDirectory;

	/// <summary>Offsets inside the 116-byte unit header; see <c>Unit/Hd2UnitHeader.cs</c> for the full table.</summary>
	const int UnitGeometryGroupOffset = 16;
	const int UnitTerrainInfoListOffset = 104;

	public override string Ident => "helldivers2";

	public override string Title => "HELLDIVERS™ 2";

	public override long? SteamAppId => AppId;

	/// <summary>The indexed game data, or null before the mount has succeeded.</summary>
	internal Hd2DataDirectory Directory => _directory;

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) ) return;

		var root = context.GetAppDirectory( AppId );
		if ( string.IsNullOrWhiteSpace( root ) || !System.IO.Directory.Exists( root ) ) return;

		var data = Path.Combine( root, Hd2DataDirectory.DataFolderName );
		if ( !File.Exists( Path.Combine( data, "bundles.nxa" ) ) ) return;

		_dataDirectory = data;
		IsInstalled = true;
	}

	protected override Task Mount( MountContext context )
	{
		if ( string.IsNullOrWhiteSpace( _dataDirectory ) ) return Task.CompletedTask;

		var started = System.Diagnostics.Stopwatch.StartNew();
#pragma warning disable CA2000 // ownership moves into the mount's _directory field, released in Shutdown
		var opened = Hd2DataDirectory.TryOpen( _dataDirectory, out var directory, out var error );
#pragma warning restore CA2000
		if ( !opened )
		{
			Hd2Log.Error( $"Unable to index Helldivers 2 data at '{_dataDirectory}': {error}" );
			return Task.CompletedTask;
		}

		_directory = directory;
		started.Stop();

		Hd2ResourceType.Dictionary = Hd2NameDictionary.Load( Path.Combine( MountDirectory, "hashes.txt" ) );

		var verified = Hd2ResourceType.Verify( out var hashFailure );
		if ( verified != Hd2ResourceType.ProbeCount )
			Hd2Log.Error( $"Helldivers 2 resource type hashes did not validate ({verified}/{Hd2ResourceType.ProbeCount}): {hashFailure}" );

		Hd2Log.Always( $"Helldivers 2 mounted: {directory.Resources.Count} resources from {directory.ArchiveCount} archives "
			+ $"({directory.PatchCount} patches, {directory.LooseArchiveCount} loose bundles, {directory.NxaBundleCount} .nxa files) "
			+ $"in {started.Elapsed.TotalSeconds:0.0}s; {verified}/{Hd2ResourceType.ProbeCount} type hashes validated; "
			+ $"name database {Hd2ResourceType.Dictionary.Count} names from {Hd2ResourceType.Dictionary.Loaded} entries "
			+ $"({Hd2ResourceType.Dictionary.Pairs} explicit hash mappings)." );

		LogTopTypes( directory );
		LogNameCoverage( directory );
		RegisterResources( context );

		IsMounted = true;

		if ( Environment.GetEnvironmentVariable( "SBOX_HELLDIVERS2_SELFTEST" ) == "1" ) _ = RunSelfTest();

		return Task.CompletedTask;
	}

	protected override void Shutdown()
	{
		_directory?.Dispose();
		_directory = null;
	}

	/// <summary>
	/// Registers the resource types this mount can currently produce. Units become models and
	/// textures become textures; materials, animations, sounds and maps are not wired up yet, so
	/// they are deliberately left out rather than exposed as something that cannot load.
	/// </summary>
	void RegisterResources( MountContext context )
	{
		var limit = ReadInt( "SBOX_HELLDIVERS2_REGISTER_LIMIT", 0, 0, int.MaxValue );
		var includeTextures = ReadBool( "SBOX_HELLDIVERS2_REGISTER_TEXTURES", false );
		var started = System.Diagnostics.Stopwatch.StartNew();

		var models = RegisterType( context, Hd2ResourceType.Unit, ResourceType.Model, static resource => new Hd2ModelResourceLoader( resource ), limit );

		// Textures are resolved by hash inside the material reader, so they do not need to exist as
		// assets for materials to work. Registering all ~17,000 of them only adds browser entries and
		// a long burst of background asset writes at startup, which is what widens the engine's
		// asset-browser race (see the note in the mount summary). Opt in when you want to browse them.
		var textures = includeTextures
			? RegisterType( context, Hd2ResourceType.Texture, ResourceType.Texture, static resource => new Hd2TextureResourceLoader( resource ), limit )
			: 0;

		started.Stop();

		Hd2Log.Always( $"Helldivers 2 registration: {models} models and {textures} textures registered in {started.Elapsed.TotalSeconds:0.0}s"
			+ ( limit > 0 ? $" (capped at {limit} per type by SBOX_HELLDIVERS2_REGISTER_LIMIT)" : string.Empty )
			+ ( includeTextures ? "." : "; textures are bound by hash for materials, set SBOX_HELLDIVERS2_REGISTER_TEXTURES=1 to also browse them." ) );
	}

	int RegisterType( MountContext context, Hd2Hash type, ResourceType resourceType, Func<Hd2Resource, ResourceLoader> create, int limit )
	{
		if ( _directory is null || !_directory.TryGetTypeIndices( type, out var indices ) ) return 0;

		var registered = 0;
		var filtered = 0;
		var filterCost = System.Diagnostics.Stopwatch.StartNew();

		foreach ( var index in indices )
		{
			if ( limit > 0 && registered >= limit ) break;

			var resource = _directory.GetResource( index );

			// Registering a resource that can never load only makes the asset system retry it
			// forever, so units that cannot produce geometry are left out of the mount.
			if ( resourceType == ResourceType.Model && !CanBuildModel( resource ) )
			{
				filtered++;
				continue;
			}

			var path = ResourcePath( resource );
			if ( string.IsNullOrWhiteSpace( path ) ) continue;

			context.Add( resourceType, path, create( resource ) );
			registered++;
		}

		filterCost.Stop();

		if ( filtered > 0 )
			Hd2Log.Always( $"Helldivers 2 registration filter: {filtered} {Hd2ResourceType.NameOf( type )} resources cannot produce geometry and were skipped "
				+ $"({filterCost.Elapsed.TotalMilliseconds:0.0} ms to decide)." );

		return registered;
	}

	/// <summary>
	/// Whether a unit can produce geometry at all. Most Helldivers 2 units keep no geometry of their
	/// own and draw from a shared <c>geometry_group</c> instead, so "has a GPU stream" is not a
	/// usable test; the answer is in the unit header. Reading it costs one small read per unit that
	/// has no GPU stream of its own (about a third of them), which is why the decision is timed.
	/// </summary>
	bool CanBuildModel( in Hd2Resource resource )
	{
		if ( _directory is null ) return false;

		// A unit with its own GPU stream still needs the header check for terrain, but those are
		// rare enough that the first failed load and the negative cache cover them.
		if ( !resource.File.Has( Hd2DataType.Gpu ) )
		{
			if ( !_directory.TryRead( resource, Hd2DataType.Main, out var main ) ) return false;
			if ( main.Length < Hd2UnitHeader.StoredSize ) return false;

			// TerrainInfoListOffset is non-zero for units that carry a heightmap instead of meshes.
			if ( System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian( main.AsSpan( UnitTerrainInfoListOffset ) ) != 0 ) return false;

			var geometryGroup = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian( main.AsSpan( UnitGeometryGroupOffset ) );
			if ( geometryGroup == 0 || geometryGroup == Hd2Unit.DevGeometryGroup ) return false;

			return _directory.TryGetResource( new Hd2Hash( geometryGroup ), Hd2GeometryGroup.TypeId, out _ );
		}

		return true;
	}

	/// <summary>
	/// The mount path for a resource: its recovered name when the database knows it, otherwise a
	/// stable type/hash path so the resource is still reachable.
	/// </summary>
	static string ResourcePath( Hd2Resource resource )
	{
		if ( Hd2ResourceType.Dictionary is { } dictionary && dictionary.TryGetName( resource.Name, out var name ) )
			return StripSourceExtension( name.Replace( '\\', '/' ).TrimStart( '/' ) );

		return $"{Hd2ResourceType.NameOf( resource.Type )}/{resource.Name}";
	}

	/// <summary>
	/// The name databases sometimes carry the original source file name (for example a <c>.tga</c>).
	/// The engine appends its own extension per resource type, so a leftover source extension would
	/// end up in the middle of the path.
	/// </summary>
	static readonly string[] SourceExtensions = [".tga", ".png", ".dds", ".fbx", ".psd", ".exr", ".jpg", ".jpeg", ".texture", ".material", ".unit"];

	static string StripSourceExtension( string name )
	{
		foreach ( var extension in SourceExtensions )
			if ( name.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
				return name[..^extension.Length];

		return name;
	}

	void LogTopTypes( Hd2DataDirectory directory )
	{
		var histogram = directory.TypeHistogram();
		var summary = string.Join( ", ", histogram.Take( 12 ).Select( static entry => $"{Hd2ResourceType.NameOf( entry.Type )}={entry.Count}" ) );
		Hd2Log.Always( $"Helldivers 2 resource types: {histogram.Count} distinct; {summary}." );
	}

	/// <summary>
	/// How many of the resources we can expose carry a recovered name. The database is scraped
	/// community data, so partial coverage is expected and worth measuring rather than assuming.
	/// </summary>
	static void LogNameCoverage( Hd2DataDirectory directory )
	{
		var dictionary = Hd2ResourceType.Dictionary;
		if ( dictionary is null || dictionary.Count == 0 ) return;

		foreach ( var type in new[] { Hd2ResourceType.Unit, Hd2ResourceType.Texture, Hd2ResourceType.Material } )
		{
			if ( !directory.TryGetTypeIndices( type, out var indices ) || indices.Count == 0 ) continue;

			var named = 0;
			foreach ( var index in indices )
				if ( dictionary.TryGetName( directory.GetResource( index ).Name, out _ ) ) named++;

			Hd2Log.Always( $"Helldivers 2 naming: {named}/{indices.Count} {Hd2ResourceType.NameOf( type )} resources have a recovered name." );
		}
	}

	async Task RunSelfTest()
	{
		await Task.Yield();

		try
		{
			var directory = _directory;
			if ( directory is null ) return;

			var count = ReadInt( "SBOX_HELLDIVERS2_SELFTEST_COUNT", 8, 1, 1000 );

			Probe( directory, Hd2ResourceType.Unit, Hd2DataType.Main );
			Probe( directory, Hd2ResourceType.Unit, Hd2DataType.Gpu );
			Probe( directory, Hd2ResourceType.Texture, Hd2DataType.Main );
			Probe( directory, Hd2ResourceType.Material, Hd2DataType.Main );
			ProbeTexturePayload( directory );
			ProbeModels( directory, count );
			ProbeTextures( directory, count );
			ProbeMaterials( directory );
			ProbeMaterialChain( directory );
			ProbeNamedBounds( directory );
		}
		catch ( Exception exception )
		{
			Hd2Log.Error( exception, "Helldivers 2 mount self-test failed." );
		}
	}

	static void Probe( Hd2DataDirectory directory, Hd2Hash type, Hd2DataType dataType )
	{
		if ( !directory.TryGetTypeIndices( type, out var indices ) || indices.Count == 0 )
		{
			Hd2Log.Always( $"[selftest] no resources of type {Hd2ResourceType.NameOf( type )} were indexed." );
			return;
		}

		var first = directory.GetResource( indices[0] );
		if ( !directory.TryRead( first, dataType, out var data ) )
		{
			Hd2Log.Always( $"[selftest] {Hd2ResourceType.NameOf( type )} {first.Name} has no readable {dataType} data." );
			return;
		}

		Hd2Log.Always( $"[selftest] {Hd2ResourceType.NameOf( type )} {first.Name} from bundle {first.Table.Id}: "
			+ $"{dataType} {data.Length} bytes, head {Hex( data, 16 )}." );
	}

	static void ProbeTexturePayload( Hd2DataDirectory directory )
	{
		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Texture, out var indices ) || indices.Count == 0 ) return;

		foreach ( var index in indices.Take( 64 ) )
		{
			var resource = directory.GetResource( index );
			if ( resource.File.SizeOf( Hd2DataType.Main ) < 0xC4 ) continue;
			if ( !directory.TryRead( resource, Hd2DataType.Main, out var data ) ) continue;

			var magic = System.Text.Encoding.ASCII.GetString( data, 0xC0, 4 );
			Hd2Log.Always( $"[selftest] texture {resource.Name} payload magic at 0xC0 is '{magic}'." );
			return;
		}

		Hd2Log.Always( "[selftest] no texture resource was large enough to check for the DDS payload magic." );
	}

	/// <summary>
	/// Builds real models from real payloads. This is the measurement that says whether the unit
	/// parser and the geometry-group indirection actually work on shipped data.
	/// </summary>
	static void ProbeModels( Hd2DataDirectory directory, int count )
	{
		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Unit, out var indices ) || indices.Count == 0 ) return;

		var attempted = 0;
		var built = 0;
		var vertices = 0L;
		var indices_ = 0L;
		var materialSlots = 0;
		var materialsBound = 0;
		var failures = new List<string>();
		var successes = new List<string>();
		var watch = System.Diagnostics.Stopwatch.StartNew();

		foreach ( var index in indices )
		{
			if ( attempted >= count ) break;

			var resource = directory.GetResource( index );
			attempted++;

			if ( Hd2ModelBuilder.TryBuild( directory, resource, out var model, out var error, out var stats ) )
			{
				built++;
				vertices += stats.Vertices;
				indices_ += stats.Indices;
				materialSlots += stats.MaterialSlots;
				materialsBound += stats.MaterialsBound;
				if ( successes.Count < 12 ) successes.Add( $"{ResourcePath( resource )} [{stats}]" );
				continue;
			}

			if ( failures.Count < 4 ) failures.Add( $"{resource.Name}: {error}" );
		}

		watch.Stop();

		if ( attempted == 0 ) return;

		Hd2Log.Always( $"[selftest] models: built {built}/{attempted} units, {vertices:n0} vertices, {indices_:n0} indices, "
			+ $"{materialsBound}/{materialSlots} group materials resolved, in {watch.Elapsed.TotalMilliseconds:0.0} ms "
			+ $"({watch.Elapsed.TotalMilliseconds / Math.Max( 1, attempted ):0.0} ms/unit)." );

		foreach ( var success in successes ) Hd2Log.Always( $"[selftest] model built: {success}" );
		foreach ( var failure in failures ) Hd2Log.Always( $"[selftest] model failure: {failure}" );
	}

	/// <summary>Creates real runtime textures, including the full BCn upload path.</summary>
	static void ProbeTextures( Hd2DataDirectory directory, int count )
	{
		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Texture, out var indices ) || indices.Count == 0 ) return;

		var attempted = 0;
		var created = 0;
		var failures = new List<string>();
		var watch = System.Diagnostics.Stopwatch.StartNew();

		foreach ( var index in indices )
		{
			if ( attempted >= count ) break;

			var resource = directory.GetResource( index );
			attempted++;

			if ( created < 4 ) Hd2Log.Always( $"[selftest] {Hd2TextureReader.Describe( directory, resource )}" );

			if ( Hd2TextureReader.TryCreate( directory, resource, out var texture, out var error ) )
			{
				created++;
				continue;
			}

			if ( failures.Count < 4 ) failures.Add( $"{resource.Name}: {error}" );
		}

		watch.Stop();

		if ( attempted == 0 ) return;

		Hd2Log.Always( $"[selftest] textures: created {created}/{attempted} runtime textures in {watch.Elapsed.TotalMilliseconds:0.0} ms." );

		foreach ( var failure in failures ) Hd2Log.Always( $"[selftest] texture failure: {failure}" );
	}

	/// <summary>
	/// Reports the engine-space bounds of a few named units. This is the check that catches a wrong
	/// up-axis or scale: the game stores metres, the engine works in inches, and a conversion mistake
	/// is otherwise only visible as a model that looks the wrong size next to something else.
	/// Deliberately independent of registration, so it works with any
	/// <c>SBOX_HELLDIVERS2_REGISTER_LIMIT</c>.
	/// </summary>
	/// <summary>
	/// Measures the material path on real payloads: how many of the game's materials turn into runtime
	/// materials, what their slots bound to, and one sample description. A material that renders black
	/// while binding "successfully" is exactly the kind of thing this is here to distinguish.
	/// </summary>
	static void ProbeMaterials( Hd2DataDirectory directory )
	{
		Hd2Log.Always( $"[selftest] {Hd2MaterialReader.Measure( directory, 64 )}" );

		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Material, out var indices ) || indices.Count == 0 ) return;

		var created = 0;
		var failed = 0;
		string firstFailure = null;
		var watch = System.Diagnostics.Stopwatch.StartNew();

		foreach ( var index in indices.Take( 64 ) )
		{
			var resource = directory.GetResource( index );

			if ( Hd2MaterialReader.TryCreate( directory, resource, out _, out var error ) )
			{
				created++;

				if ( created == 1 ) Hd2Log.Always( $"[selftest] material sample: {Hd2MaterialReader.Describe( directory, resource )}" );
				continue;
			}

			failed++;
			firstFailure ??= $"{resource.Name}: {error}";
		}

		watch.Stop();

		Hd2Log.Always( $"[selftest] materials: created {created}/{created + failed} runtime materials in {watch.Elapsed.TotalMilliseconds:0.0} ms; "
			+ $"binder pooled {Hd2MaterialBinder.MaterialCount} materials and {Hd2MaterialBinder.TextureCount} textures." );

		if ( firstFailure is not null ) Hd2Log.Always( $"[selftest] material failure: {firstFailure}" );
	}

	/// <summary>
	/// Walks one named unit all the way to runtime textures: unit to material slot to material
	/// resource to texture resource to a created <see cref="Texture"/>. Each link either produces a
	/// usable object or a readable reason, which is what tells a missing binding apart from a texture
	/// the engine rejected.
	/// </summary>
	static void ProbeMaterialChain( Hd2DataDirectory directory )
	{
		var wanted = Environment.GetEnvironmentVariable( "SBOX_HELLDIVERS2_CHAIN_PROBE" )?.Trim();
		if ( string.IsNullOrWhiteSpace( wanted ) ) wanted = "cha_soldier";

		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Unit, out var unitIndices ) ) return;

		var dictionary = Hd2ResourceType.Dictionary;
		if ( dictionary is null || dictionary.Count == 0 ) return;

		foreach ( var index in unitIndices )
		{
			var unitResource = directory.GetResource( index );
			if ( !dictionary.TryGetName( unitResource.Name, out var unitName ) ) continue;
			if ( !unitName.Contains( wanted, StringComparison.OrdinalIgnoreCase ) ) continue;

			if ( !directory.TryRead( unitResource, Hd2DataType.Main, out var main ) ) return;
			if ( !Hd2UnitReader.TryRead( main, out var unit, out var unitError ) )
			{
				Hd2Log.Always( $"[selftest] chain {unitName}: unit parse failed - {unitError}" );
				return;
			}

			Hd2Log.Always( $"[selftest] chain {unitName}: {unit.MaterialLookup.Count} material slot(s) on the unit" );

			foreach ( var pair in unit.MaterialLookup.Take( 3 ) )
			{
				if ( !directory.TryGetResource( pair.Value, Hd2ResourceType.Material, out var materialResource ) )
				{
					Hd2Log.Always( $"[selftest] chain   material 0x{pair.Value} is not present in the mounted data" );
					continue;
				}

				if ( !Hd2MaterialReader.TryRead( directory, materialResource, out var parsed, out var readError ) )
				{
					Hd2Log.Always( $"[selftest] chain   material 0x{pair.Value} parse failed - {readError}" );
					continue;
				}

				Hd2Log.Always( $"[selftest] chain   {Hd2MaterialReader.Describe( parsed )} (bindable {parsed.BindableSlots.Count()}/{parsed.Textures.Length})" );

				// Materials name a base material and often carry no slots of their own, so the chain is
				// what decides whether a resource has any textures at all.
				var baseHash = parsed.Header.BaseMaterial;
				for ( var depth = 1; depth <= 4 && baseHash.Value != 0; depth++ )
				{
					if ( !directory.TryGetResource( baseHash, Hd2ResourceType.Material, out var baseResource ) )
					{
						Hd2Log.Always( $"[selftest] chain     base[{depth}] 0x{baseHash} is not present in the mounted data" );
						break;
					}

					if ( !Hd2MaterialReader.TryRead( directory, baseResource, out var baseMaterial, out var baseError ) )
					{
						Hd2Log.Always( $"[selftest] chain     base[{depth}] 0x{baseHash} parse failed - {baseError}" );
						break;
					}

					Hd2Log.Always( $"[selftest] chain     base[{depth}] {Hd2MaterialReader.Describe( baseMaterial )} (bindable {baseMaterial.BindableSlots.Count()}/{baseMaterial.Textures.Length})" );
					baseHash = baseMaterial.Header.BaseMaterial;
				}

				foreach ( var slot in parsed.Textures.Take( 4 ) )
				{
					var exists = directory.TryGetResource( slot.Texture, Hd2ResourceType.Texture, out var textureResource );
					Texture texture = null;
					string textureError = null;

					if ( exists ) Hd2TextureReader.TryCreate( directory, textureResource, out texture, out textureError );

					Hd2Log.Always( $"[selftest] chain     slot '{slot.Name}' -> {( slot.Parameter ?? "(unmapped)" )}: "
						+ $"resource={exists}, created={texture is not null}, "
						+ $"dims={( texture is null ? "-" : $"{texture.Width}x{texture.Height}" )}, error={texture?.IsError}"
						+ ( textureError is null ? "" : $", {textureError}" ) );
				}
			}

			return;
		}

		Hd2Log.Always( $"[selftest] chain probe found no unit matching '{wanted}'." );
	}

	static void ProbeNamedBounds( Hd2DataDirectory directory )
	{
		if ( !directory.TryGetTypeIndices( Hd2ResourceType.Unit, out var indices ) ) return;

		var dictionary = Hd2ResourceType.Dictionary;
		if ( dictionary is null || dictionary.Count == 0 ) return;

		foreach ( var wanted in NamedBoundsProbes )
		{
			foreach ( var index in indices )
			{
				var resource = directory.GetResource( index );
				if ( !dictionary.TryGetName( resource.Name, out var name ) ) continue;
				if ( !name.Contains( wanted, StringComparison.OrdinalIgnoreCase ) ) continue;

				if ( !Hd2ModelBuilder.TryBuild( directory, resource, out var model, out var error ) )
				{
					Hd2Log.Always( $"[selftest] bounds {wanted}: build failed - {error}" );
					break;
				}

				var bounds = model.Bounds;
				Hd2Log.Always( $"[selftest] bounds {wanted}: {bounds.Size.x:0.###} x {bounds.Size.y:0.###} x {bounds.Size.z:0.###} inches "
					+ $"(min z {bounds.Mins.z:0.###}, {model.MeshCount} meshes) from {name}" );
				break;
			}
		}
	}

	static readonly string[] NamedBoundsProbes =
	[
		"oil_pump/oil_pump",
		"grenade_launcher/grenade_launcher",
		"habs_forklift_01",
		"cha_soldier",
		"il_canister"
	];

	static string Hex( byte[] data, int count )
	{
		var length = Math.Min( count, data.Length );
		return Convert.ToHexString( data.AsSpan( 0, length ) ).ToLowerInvariant();
	}

	static int ReadInt( string name, int fallback, int minimum, int maximum )
	{
		return int.TryParse( Environment.GetEnvironmentVariable( name ), out var value )
			? Math.Clamp( value, minimum, maximum )
			: fallback;
	}

	static bool ReadBool( string name, bool fallback )
	{
		var value = Environment.GetEnvironmentVariable( name )?.Trim();
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;

		return value == "1" || value.Equals( "true", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "on", StringComparison.OrdinalIgnoreCase );
	}
}

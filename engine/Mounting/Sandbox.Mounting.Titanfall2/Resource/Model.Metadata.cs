using Titanfall2.Formats;

/// <summary>Original MDL choices and gameplay metadata retained alongside the native model.</summary>
[Sandbox.ModelEditor.GameData( "titanfall2_model" )]
public sealed class Titanfall2ModelData
{
	public int SkinCount { get; set; }
	public int Skin { get; set; }
	public int Body { get; set; }
	public BodyGroup[] BodyGroups { get; set; } = [];
	public Hitbox[] Hitboxes { get; set; } = [];
	/// <summary>Name matches the native body group (blank names become bodypart_N, duplicates get _N).</summary>
	public sealed record BodyGroup( string Name, int Base, string[] Choices );
	/// <summary>Surface is the original MDL surfaceprop, which may not exist as an s&amp;box Surface.</summary>
	public sealed record Hitbox( string Set, string Name, int Group, int CriticalOverride, string KeyValue, string Surface = null );

	/// <summary>Changes one choice without resetting the other packed body groups.</summary>
	public int SetBodyGroup( int body, string name, int choice )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( body );
		var group = BodyGroups.FirstOrDefault( group => string.Equals( group.Name, name, StringComparison.OrdinalIgnoreCase ) )
			?? throw new ArgumentException( $"Unknown body group '{name}'.", nameof( name ) );
		if ( choice < 0 || choice >= group.Choices.Length || group.Base <= 0 ) throw new ArgumentOutOfRangeException( nameof( choice ) );
		return checked(body + (choice - body / group.Base % group.Choices.Length) * group.Base);
	}
}

partial class ModelLoader
{
	// Retail client.dll HITGROUP_* script constants (GENERIC = 0 through GEAR = 8).
	static readonly string[] HitgroupNames = ["generic", "head", "chest", "stomach", "leftarm", "rightarm", "leftleg", "rightleg", "gear"];
	const int MaxNativeBodyGroupChoices = 64;

	void AddModelMetadata( ModelBuilder builder, Titanfall2Mdl53Reader.ParsedModel parsed, SkeletonData skeleton )
	{
		var hitboxes = new List<Titanfall2ModelData.Hitbox>();
		var attachments = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var attachment in parsed.Attachments )
		{
			var source = attachment.Attachment;
			if ( source.LocalBone < 0 || source.LocalBone >= skeleton.Names.Length || string.IsNullOrWhiteSpace( attachment.Name ) ) continue;
			// Studio_FindAttachment returns the first match; 33 retail weapons repeat names
			// such as muzzle_flash, and a later duplicate must not replace the first.
			if ( !attachments.Add( attachment.Name ) ) continue;
			var m = source.LocalTransform;
			var position = new Vector3( m.M03, m.M13, m.M23 );
			var forward = new Vector3( m.M00, m.M10, m.M20 );
			var up = new Vector3( m.M02, m.M12, m.M22 );
			if ( !IsFinite( position ) || !IsFinite( forward ) || !IsFinite( up ) ) continue;
			// 462 retail attachments (e.g. Ronin Prime SCR_*) store an all-zero matrix. The
			// original resolves them to the bone origin; keep the name with bone orientation.
			var rotation = forward.LengthSquared < 0.000001f || Vector3.Cross( forward, up ).LengthSquared < 0.000001f
				? Rotation.Identity
				: Rotation.LookAt( forward, up );
			builder.AddAttachment( attachment.Name, position, rotation, skeleton.Names[source.LocalBone] );
		}
		foreach ( var sourceSet in parsed.HitboxSets )
		{
			var setName = string.IsNullOrWhiteSpace( sourceSet.Name ) ? $"set_{sourceSet.Index}" : sourceSet.Name;
			var set = builder.AddHitboxSet( setName );
			foreach ( var source in sourceSet.Hitboxes )
			{
				var box = source.Hitbox;
				if ( box.Bone < 0 || box.Bone >= skeleton.Names.Length ) continue;
				var min = ToSandbox( box.BoundingBoxMin );
				var max = ToSandbox( box.BoundingBoxMax );
				if ( !IsFinite( min ) || !IsFinite( max ) || min.x > max.x || min.y > max.y || min.z > max.z ) continue;
				var name = string.IsNullOrWhiteSpace( source.Name ) ? $"hitbox_{source.Index}" : source.Name;
				var surface = parsed.Bones[box.Bone].SurfaceProperty;
				if ( string.IsNullOrWhiteSpace( surface ) ) surface = parsed.SurfaceProperty;
				if ( string.IsNullOrWhiteSpace( surface ) ) surface = "default";
				var hitbox = set.AddBox( name, skeleton.Names[box.Bone], new BBox( min, max ) )
					.WithSurface( surface )
					.AddTag( $"titanfall2_hitgroup_{box.Group}" );
				if ( box.Group >= 0 && box.Group < HitgroupNames.Length ) hitbox.AddTag( HitgroupNames[box.Group] );
				if ( box.CriticalOverride != 0 ) hitbox.AddTag( "titanfall2_critical_override" );
				hitboxes.Add( new( setName, name, box.Group, box.CriticalOverride, source.KeyValue, surface ) );
			}
		}
		var bodyGroupNames = BodyGroupNames( parsed );
		builder.WithData( new Titanfall2ModelData
		{
			SkinCount = Math.Max( 1, parsed.Header.SkinFamilyCount ),
			Skin = skin,
			Body = body,
			BodyGroups = parsed.BodyParts.Select( ( part, index ) => new Titanfall2ModelData.BodyGroup( bodyGroupNames[index], part.BodyPart.Base, part.Models.Select( model => model.Name ).ToArray() ) ).ToArray(),
			Hitboxes = hitboxes.ToArray()
		} );
	}

	/// <summary>Unique names for every body part, shared by native body groups and <see cref="Titanfall2ModelData"/>.</summary>
	internal static string[] BodyGroupNames( Titanfall2Mdl53Reader.ParsedModel parsed )
	{
		var names = new string[parsed.BodyParts.Length];
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		for ( var part = 0; part < names.Length; part++ )
		{
			var sourceName = string.IsNullOrWhiteSpace( parsed.BodyParts[part].Name ) ? $"bodypart_{part}" : parsed.BodyParts[part].Name;
			var name = sourceName;
			for ( var suffix = 1; !used.Add( name ); suffix++ ) name = $"{sourceName}_{suffix}";
			names[part] = name;
		}
		return names;
	}

	/// <summary>Native group name per part, or null when no part has alternatives or the engine's 64 choices are exceeded.</summary>
	static string[] NativeBodyGroupNames( Titanfall2Mdl53Reader.ParsedModel parsed )
	{
		if ( !parsed.BodyParts.Any( part => part.Models.Length > 1 ) ) return null;
		// Every part becomes a group, single-choice ones too: the native builder only derives
		// body part choices correctly when no mesh is left in the all-groups mask (as Source 1 does).
		var choices = parsed.BodyParts.Sum( part => Math.Max( 1, part.Models.Length ) );
		if ( choices > MaxNativeBodyGroupChoices )
		{
			Titanfall2Log.Warning( $"Titanfall 2 model has {choices} body group choices; the engine supports {MaxNativeBodyGroupChoices}, so only the packed body value is imported." );
			return null;
		}
		return BodyGroupNames( parsed );
	}

	void RegisterBodyGroups( ModelBuilder builder, Titanfall2Mdl53Reader.ParsedModel parsed, string[] names )
	{
		for ( var part = 0; part < names.Length; part++ )
		{
			if ( names[part] is null ) continue;
			// Register every choice in order, blank ones too, so choice 0 stays the native
			// default and SetBodyGroup( name, n ) reaches MDL model n.
			for ( var choice = 0; choice < parsed.BodyParts[part].Models.Length; choice++ )
				builder.AddMesh( null, names[part], choice );
		}
	}

	static int SelectedBodyChoice( Titanfall2Mdl53Reader.BodyPartInfo part, int body )
		=> part.BodyPart.Base > 0 && part.Models.Length > 0 ? body / part.BodyPart.Base % part.Models.Length : 0;

	static bool IsSelectedBodyChoice( Titanfall2Mdl53Reader.ParsedModel parsed, int part, int choice, int body )
		=> part < 0 || part >= parsed.BodyParts.Length || parsed.BodyParts[part].Models.Length <= 1
			|| SelectedBodyChoice( parsed.BodyParts[part], body ) == choice;

	static bool SameSkinMaterials( Titanfall2Mdl53Reader.ParsedModel parsed, int first, int second )
	{
		for ( var family = 0; family < parsed.Header.SkinFamilyCount; family++ )
		{
			if ( !string.Equals( Titanfall2MeshExtractor.ResolveMaterialName( parsed, first, 0, family ),
				Titanfall2MeshExtractor.ResolveMaterialName( parsed, second, 0, family ), StringComparison.OrdinalIgnoreCase ) )
				return false;
		}
		return true;
	}

	/// <summary>
	/// Adds "Skin N" material groups in the model's material order (first use per distinct
	/// material), so ModelRenderer.MaterialGroup switches skin families on the same model.
	/// </summary>
	void AddSkinMaterialGroups( ModelBuilder builder, Titanfall2Mdl53Reader.ParsedModel parsed, List<(Material Material, int SkinReference)> slots, bool conflict )
	{
		var families = parsed.Header.SkinFamilyCount;
		if ( families <= 1 || slots.Count == 0 ) return;
		if ( conflict )
		{
			// One native material slot cannot map to two different skin materials.
			Titanfall2Log.Warning( $"Titanfall 2 model shares one material between skin slots that diverge; material groups skipped: {ImportPath}" );
			return;
		}
		var groups = new Material[families][];
		try
		{
			for ( var family = 0; family < families; family++ )
			{
				groups[family] = slots.Select( slot => family == skin ? slot.Material
					: LoadMaterial( Titanfall2MeshExtractor.ResolveMaterialName( parsed, slot.SkinReference, 0, family ) ) ?? slot.Material ).ToArray();
			}
		}
		catch ( Exception exception ) when ( exception is InvalidOperationException or ArgumentOutOfRangeException )
		{
			Titanfall2Log.Warning( $"Titanfall 2 model skin table is invalid; material groups skipped: {ImportPath} ({exception.Message})" );
			return;
		}
		for ( var family = 0; family < families; family++ )
			builder.AddMaterialGroup( $"Skin {family}" ).AddMaterials( groups[family] );
	}

	Model BuildCollisionOnly( Titanfall2Mdl53Reader.ParsedModel parsed, Titanfall2MeshExtractor.ExtractedLod lod, byte[] bytes )
	{
		var builder = Model.Builder.WithName( ImportPath );
		var skeleton = AddSkeleton( builder, parsed );
		AddModelMetadata( builder, parsed, skeleton );
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		foreach ( var mesh in lod.Meshes )
		{
			var name = NormalizeMaterialName( mesh.MaterialName );
			if ( !Titanfall2CollisionFilter.ShouldIncludeMaterial( name, GetMaterialMetadata( name ) ) ) continue;
			var offset = vertices.Count;
			vertices.AddRange( mesh.Vertices.Select( vertex => ToSandbox( vertex.Position ) ) );
			indices.AddRange( ReverseWinding( mesh.Indices ).Select( index => index + offset ) );
		}
		var collision = AddCollision( builder, parsed, skeleton, bytes, vertices, indices );
		if ( collision.HullCount == 0 ) return null;
		builder.WithData( new Titanfall2ModelCollisionData
		{
			SourcePath = ImportPath,
			GeometrySource = collision.Description,
			HullCount = collision.HullCount,
			ModelContents = unchecked((uint)parsed.Header.Contents)
		} );
		return builder.Create();
	}
}

public partial class Titanfall2Mount
{
	/// <summary>Loads an MDL skin/body variant. Physics-only variants never construct materials or GPU meshes.</summary>
	public Model LoadModelVariant( string path, int skin = 0, int body = 0, bool collisionOnly = false )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var resourcePath = path.StartsWith( "mount://", StringComparison.OrdinalIgnoreCase ) ? path : $"mount://{Ident}/{path.TrimStart( '/' )}";
		if ( !resourcePath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) resourcePath += ".vmdl";
		if ( GetByPath( resourcePath ) is not ModelLoader loader ) return null;
		return skin == 0 && body == 0 && !collisionOnly ? Model.Load( resourcePath ) : loader.GetVariant( skin, body, collisionOnly );
	}
}

/// <summary>Applies authored MDL skin and packed body choices to a renderer and optional collider.</summary>
[Library( "titanfall2_model_variant" )]
public sealed class Titanfall2ModelVariant : Component, Sandbox.Internal.IUpdateSubscriber
{
	[Property] public string MountIdent { get; set; } = "titanfall2";
	[Property] public string ModelPath { get; set; }
	[Property] public int Skin { get; set; }
	[Property] public int Body { get; set; }
	[Property] public ModelRenderer Renderer { get; set; }
	[Property] public ModelCollider Collider { get; set; }
	(string Mount, string Path, int Skin, int Body, bool Server)? _applied;
	Model _model;
	double _retryAt;
	protected override void OnEnabled() => Apply();
	protected override void OnUpdate() => Apply();
	public bool SetBodyGroup( string name, int choice )
	{
		Apply();
		var data = _model?.GetData<Titanfall2ModelData>();
		if ( data is null ) return false;
		Body = data.SetBodyGroup( Body, name, choice );
		return Apply();
	}
	public bool Apply()
	{
		var key = (MountIdent, ModelPath, Skin, Body, Application.IsDedicatedServer);
		if ( _applied != key )
		{
			// A different variant owns a different model; drop the previous reference so a
			// failed load cannot keep reporting the old one as applied.
			_model = null;
			_retryAt = 0;
		}
		else
		{
			if ( _model is not null ) return true;
			// A failed load used to be retried from OnUpdate every frame: a full MDL decode
			// plus material/GPU creation per attempt. Back off like the prop model path.
			if ( Titanfall2Log.ElapsedSeconds < _retryAt ) return false;
		}
		if ( string.IsNullOrWhiteSpace( ModelPath ) || Sandbox.Mounting.Directory.Get( MountIdent ) is not Titanfall2Mount mount || !mount.IsMounted ) return false;
		var model = mount.LoadModelVariant( ModelPath, Skin, Body, Application.IsDedicatedServer );
		if ( !ModelLoader.IsUsableModel( model ) )
		{
			_applied = key;
			_retryAt = Titanfall2Log.ElapsedSeconds + Titanfall2StreamingSettings.ModelFailureRetrySeconds;
			return false;
		}
		Renderer ??= Components.Get<ModelRenderer>();
		Collider ??= Components.Get<ModelCollider>();
		if ( Renderer.IsValid() && !Application.IsDedicatedServer ) Renderer.Model = model;
		if ( Collider.IsValid() ) Collider.Model = model;
		_model = model;
		_applied = key;
		_retryAt = 0;
		return true;
	}
}

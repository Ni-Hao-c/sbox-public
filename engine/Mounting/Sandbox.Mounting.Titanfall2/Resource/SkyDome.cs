using Titanfall2;

/// <summary>
/// Draws the imported Titanfall sky sphere as camera-following world geometry.
/// <para>
/// The retail skybox is a sphere authored around the map's <c>sky_camera</c>
/// (<c>models/vistas/angel_city_se.mdl</c> is ~2000 units across) whose inside is painted with the
/// skyline. The engine's 3D skybox view keeps the authoring-space camera at the sphere centre, so
/// the sphere ends up either hugging the camera or sitting outside it; rotating the view past the
/// painted band then makes it vanish. Re-centring the sphere on the camera every frame keeps the
/// player inside it for every view direction, which is what the paint was authored for.
/// </para>
/// </summary>
[Library]
public sealed class Titanfall2SkyDome : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2SkyDome" );
	const int DataMagic = 0x44534B54; // TSKD
	const int DataVersion = 1;
	const int MaximumModelCount = 1024;

	[Property, Hide] public string MountIdent { get; set; }
	[Property, Hide] public string MapPath { get; set; }
	[Property, Hide] public Vector3 SkyCameraOrigin { get; set; }
	[Property, Hide] public float ModelScale { get; set; } = 20f;
	[Property, Hide] public bool FogEnabled { get; set; }
	[Property, Hide] public Color FogColor { get; set; } = Color.White;
	[Property, Hide] public string EncodedModels { get; set; }

	sealed class DomeEntry
	{
		public SceneModel Model;
		public Rotation Rotation = Rotation.Identity;
		public float Scale = 1f;
		public Vector3 Offset;
	}

	readonly List<DomeEntry> _models = new();
	bool _built;
	Vector3? _lastCentre;

	internal void Configure( string mountIdent, string mapPath, Vector3 skyCameraOrigin, float modelScale,
		Titanfall2SkyboxFog fog, IReadOnlyList<Titanfall2SkyboxModel> models )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		SkyCameraOrigin = skyCameraOrigin;
		ModelScale = Math.Max( modelScale, 0.001f );
		FogEnabled = fog.Enabled;
		FogColor = fog.Color;
		EncodedModels = Encode( models );
	}

	protected override void OnStart() => Build();
	protected override void OnEnabled()
	{
		Build();
		foreach ( var entry in _models )
			if ( entry.Model.IsValid() ) entry.Model.RenderingEnabled = true;
	}

	protected override void OnDisabled()
	{
		foreach ( var entry in _models )
			if ( entry.Model.IsValid() ) entry.Model.RenderingEnabled = false;
	}

	protected override void OnDestroy() => ReleaseModels();

	void ReleaseModels()
	{
		foreach ( var entry in _models ) entry.Model?.Delete();
		_models.Clear();
		_lastCentre = null;
	}

	void Build()
	{
		if ( _built || string.IsNullOrWhiteSpace( EncodedModels ) ) return;
		var world = Scene?.SceneWorld;
		if ( world is null || !world.IsValid() ) return;
		_built = true;

		try
		{
			var failures = 0;
			var definitions = Decode( EncodedModels ).ToArray();
			foreach ( var definition in definitions )
			{
				var resourcePath = $"mount://{MountIdent}/{NormalizeModelPath( definition.ModelPath )}.vmdl";
				var model = Model.Load( resourcePath );
				if ( !ModelLoader.IsUsableModel( model ) )
				{
					failures++;
					Log.Warning( $"Unable to load Titanfall 2 sky dome model '{resourcePath}'." );
					continue;
				}

				var entry = new DomeEntry
				{
					Rotation = definition.Rotation.ToRotation(),
					Scale = MathF.Max( definition.Scale <= 0f ? 1f : definition.Scale, 0.001f ) * ModelScale,
					Offset = (definition.Position - SkyCameraOrigin) * ModelScale
				};
				entry.Model = new SceneModel( world, model, new Transform( definition.Position, entry.Rotation, entry.Scale ) );
				entry.Model.Flags.CastShadows = false;
				entry.Model.Flags.IsStatic = false;
				entry.Model.Flags.NeedsEnvironmentMap = false;
				entry.Model.Flags.NeedsLightProbe = false;
				entry.Model.UpdateToBindPose();
				entry.Model.Bounds = model.Bounds.Transform( entry.Model.Transform );
				_models.Add( entry );
			}

			Log.Info( $"Titanfall 2 sky dome ready: {_models.Count} models, scale {ModelScale:0.##}, "
				+ $"{failures} failures ({MapPath})." );
		}
		catch ( Exception exception )
		{
			ReleaseModels();
			_built = false;
			Log.Warning( exception, $"Unable to create the Titanfall 2 sky dome for '{MapPath}'." );
		}
	}

	protected override void OnUpdate()
	{
		if ( !_built || _models.Count == 0 ) return;
		if ( Application.IsHeadless ) return;

		var camera = Scene?.Camera;
		if ( !camera.IsValid() ) return;

		// Keep the painted sphere centred on the viewer: that is the pose it was authored from,
		// so the skyline stays put in world directions instead of sliding past the camera.
		var centre = camera.WorldPosition;
		// The poses depend only on the camera position; skip the bind-pose/bounds refresh while it is still.
		if ( _lastCentre == centre ) return;
		_lastCentre = centre;
		foreach ( var entry in _models )
		{
			if ( !entry.Model.IsValid() ) continue;
			entry.Model.Transform = new Transform( centre + entry.Offset, entry.Rotation, entry.Scale );
			entry.Model.UpdateToBindPose();
			entry.Model.Bounds = entry.Model.Model.Bounds.Transform( entry.Model.Transform );
		}
	}

	static string Encode( IReadOnlyList<Titanfall2SkyboxModel> source )
	{
		var models = source?.Where( static model => !string.IsNullOrWhiteSpace( model.ModelPath ) ).ToArray()
			?? Array.Empty<Titanfall2SkyboxModel>();
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( models.Length );
			foreach ( var model in models )
			{
				writer.Write( NormalizeModelPath( model.ModelPath ) );
				writer.Write( model.Position.x );
				writer.Write( model.Position.y );
				writer.Write( model.Position.z );
				writer.Write( model.Rotation.pitch );
				writer.Write( model.Rotation.yaw );
				writer.Write( model.Rotation.roll );
				writer.Write( model.Scale );
			}
		}

		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<Titanfall2SkyboxModel> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Sky dome data has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( "Sky dome data has an unsupported version." );
		var count = reader.ReadInt32();
		if ( count < 0 || count > MaximumModelCount ) throw new InvalidDataException( $"Invalid sky dome model count {count}." );

		for ( var index = 0; index < count; index++ )
		{
			yield return new Titanfall2SkyboxModel(
				reader.ReadString(),
				new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() ),
				new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() ),
				reader.ReadSingle() );
		}
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? normalized[..^5] : normalized;
	}
}

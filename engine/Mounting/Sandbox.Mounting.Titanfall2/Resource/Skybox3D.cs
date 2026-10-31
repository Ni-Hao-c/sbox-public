using System.Reflection;
using Sandbox.Rendering;

/// <summary>
/// Hosts Titanfall 2 vista models in the engine's native 3D skybox world. The
/// native bridge is internal to Sandbox.Engine, so the mounting layer discovers
/// it at runtime without changing the engine assembly.
/// </summary>
[Library]
public sealed class Titanfall2Skybox3D : Component, Component.DontExecuteOnServer, Sandbox.Internal.IPreRenderSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Skybox" );
	const int DataMagic = 0x324B5354; // TSK2
	const int DataVersion = 1;
	const int MaximumModelCount = 1024;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	[Property, Hide]
	public string CameraName { get; set; }

	[Property, Hide]
	public Vector3 CameraOrigin { get; set; }

	[Property, Hide]
	public Angles CameraAngles { get; set; }

	[Property, Hide]
	public float SkyScale { get; set; } = 1000f;

	[Property, Hide]
	public bool FogEnabled { get; set; }

	[Property, Hide]
	public Color FogColor { get; set; } = new( 0.74f, 0.85f, 1f );

	[Property, Hide]
	public float FogStartDistance { get; set; } = 500f;

	[Property, Hide]
	public float FogEndDistance { get; set; } = 5000f;

	[Property, Hide]
	public float FogMaximumOpacity { get; set; } = 0.3f;
	[Property, Hide] public Vector4 FogDensity { get; set; }
	[Property, Hide] public Vector4 FogBaseColor { get; set; }
	[Property, Hide] public Vector4 FogDirectionalColor { get; set; }
	[Property, Hide] public Vector4 FogDirection { get; set; }
	[Property, Hide] public Vector4 FogHeight { get; set; }
	[Property, Hide] public bool UseWorldFog { get; set; }
	Titanfall2HeightFog _worldFog;
	RealTimeSince _worldFogSearch = 10f;

	[Property, Hide]
	public string EncodedModels { get; set; }

	readonly List<SceneModel> _sceneModels = new();
	SceneWorld _skyboxWorld;
	SceneSkyBox _skySkyObject;
	SceneDirectionalLight _skyLight;
	object _skyboxBridge;
	Type _skyboxBridgeType;
	PropertyInfo _bridgeOrigin;
	Action _updateBridge;
	float _drawScale;
	Vector3? _lastViewPosition;

	internal void Configure(
		string mountIdent,
		string mapPath,
		string cameraName,
		Vector3 cameraOrigin,
		Angles cameraAngles,
		float skyScale,
		Titanfall2SkyboxFog fog,
		IReadOnlyList<Titanfall2SkyboxModel> models )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		CameraName = cameraName;
		CameraOrigin = cameraOrigin;
		CameraAngles = cameraAngles;
		SkyScale = Math.Clamp( skyScale, 1f, 100000f );
		FogEnabled = fog.Enabled;
		FogColor = fog.Color;
		FogStartDistance = MathF.Max( 0f, fog.StartDistance );
		FogEndDistance = MathF.Max( FogStartDistance + 1f, fog.EndDistance );
		FogMaximumOpacity = Math.Clamp( fog.MaximumOpacity, 0f, 0.85f );
		FogDensity = fog.Parameters.Density;
		FogBaseColor = fog.Parameters.Color;
		FogDirectionalColor = fog.Parameters.DirectionalColor;
		FogDirection = fog.Parameters.Direction;
		FogHeight = fog.Parameters.Height;
		UseWorldFog = fog.UseWorldFog;
		EncodedModels = Encode( models );
	}

	protected override void OnStart()
	{
		CreateSkybox();
	}

	protected override void OnEnabled() => CreateSkybox();

	protected override void OnDisabled() => DestroySkybox();

	protected override void OnDestroy()
	{
		DestroySkybox();
	}

	protected override void OnPreRender()
	{
		if ( _skyboxBridge is null || _updateBridge is null ) return;
		// SetData allocates a dynamic constant buffer. Refresh it even when the
		// camera is stationary; a buffer from scene creation is not frame history.
		var fog = new Titanfall2FogParameters( Titanfall2StreamingSettings.MapFog ? FogDensity : Vector4.Zero,
			FogBaseColor, FogDirectionalColor, FogDirection, FogHeight );
		if ( UseWorldFog && Titanfall2StreamingSettings.MapFog )
		{
			// A map whose fog does not cover the origin has no Titanfall2HeightFog at all
			// (CreateEnvironment), so an unthrottled search walks the whole scene every frame.
			if ( (!_worldFog.IsValid() || !_worldFog.Active) && _worldFogSearch > 1f )
			{
				_worldFogSearch = 0;
				_worldFog = Scene.GetComponentsInChildren<Titanfall2HeightFog>().FirstOrDefault( f => f.Active );
			}
			if ( _worldFog.IsValid() && _worldFog.Active )
			{
				_worldFog.SelectForView();
				fog = _worldFog.Parameters;
			}
		}
		foreach ( var model in _sceneModels ) fog.Apply( model.Attributes );
		var camera = Scene?.Camera;
		if ( !camera.IsValid() ) return;
		// Camera modifiers compose before PreRender. Match the view that is actually
		// rendered, including third-person offsets and camera effects.
		var viewPosition = camera.View.Position;
		if ( !viewPosition.IsFinite || _lastViewPosition == viewPosition ) return;

		try
		{
			// The retail vista is a painted panorama, including cards close to its origin.
			// Native 3D skyboxes add mainCamera / Scale. Cancel that translation so walking
			// cannot move the sky camera through those cards or bring the skyline forward.
			_bridgeOrigin.SetValue( _skyboxBridge, CameraOrigin - viewPosition / _drawScale );
			_updateBridge();
			_lastViewPosition = viewPosition;
		}
		catch ( Exception exception )
		{
			// Never let a failed native bridge callback throw every frame.
			Log.Warning( exception, $"Unable to update Titanfall 2 skybox for '{MapPath}'." );
			DestroySkybox();
		}
	}

	void CreateSkybox()
	{
		if ( _skyboxBridge is not null || string.IsNullOrWhiteSpace( EncodedModels ) ) return;
		var parentWorld = Scene?.SceneWorld;
		if ( parentWorld is null || !parentWorld.IsValid() ) return;

		try
		{
			var definitions = Decode( EncodedModels ).ToArray();
			if ( definitions.Length == 0 ) return;

			// Vista models are loaded before the bridge exists: their bounds decide the draw
			// scale, because scale 1 puts a giant model in the playable world while a huge
			// scale pushes the content past the camera's far plane and the sky disappears.
			var loadedModels = new List<(Titanfall2SkyboxModel Definition, Model Model)>( definitions.Length );
			var failures = 0;
			foreach ( var definition in definitions )
			{
				var resourcePath = $"mount://{MountIdent}/{NormalizeModelPath( definition.ModelPath )}.vmdl";
				var model = Model.Load( resourcePath );
				if ( !ModelLoader.IsUsableModel( model ) )
				{
					failures++;
					Log.Warning( $"Unable to load Titanfall 2 skybox vista model '{resourcePath}'." );
					continue;
				}

				loadedModels.Add( (definition, model) );
			}

			if ( loadedModels.Count == 0 )
			{
				Log.Warning( $"Titanfall 2 3D skybox has no loadable vista models ({MapPath})." );
				return;
			}

			_skyboxBridgeType = typeof( SceneWorld ).Assembly.GetType( "Sandbox.SceneSkybox3D", throwOnError: false );
			if ( _skyboxBridgeType is null )
				throw new InvalidOperationException( "Sandbox.SceneSkybox3D is unavailable." );

			var constructor = _skyboxBridgeType.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				types: [typeof( SceneWorld ), typeof( SceneWorld )],
				modifiers: null );
			if ( constructor is null )
				throw new MissingMethodException( _skyboxBridgeType.FullName, ".ctor(SceneWorld, SceneWorld)" );

			_skyboxWorld = new SceneWorld
			{
				// Vista textures contain most of their lighting. A bright ambient term keeps
				// the generic Titanfall material from rendering the isolated world black.
				AmbientLightColor = new Color( 0.85f, 0.9f, 1.0f )
			};
			// The authored fog distances belong to the map's own unit scale, but the vista
			// content is drawn SkyboxModelScale times larger in this isolated world. Applying
			// the raw values (mp_angel_city authors 0-500) against geometry tens of thousands
			// of units away saturates the fog everywhere and turns the whole sky into flat
			// fog colour - which then feeds bloom and auto exposure and reads as a moving glow.
			var fogScale = MathF.Max( 1f, Titanfall2StreamingSettings.SkyboxModelScale );
			var fogStart = FogStartDistance * fogScale;
			var fogEnd = MathF.Max( fogStart + 1f, FogEndDistance * fogScale );
			if ( FogEnabled && !Titanfall2StreamingSettings.MapFog )
			{
				// sky_camera fog belongs to the isolated 3D-skybox world. Applying it
				// here restores Titanfall's aerial perspective without re-enabling fog
				// cards or adding fog to the playable BSP world.
				_skyboxWorld.GradientFog = new GradientFogSetup
				{
					Enabled = true,
					StartDistance = fogStart,
					EndDistance = fogEnd,
					StartHeight = -100000f,
					EndHeight = 100000f,
					MaximumOpacity = FogMaximumOpacity,
					Color = FogColor.WithAlpha( 1f ),
					DistanceFalloffExponent = 1.15f,
					VerticalFalloffExponent = 0.01f
				};
			}

			// The 3D skybox view draws its own 2D sky from THIS world's SceneSkyBox; without one
			// the directions the vista panorama does not cover fall back to a flat procedural sky.
			if ( Titanfall2StreamingSettings.Sky2D )
			{
				var skyMaterial = Material.Load( Titanfall2StreamingSettings.SkyMaterial );
				if ( skyMaterial is not null && skyMaterial.IsValid )
				{
					_skySkyObject = new SceneSkyBox( _skyboxWorld, skyMaterial )
					{
						SkyTint = Color.White
					};
				}
			}

			var scale = ResolveScale( loadedModels );
			_skyboxBridge = constructor.Invoke( [parentWorld, _skyboxWorld] );
			SetBridgeProperty( "CameraOrigin", CameraOrigin );
			SetBridgeProperty( "Origin", CameraOrigin );
			SetBridgeProperty( "Angles", CameraAngles );
			SetBridgeProperty( "Scale", scale );
			_drawScale = scale;
			_bridgeOrigin = _skyboxBridgeType.GetProperty( "Origin" );
			_updateBridge = _skyboxBridgeType.GetMethod( "Update" ).CreateDelegate<Action>( _skyboxBridge );
			InvokeBridge( "Update" );
			OnPreRender();

			_skyLight = new SceneDirectionalLight(
				_skyboxWorld,
				Rotation.From( 50f, -35f, 0f ),
				new Color( 1.0f, 0.95f, 0.86f ) )
			{
				ShadowsEnabled = false
			};

			var modelScale = Titanfall2StreamingSettings.SkyboxModelScale;
			foreach ( var (definition, model) in loadedModels )
			{
				var sceneModel = new SceneModel(
					_skyboxWorld,
					model,
					new Transform( definition.Position, definition.Rotation.ToRotation(),
						MathF.Max( definition.Scale, 0.001f ) * modelScale ) );
				sceneModel.Flags.IsStatic = true;
				sceneModel.Flags.CastShadows = false;
				// Only the isolated vista uses miniature camera planes. Do not change
				// the player's near plane or normal material previews/dome rendering.
				sceneModel.Attributes.Set( "T2SkyProjectionNear", 0.001f );
				// Draw attributes override the main view's fog: the sky camera has
				// its own authored heights, density and directional scattering.
				new Titanfall2FogParameters( Titanfall2StreamingSettings.MapFog ? FogDensity : Vector4.Zero,
					FogBaseColor, FogDirectionalColor, FogDirection, FogHeight ).Apply( sceneModel.Attributes );
				// This isolated world has no ModelRenderer to initialize bones/bounds.
				// An uninitialized SceneModel has a point-sized culling box at its origin,
				// so turning away from that point incorrectly hides the entire panorama.
				sceneModel.UpdateToBindPose();
				sceneModel.Bounds = model.Bounds.Transform( sceneModel.Transform );
				_sceneModels.Add( sceneModel );
			}

			Log.Info( $"Titanfall 2 3D skybox ready: {_sceneModels.Count}/{definitions.Length} vista models, "
				+ $"camera '{CameraName}', origin {CameraOrigin}, scale {scale:0.####}, model scale {modelScale:0.##}"
				+ $"({(Titanfall2StreamingSettings.SkyboxScale > 0f ? " override" : string.Empty)}), "
				+ $"{(FogEnabled ? $"fog {fogStart:0.#}-{fogEnd:0.#} @ {FogMaximumOpacity:0.##} (authored {FogStartDistance:0.#}-{FogEndDistance:0.#})" : "fog disabled")}, "
				+ $"{failures} failures ({MapPath})." );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to create Titanfall 2 3D skybox for '{MapPath}'." );
			DestroySkybox();
		}
	}

	/// <summary>
	/// Pushes the authored vista content to a believable distance. Titanfall stores skyscale=1000,
	/// which the engine would use verbatim - putting its skybox camera inside the sector - so the
	/// value is derived from the content extent unless it is overridden.
	/// </summary>
	float ResolveScale( List<(Titanfall2SkyboxModel Definition, Model Model)> models )
	{
		if ( Titanfall2StreamingSettings.SkyboxScale > 0f ) return Titanfall2StreamingSettings.SkyboxScale;

		var extent = 1f;
		foreach ( var (definition, model) in models )
		{
			var offset = (definition.Position - CameraOrigin).Length;
			var radius = model.Bounds.Size.Length * MathF.Abs( MathF.Max( definition.Scale, 0.001f ) )
				* Titanfall2StreamingSettings.SkyboxModelScale;
			extent = MathF.Max( extent, offset + radius );
		}

		return Math.Clamp( Titanfall2StreamingSettings.SkyboxTargetDistance / extent, 0.001f, 4096f );
	}

	void DestroySkybox()
	{
		foreach ( var sceneModel in _sceneModels )
			sceneModel?.Delete();
		_sceneModels.Clear();
		_skyLight?.Delete();
		_skyLight = null;
		_skySkyObject?.Delete();
		_skySkyObject = null;

		if ( _skyboxBridge is not null )
		{
			try
			{
				InvokeBridge( "Delete" );
			}
			catch ( Exception exception )
			{
				Log.Warning( exception, "Unable to detach the Titanfall 2 3D skybox world." );
			}
		}
		_skyboxBridge = null;
		_skyboxBridgeType = null;
		_bridgeOrigin = null;
		_updateBridge = null;
		_lastViewPosition = null;
		_skyboxWorld?.Delete();
		_skyboxWorld = null;
	}

	void SetBridgeProperty( string name, object value )
	{
		var property = _skyboxBridgeType?.GetProperty( name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );
		if ( property is null || !property.CanWrite )
			throw new MissingMemberException( _skyboxBridgeType?.FullName, name );
		property.SetValue( _skyboxBridge, value );
	}

	void InvokeBridge( string name )
	{
		var method = _skyboxBridgeType?.GetMethod( name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic );
		if ( method is null ) throw new MissingMethodException( _skyboxBridgeType?.FullName, name );
		method.Invoke( _skyboxBridge, null );
	}

	static string Encode( IReadOnlyList<Titanfall2SkyboxModel> source )
	{
		var models = source?.Where( static model => !string.IsNullOrWhiteSpace( model.ModelPath ) ).ToArray()
			?? Array.Empty<Titanfall2SkyboxModel>();
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, leaveOpen: true ) )
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
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), writable: false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, leaveOpen: false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Skybox data has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( "Skybox data has an unsupported version." );
		var count = reader.ReadInt32();
		if ( count < 0 || count > MaximumModelCount ) throw new InvalidDataException( $"Invalid skybox model count {count}." );

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

readonly record struct Titanfall2SkyboxModel( string ModelPath, Vector3 Position, Angles Rotation, float Scale );

readonly record struct Titanfall2SkyboxFog(
	bool Enabled,
	Color Color,
	float StartDistance,
	float EndDistance,
	float MaximumOpacity )
{
	public Titanfall2FogParameters Parameters { get; init; }
	public bool UseWorldFog { get; init; }
	public static Titanfall2SkyboxFog Disabled => new( false, Color.White, 0f, 1f, 0f );
}

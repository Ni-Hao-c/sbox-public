using System.Reflection;
using Sandbox.Rendering;

/// <summary>
/// Hosts Titanfall 2 vista models in the engine's native 3D skybox world. The
/// native bridge is internal to Sandbox.Engine, so the mounting layer discovers
/// it at runtime without changing the engine assembly.
/// </summary>
[Library, Hide]
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
	SceneDirectionalLight _skyLight;
	object _skyboxBridge;
	Type _skyboxBridgeType;

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
		if ( _skyboxBridge is null ) return;
		// SetData allocates a dynamic constant buffer. Refresh it even when the
		// camera is stationary; a buffer from scene creation is not frame history.
		var fog = new Titanfall2FogParameters( FogDensity, FogBaseColor, FogDirectionalColor, FogDirection, FogHeight );
		if ( UseWorldFog )
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
		// The native bridge uses the final render view for each camera, including
		// transient weapon effects. Keep its authored origin fixed: cancelling a
		// different camera snapshot here introduces shaking and removes height parallax.
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

			// Load every selected vista before attaching its world to the main scene.
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

			var scale = float.IsFinite( SkyScale ) ? Math.Clamp( SkyScale, 1f, 100000f ) : 1000f;
			_skyboxBridge = constructor.Invoke( [parentWorld, _skyboxWorld] );
			SetBridgeProperty( "CameraOrigin", CameraOrigin );
			SetBridgeProperty( "Origin", CameraOrigin );
			SetBridgeProperty( "Angles", CameraAngles );
			SetBridgeProperty( "Scale", scale );
			InvokeBridge( "Update" );
			OnPreRender();

			_skyLight = new SceneDirectionalLight(
				_skyboxWorld,
				Rotation.From( 50f, -35f, 0f ),
				new Color( 1.0f, 0.95f, 0.86f ) )
			{
				ShadowsEnabled = false
			};

			foreach ( var (definition, model) in loadedModels )
			{
				var sceneModel = new SceneModel(
					_skyboxWorld,
					model,
					new Transform( definition.Position, definition.Rotation.ToRotation(),
						MathF.Max( definition.Scale, 0.001f ) ) );
				sceneModel.Flags.IsStatic = true;
				sceneModel.Flags.CastShadows = false;
				// Only the isolated vista uses miniature camera planes. Do not change
				// the player's near plane or normal material previews.
				sceneModel.Attributes.Set( "T2SkyProjectionNear", 0.001f );
				// Draw attributes override the main view's fog: the sky camera has
				// its own authored heights, density and directional scattering.
				new Titanfall2FogParameters( FogDensity, FogBaseColor, FogDirectionalColor, FogDirection, FogHeight )
					.Apply( sceneModel.Attributes );
				// This isolated world has no ModelRenderer to initialize bones/bounds.
				// An uninitialized SceneModel has a point-sized culling box at its origin,
				// so turning away from that point incorrectly hides the entire panorama.
				sceneModel.UpdateToBindPose();
				sceneModel.Bounds = model.Bounds.Transform( sceneModel.Transform );
				_sceneModels.Add( sceneModel );
			}

			Log.Info( $"Titanfall 2 3D skybox ready: {_sceneModels.Count}/{definitions.Length} vista models, "
				+ $"camera '{CameraName}', origin {CameraOrigin}, scale {scale:0.####}, "
				+ $"{(UseWorldFog ? "world fog" : "sky_camera fog")}, {failures} failures ({MapPath})." );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to create Titanfall 2 3D skybox for '{MapPath}'." );
			DestroySkybox();
		}
	}

	void DestroySkybox()
	{
		foreach ( var sceneModel in _sceneModels )
			sceneModel?.Delete();
		_sceneModels.Clear();
		_skyLight?.Delete();
		_skyLight = null;

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

/// <summary>sky_camera fog for the isolated skybox world; <see cref="UseWorldFog"/> follows the map's height fog instead.</summary>
readonly record struct Titanfall2SkyboxFog( Titanfall2FogParameters Parameters, bool UseWorldFog )
{
	public static Titanfall2SkyboxFog Disabled => default;
}

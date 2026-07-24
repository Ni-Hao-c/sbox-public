using System.Diagnostics;
using Titanfall2;

/// <summary>
/// Progressively loads every Titanfall 2 BSP static prop. Cells near the active
/// camera are prioritized, then the remaining map cells are populated using the
/// same frame budget. Spawned props are retained until the scene is destroyed.
/// </summary>
[Library]
public sealed class Titanfall2StaticPropStreamer : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Props" );
	const int DataMagic = 0x32505354; // TSP2
	const int DataVersion = 1;
	const int MaximumModelCount = 65536;
	const int MaximumPropCount = 1_000_000;

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string MapPath { get; set; }

	[Property, Hide]
	public Vector3 FallbackAnchor { get; set; }

	[Property, Hide]
	public string EncodedProps { get; set; }

	readonly Dictionary<CellCoordinate, PropCell> _cells = new();
	readonly List<PropCell> _workQueue = new();
	readonly Stopwatch _refreshTimer = new();
	readonly Stopwatch _statusTimer = new();
	Vector3 _lastAnchor;
	bool _hasAnchor;
	bool _initialized;
	bool _completionLogged;
	bool _batchesBuilt;
	int _totalProps;
	int _activeCells;
	int _liveProps;
	int _failedProps;
	int _failureLogs;
	int _skinnedProps;
	int _plainProps;
	int _instancedProps;
	int _batchCount;
	int _renderingProps;
	int _shadowCastingProps;

	public void Configure( string mountIdent, string mapPath, Vector3 fallbackAnchor,
		IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props )
	{
		MountIdent = mountIdent;
		MapPath = mapPath;
		FallbackAnchor = fallbackAnchor;
		EncodedProps = Encode( props );
	}

	protected override void OnAwake()
	{
		InitializeCells();
	}

	protected override async Task OnLoad( LoadingContext context )
	{
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 || _totalProps == 0 ) return;

		var loadTimer = Stopwatch.StartNew();
		context.Title = $"Loading Titanfall 2 static props (0/{_totalProps})";
		// The active camera is not guaranteed to exist while the SceneFile is being
		// deserialized. The first map spawn remains a deterministic priority anchor.
		RefreshCells( FallbackAnchor );

		while ( _liveProps + _failedProps < _totalProps )
		{
			PopulateCells(
				Titanfall2StreamingSettings.PropPreloadPerYield,
				Titanfall2StreamingSettings.PropPreloadBudgetMilliseconds );
			var processed = _liveProps + _failedProps;
			context.Title = $"Loading Titanfall 2 static props ({processed}/{_totalProps})";
			if ( processed < _totalProps ) await Task.Yield();
		}

		LogCompletion();
		Log.Info( $"Titanfall 2 static props finished during scene loading in "
			+ $"{loadTimer.Elapsed.TotalSeconds:0.00}s; gameplay can now start ({MapPath})." );
	}

	protected override void OnUpdate()
	{
		if ( !_initialized ) InitializeCells();
		if ( !_initialized || _cells.Count == 0 ) return;

		var anchor = Scene?.Camera.IsValid() == true
			? Scene.Camera.WorldPosition
			: FallbackAnchor;
		var refreshDistance = Titanfall2StreamingSettings.PropCellSize * 0.25f;
		if ( !_hasAnchor
			|| _refreshTimer.Elapsed.TotalSeconds >= 0.25
			|| anchor.DistanceSquared( _lastAnchor ) >= refreshDistance * refreshDistance )
		{
			RefreshCells( anchor );
		}

		PopulateCells(
			Titanfall2StreamingSettings.PropsPerFrame,
			Titanfall2StreamingSettings.PropFrameBudgetMilliseconds );
		LogCompletion();
		if ( !_completionLogged && _statusTimer.Elapsed.TotalSeconds >= 5.0 )
		{
			var processed = _liveProps + _failedProps;
			var queued = Math.Max( 0, _totalProps - processed );
			Log.Info( $"Titanfall 2 prop streaming: {_liveProps} retained, {queued} queued, "
				+ $"{_activeCells}/{_cells.Count} cells activated, {_failedProps} skipped ({MapPath})." );
			_statusTimer.Restart();
		}
	}

	protected override void OnDestroy()
	{
		foreach ( var cell in _cells.Values )
			DestroyCellInstances( cell );
		_cells.Clear();
		_workQueue.Clear();
	}

	void InitializeCells()
	{
		if ( _initialized ) return;
		_initialized = true;
		if ( string.IsNullOrWhiteSpace( EncodedProps ) ) return;

		try
		{
			foreach ( var prop in Decode( EncodedProps ) )
			{
				var coordinate = CellCoordinate.FromPosition( prop.Position, Titanfall2StreamingSettings.PropCellSize );
				if ( !_cells.TryGetValue( coordinate, out var cell ) )
				{
					cell = new PropCell( coordinate );
					_cells.Add( coordinate, cell );
				}
				cell.Props.Add( prop );
			}
			_totalProps = _cells.Values.Sum( static cell => cell.Props.Count );

			_refreshTimer.Start();
			_statusTimer.Start();
			Log.Info( $"Titanfall 2 prop streamer ready: {_totalProps} props in "
				+ $"{_cells.Count} cells; prioritize within {Titanfall2StreamingSettings.PropLoadRadius:0}, "
				+ "then load the full map and retain all loaded props until scene teardown, "
				+ $"{Titanfall2StreamingSettings.PropsPerFrame} props/frame, "
				+ $"{Titanfall2StreamingSettings.PropFrameBudgetMilliseconds:0.0} ms budget ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_cells.Clear();
			Log.Warning( $"Failed to initialize Titanfall 2 prop streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void RefreshCells( Vector3 anchor )
	{
		_lastAnchor = anchor;
		_hasAnchor = true;
		_refreshTimer.Restart();
		var loadDistanceSquared = Titanfall2StreamingSettings.PropLoadRadius * Titanfall2StreamingSettings.PropLoadRadius;

		foreach ( var cell in _cells.Values )
		{
			cell.DistanceSquared = cell.Coordinate.DistanceSquaredToCell( anchor, Titanfall2StreamingSettings.PropCellSize );
			cell.Wanted = cell.DistanceSquared <= loadDistanceSquared;
			UpdateCellShadowState( cell, anchor );
		}

		_workQueue.Clear();
		_workQueue.AddRange( _cells.Values.Where( static cell => cell.NextProp < cell.Props.Count ) );
		_workQueue.Sort( static ( left, right ) =>
		{
			var priority = right.Wanted.CompareTo( left.Wanted );
			return priority != 0 ? priority : left.DistanceSquared.CompareTo( right.DistanceSquared );
		} );
	}

	void PopulateCells( int maximumProps, float budgetMilliseconds )
	{
		if ( _workQueue.Count == 0 ) return;
		var timer = Stopwatch.StartNew();
		var attempted = 0;
		while ( attempted < maximumProps && _workQueue.Count > 0 )
		{
			var cell = _workQueue[0];
			if ( cell.NextProp >= cell.Props.Count )
			{
				_workQueue.RemoveAt( 0 );
				continue;
			}
			if ( !cell.Active )
			{
				cell.Active = true;
				_activeCells++;
			}

			var prop = cell.Props[cell.NextProp++];
			attempted++;
			if ( SpawnProp( cell, prop ) )
			{
				_liveProps++;
			}
			else
			{
				_failedProps++;
			}

			if ( cell.NextProp >= cell.Props.Count )
				_workQueue.RemoveAt( 0 );
			if ( timer.Elapsed.TotalMilliseconds >= budgetMilliseconds )
				break;
		}
	}

	void LogCompletion()
	{
		if ( _completionLogged || _totalProps <= 0 || _liveProps + _failedProps < _totalProps ) return;
		BuildPendingBatches();
		_completionLogged = true;
		Log.Info( $"Titanfall 2 prop streaming complete: {_liveProps}/{_totalProps} retained in "
			+ $"{_activeCells}/{_cells.Count} cells, {_instancedProps} GPU-instanced in {_batchCount} batches, "
			+ $"{_skinnedProps} frozen-skinned fallbacks, {_plainProps} individual rigid, "
			+ $"{_renderingProps} rendering, {_shadowCastingProps} casting shadows, "
			+ $"{_failedProps} skipped ({MapPath})." );
	}

	bool SpawnProp( PropCell cell, StreamedProp prop )
	{
		if ( string.IsNullOrWhiteSpace( prop.ModelPath ) || string.IsNullOrWhiteSpace( MountIdent ) ) return false;
		var resourcePath = $"mount://{MountIdent}/{prop.ModelPath}.vmdl";
		var staticResourcePath = $"mount://{MountIdent}/{ModelLoader.GetStaticInstancePath( prop.ModelPath )}.vmdl";
		var staticModel = Model.Load( staticResourcePath );
		var model = staticModel is not null && staticModel != Model.Error
			? staticModel
			: Model.Load( resourcePath );
		if ( model is null || model == Model.Error )
		{
			if ( _failureLogs++ < 16 ) Log.Warning( $"Unable to stream Titanfall 2 static prop model '{resourcePath}'." );
			return false;
		}

		var scale = MathF.Max( prop.Scale, 0.001f );
		var radius = model.Bounds.Size.Length * 0.5f * scale;
		var castShadows = ShouldCastShadows( prop.Position, radius, _lastAnchor );
		const bool renderingEnabled = true;
		var worldTransform = new Transform( prop.Position, prop.Rotation.ToRotation(), scale );
		SceneObject renderObject = null;
		var isBatched = staticModel.IsValid() && staticModel != Model.Error && ModelLoader.CanInstance( staticModel );

		// Opaque/cutout static variants contain bind-pose vertices without a
		// skeleton, so identical models can share one instanced draw inside their
		// permanent BSP cell. Special transparent/decal/water models stay as
		// individual native objects to preserve material pass ordering.
		if ( isBatched )
		{
			if ( !cell.Batches.TryGetValue( staticModel, out var pendingBatch ) )
			{
				pendingBatch = new PendingBatch( staticModel );
				cell.Batches.Add( staticModel, pendingBatch );
			}
			pendingBatch.Add( worldTransform, radius, castShadows );
			_instancedProps++;
		}
		else if ( staticModel.IsValid() && staticModel != Model.Error )
		{
			renderObject = new SceneObject( Scene.SceneWorld, staticModel, worldTransform );
			_plainProps++;
		}
		else if ( model.BoneCount > 0 || model.AnimationCount > 0 )
		{
			var skinned = new SceneModel( Scene.SceneWorld, model, worldTransform )
			{
				UseAnimGraph = false,
				PlaybackRate = 0f
			};
			skinned.UpdateToBindPose();
			renderObject = skinned;
			_skinnedProps++;
		}
		else
		{
			renderObject = new SceneObject( Scene.SceneWorld, model, worldTransform );
			_plainProps++;
		}
		if ( renderObject.IsValid() )
		{
			renderObject.Flags.IsStatic = true;
			renderObject.Flags.CastShadows = castShadows;
			renderObject.RenderingEnabled = renderingEnabled;
		}

		if ( renderingEnabled ) _renderingProps++;
		if ( castShadows ) _shadowCastingProps++;
		GameObject collisionObject = null;
		if ( (Titanfall2StreamingSettings.PropCollisions || Titanfall2StreamingSettings.NavMeshStaticProps) && prop.Collidable )
		{
			collisionObject = new GameObject( GameObject, true, System.IO.Path.GetFileNameWithoutExtension( prop.ModelPath ) );
			collisionObject.WorldPosition = prop.Position;
			collisionObject.WorldRotation = prop.Rotation;
			collisionObject.WorldScale = Vector3.One * scale;
			collisionObject.IsStatic = true;
			if ( Titanfall2StreamingSettings.NavMeshStaticProps )
				collisionObject.Tags.Add( Titanfall2DeferredNavMeshBuilder.NavMeshBodyTag );
			var collider = collisionObject.AddComponent<ModelCollider>();
			var collisionModel = Model.Load( resourcePath );
			collider.Model = collisionModel.IsValid() && collisionModel != Model.Error ? collisionModel : model;
			collider.Static = true;
		}
		cell.Instances.Add( new PropInstance(
			collisionObject, renderObject, prop.Position, radius, renderingEnabled, castShadows, isBatched ) );
		return true;
	}

	void UpdateCellShadowState( PropCell cell, Vector3 anchor )
	{
		foreach ( var instance in cell.Instances )
		{
			if ( instance.IsBatched ) continue;
			var castShadows = ShouldCastShadows( instance.Position, instance.Radius, anchor );
			if ( castShadows != instance.CastShadows )
			{
				instance.CastShadows = castShadows;
				if ( instance.RenderObject.IsValid() )
					instance.RenderObject.Flags.CastShadows = castShadows;
				_shadowCastingProps += castShadows ? 1 : -1;
			}
		}

		foreach ( var pendingBatch in cell.Batches.Values )
		{
			var nearCount = 0;
			for ( var index = 0; index < pendingBatch.Transforms.Count; ++index )
			{
				if ( ShouldCastShadows(
					pendingBatch.Transforms[index].Position,
					pendingBatch.Radii[index],
					anchor ) )
				{
					nearCount++;
				}
			}

			// A batch has one shadow-pass flag. If any instance in this cell/model
			// group is close enough, the permanent batch casts as a whole.
			var desiredCount = pendingBatch.RenderBatch.IsValid()
				? (nearCount > 0 ? pendingBatch.Transforms.Count : 0)
				: nearCount;
			if ( pendingBatch.RenderBatch.IsValid() )
				pendingBatch.RenderBatch.CastShadows = desiredCount > 0;
			else if ( pendingBatch.FallbackObjects.Count == pendingBatch.Transforms.Count )
			{
				for ( var index = 0; index < pendingBatch.FallbackObjects.Count; ++index )
					pendingBatch.FallbackObjects[index].Flags.CastShadows = ShouldCastShadows(
						pendingBatch.Transforms[index].Position,
						pendingBatch.Radii[index],
						anchor );
			}

			_shadowCastingProps += desiredCount - pendingBatch.ShadowedInstanceCount;
			pendingBatch.ShadowedInstanceCount = desiredCount;
		}
	}

	void BuildPendingBatches()
	{
		if ( _batchesBuilt ) return;
		_batchesBuilt = true;

		foreach ( var cell in _cells.Values )
		{
			foreach ( var pendingBatch in cell.Batches.Values )
			{
				if ( pendingBatch.Transforms.Count == 0 ) continue;
				var castShadows = pendingBatch.ShadowedInstanceCount > 0;
				try
				{
					pendingBatch.RenderBatch = new Titanfall2StaticModelBatch(
						Scene.SceneWorld,
						pendingBatch.Model,
						pendingBatch.Transforms,
						castShadows );
					_batchCount++;

					var batchedShadowCount = castShadows ? pendingBatch.Transforms.Count : 0;
					_shadowCastingProps += batchedShadowCount - pendingBatch.ShadowedInstanceCount;
					pendingBatch.ShadowedInstanceCount = batchedShadowCount;
				}
				catch ( Exception exception )
				{
					Log.Warning( exception, $"Unable to instance Titanfall 2 static model '{pendingBatch.Model.Name}'; "
						+ "retaining permanent individual objects." );
					for ( var index = 0; index < pendingBatch.Transforms.Count; ++index )
					{
						var fallback = new SceneObject(
							Scene.SceneWorld,
							pendingBatch.Model,
							pendingBatch.Transforms[index] );
						fallback.Flags.IsStatic = true;
						fallback.Flags.CastShadows = ShouldCastShadows(
							pendingBatch.Transforms[index].Position,
							pendingBatch.Radii[index],
							_lastAnchor );
						pendingBatch.FallbackObjects.Add( fallback );
					}
				}
			}
		}
	}

	static bool ShouldCastShadows( Vector3 position, float radius, Vector3 anchor )
	{
		if ( radius < Titanfall2StreamingSettings.PropShadowMinimumRadius ) return false;
		var distance = Titanfall2StreamingSettings.PropShadowDistance + radius;
		return position.DistanceSquared( anchor ) <= distance * distance;
	}

	void DestroyCellInstances( PropCell cell )
	{
		if ( !cell.Active && cell.Instances.Count == 0 && cell.Batches.Count == 0 ) return;
		foreach ( var pendingBatch in cell.Batches.Values )
		{
			_shadowCastingProps = Math.Max( 0, _shadowCastingProps - pendingBatch.ShadowedInstanceCount );
			pendingBatch.RenderBatch?.Delete();
			foreach ( var fallback in pendingBatch.FallbackObjects )
				fallback?.Delete();
		}
		cell.Batches.Clear();

		foreach ( var instance in cell.Instances )
		{
			if ( instance.RenderingEnabled ) _renderingProps = Math.Max( 0, _renderingProps - 1 );
			if ( !instance.IsBatched && instance.CastShadows )
				_shadowCastingProps = Math.Max( 0, _shadowCastingProps - 1 );
			instance.RenderObject?.Delete();
			if ( instance.CollisionObject.IsValid() && !instance.CollisionObject.IsDestroyed ) instance.CollisionObject.Destroy();
		}
		_liveProps = Math.Max( 0, _liveProps - cell.Instances.Count );
		cell.Instances.Clear();
		cell.NextProp = 0;
		cell.Active = false;
		cell.Wanted = false;
		_activeCells = Math.Max( 0, _activeCells - 1 );
	}

	static string Encode( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> source )
	{
		var valid = source.Where( static prop => !string.IsNullOrWhiteSpace( prop.ModelPath ) ).ToArray();
		var models = valid.Select( static prop => NormalizeModelPath( prop.ModelPath ) )
			.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
		var indices = models.Select( static ( path, index ) => (path, index) )
			.ToDictionary( static pair => pair.path, static pair => pair.index, StringComparer.OrdinalIgnoreCase );

		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( DataMagic );
			writer.Write( DataVersion );
			writer.Write( models.Length );
			foreach ( var model in models ) writer.Write( model );
			writer.Write( valid.Length );
			foreach ( var prop in valid )
			{
				writer.Write( indices[NormalizeModelPath( prop.ModelPath )] );
				writer.Write( prop.Origin.X );
				writer.Write( prop.Origin.Y );
				writer.Write( prop.Origin.Z );
				writer.Write( prop.Angles.X );
				writer.Write( prop.Angles.Y );
				writer.Write( prop.Angles.Z );
				writer.Write( prop.Scale );
				writer.Write( prop.IsCollidable );
			}
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<StreamedProp> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Static prop data has an invalid signature." );
		if ( reader.ReadInt32() != DataVersion ) throw new InvalidDataException( "Static prop data has an unsupported version." );
		var modelCount = reader.ReadInt32();
		if ( modelCount < 0 || modelCount > MaximumModelCount ) throw new InvalidDataException( $"Invalid model count {modelCount}." );
		var models = new string[modelCount];
		for ( var index = 0; index < models.Length; index++ ) models[index] = reader.ReadString();

		var propCount = reader.ReadInt32();
		if ( propCount < 0 || propCount > MaximumPropCount ) throw new InvalidDataException( $"Invalid prop count {propCount}." );
		for ( var index = 0; index < propCount; index++ )
		{
			var modelIndex = reader.ReadInt32();
			if ( modelIndex < 0 || modelIndex >= models.Length ) throw new InvalidDataException( $"Invalid model index {modelIndex}." );
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var scale = reader.ReadSingle();
			var collidable = reader.ReadBoolean();
			yield return new StreamedProp( models[modelIndex], position, rotation, scale, collidable );
		}
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? normalized[..^5] : normalized;
	}

	readonly record struct StreamedProp( string ModelPath, Vector3 Position, Angles Rotation, float Scale, bool Collidable );

	sealed class PropInstance(
		GameObject collisionObject,
		SceneObject renderObject,
		Vector3 position,
		float radius,
		bool renderingEnabled,
		bool castShadows,
		bool isBatched )
	{
		public GameObject CollisionObject { get; } = collisionObject;
		public SceneObject RenderObject { get; } = renderObject;
		public Vector3 Position { get; } = position;
		public float Radius { get; } = radius;
		public bool RenderingEnabled { get; set; } = renderingEnabled;
		public bool CastShadows { get; set; } = castShadows;
		public bool IsBatched { get; } = isBatched;
	}

	sealed class PendingBatch( Model model )
	{
		public Model Model { get; } = model;
		public List<Transform> Transforms { get; } = new();
		public List<float> Radii { get; } = new();
		public List<SceneObject> FallbackObjects { get; } = new();
		public Titanfall2StaticModelBatch RenderBatch { get; set; }
		public int ShadowedInstanceCount { get; set; }

		public void Add( Transform transform, float radius, bool castShadows )
		{
			Transforms.Add( transform );
			Radii.Add( radius );
			if ( castShadows ) ShadowedInstanceCount++;
		}
	}

	readonly record struct CellCoordinate( int X, int Y )
	{
		public static CellCoordinate FromPosition( Vector3 position, float size ) => new(
			(int)MathF.Floor( position.x / size ),
			(int)MathF.Floor( position.y / size ) );

		public float DistanceSquaredToCell( Vector3 point, float size )
		{
			var minimumX = X * size;
			var minimumY = Y * size;
			var maximumX = minimumX + size;
			var maximumY = minimumY + size;
			var deltaX = point.x < minimumX ? minimumX - point.x : point.x > maximumX ? point.x - maximumX : 0f;
			var deltaY = point.y < minimumY ? minimumY - point.y : point.y > maximumY ? point.y - maximumY : 0f;
			return deltaX * deltaX + deltaY * deltaY;
		}
	}

	sealed class PropCell( CellCoordinate coordinate )
	{
		public CellCoordinate Coordinate { get; } = coordinate;
		public List<StreamedProp> Props { get; } = new();
		public List<PropInstance> Instances { get; } = new();
		public Dictionary<Model, PendingBatch> Batches { get; } = new();
		public int NextProp { get; set; }
		public float DistanceSquared { get; set; }
		public bool Active { get; set; }
		public bool Wanted { get; set; }
	}
}

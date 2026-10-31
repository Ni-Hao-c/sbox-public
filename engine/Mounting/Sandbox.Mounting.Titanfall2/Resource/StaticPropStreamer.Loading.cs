using System.Diagnostics;
using Sandbox.Mounting.PerfKit;
using Titanfall2;

public sealed partial class Titanfall2StaticPropStreamer
{
	/// <summary>
	/// Restores the CellBSP tree when the component was recreated from scene state.
	/// Rendering a mounted map instantiates the prop streamer twice: the first instance
	/// is configured directly and receives the tree by reference, the second is built
	/// from serialized properties and only sees <see cref="EncodedVisibility"/>. The
	/// measured symptom of not restoring it was a PVS diagnosis reporting
	/// cells=0/planes=0/nodes=0 while the map itself had parsed 74 cells and 712 portals.
	/// </summary>
	void EnsureVisibility()
	{
		if ( _visibility is { IsValid: true } ) return;

		if ( string.IsNullOrWhiteSpace( EncodedVisibility ) )
		{
			_visibilityRestoreFailed = true;
			return;
		}

		try
		{
			_visibility = DecodeVisibility( EncodedVisibility );
			_visibilityRestoreFailed = _visibility is null;
		}
		catch ( Exception exception )
		{
			_visibilityRestoreFailed = true;
			Log.Warning( exception, $"Unable to restore Titanfall 2 PVS data for '{MapPath}'." );
		}
	}

	static string EncodeVisibility( Titanfall2BspReader.BspVisibility visibility )
	{
		using var stream = new MemoryStream();
		using ( var writer = new BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
		{
			writer.Write( VisibilityMagic );
			writer.Write( VisibilityVersion );
			var usable = visibility is { IsValid: true };
			writer.Write( usable );
			if ( usable )
			{
				writer.Write( visibility.CellCount );
				writer.Write( visibility.AlwaysVisibleCells.Low );
				writer.Write( visibility.AlwaysVisibleCells.High );

				writer.Write( visibility.Planes.Count );
				foreach ( var plane in visibility.Planes )
				{
					writer.Write( plane.Normal.X );
					writer.Write( plane.Normal.Y );
					writer.Write( plane.Normal.Z );
					writer.Write( plane.Distance );
				}

				writer.Write( visibility.Nodes.Count );
				foreach ( var node in visibility.Nodes )
				{
					writer.Write( node.Plane );
					writer.Write( node.Child );
				}

				writer.Write( visibility.Portals.Count );
				foreach ( var portal in visibility.Portals )
				{
					writer.Write( portal.FromCell );
					writer.Write( portal.ToCell );
				}
			}
		}

		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	/// <summary>
	/// True when <paramref name="count"/> elements of <paramref name="elementBytes"/> each
	/// still fit in what is left of the payload.
	/// </summary>
	static bool HasRoomFor( MemoryStream stream, int count, int elementBytes )
	{
		if ( count < 0 ) return false;
		var remaining = stream.Length - stream.Position;
		return remaining >= 0 && (long)count * elementBytes <= remaining;
	}

	static Titanfall2BspReader.BspVisibility DecodeVisibility( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != VisibilityMagic ) throw new InvalidDataException( "Visibility data has an invalid signature." );
		if ( reader.ReadInt32() != VisibilityVersion ) throw new InvalidDataException( "Visibility data has an unsupported version." );
		if ( !reader.ReadBoolean() ) return null;

		var cellCount = reader.ReadInt32();
		var alwaysVisible = new Titanfall2BspReader.VisibilityCellMask( reader.ReadUInt64(), reader.ReadUInt64() );

		// The element counts are validated against the bytes actually left in the payload
		// rather than against a fixed limit. A fixed limit cannot be right for every map:
		// Angel City's CellBSP carries 45207 planes, which an arbitrary 4096 guard rejected
		// as corrupt even though the payload was intact.
		var planeCount = reader.ReadInt32();
		if ( !HasRoomFor( stream, planeCount, PlaneBytes ) ) throw new InvalidDataException( $"Invalid plane count {planeCount}." );
		var planes = new Titanfall2BspReader.VisibilityPlane[planeCount];
		for ( var index = 0; index < planeCount; index++ )
		{
			var normal = new System.Numerics.Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			planes[index] = new Titanfall2BspReader.VisibilityPlane( normal, reader.ReadSingle() );
		}

		var nodeCount = reader.ReadInt32();
		if ( !HasRoomFor( stream, nodeCount, NodeBytes ) ) throw new InvalidDataException( $"Invalid node count {nodeCount}." );
		var nodes = new Titanfall2BspReader.VisibilityBspNode[nodeCount];
		for ( var index = 0; index < nodeCount; index++ )
			nodes[index] = new Titanfall2BspReader.VisibilityBspNode( reader.ReadInt32(), reader.ReadInt32() );

		var portalCount = reader.ReadInt32();
		if ( !HasRoomFor( stream, portalCount, PortalBytes ) ) throw new InvalidDataException( $"Invalid portal count {portalCount}." );
		var portals = new Titanfall2BspReader.VisibilityPortal[portalCount];
		for ( var index = 0; index < portalCount; index++ )
		{
			// Only the cell pair is needed: GetReachableCells walks FromCell/ToCell and
			// never reads the portal bounds.
			portals[index] = new Titanfall2BspReader.VisibilityPortal(
				reader.ReadInt32(), reader.ReadInt32(), default, default, false );
		}

		return new Titanfall2BspReader.BspVisibility
		{
			CellCount = cellCount,
			AlwaysVisibleCells = alwaysVisible,
			Planes = planes,
			Nodes = nodes,
			Portals = portals
		};
	}

	void InitializeCells()
	{
		if ( _initialized ) return;
		_initialized = true;
		EnsureVisibility();
		if ( string.IsNullOrWhiteSpace( EncodedProps ) ) return;

		try
		{
			_localLights = Titanfall2LocalLightBuffer.Restore( EncodedLocalLights );
			_propsWithPvsCells = 0;
			foreach ( var prop in Decode( EncodedProps ) )
			{
				var coordinate = CellCoordinate.FromPosition( prop.Position, CellSize );
				if ( !_cells.TryGetValue( coordinate, out var cell ) )
				{
					var clusterCoordinate = ClusterCoordinate.FromCell( coordinate, BatchClusterCells );
					if ( !_clusters.TryGetValue( clusterCoordinate, out var cluster ) )
					{
						cluster = new PropCluster( clusterCoordinate );
						_clusters.Add( clusterCoordinate, cluster );
					}

					cell = new PropCell( coordinate, cluster );
					_cells.Add( coordinate, cell );
					cluster.Cells.Add( cell );
				}
				cell.Props.Add( prop );
				if ( !prop.PvsCells.IsEmpty ) _propsWithPvsCells++;
			}
			_totalProps = _cells.Values.Sum( static cell => cell.Props.Count );

			_refreshTimer.Start();
			_statusTimer.Start();
			Log.Info( $"Titanfall 2 prop streamer ready: {_totalProps} props in "
				+ $"{_cells.Count} cells; prioritize within {LoadRadius:0}, "
				+ "then load the full map and retain all loaded props until scene teardown, "
				+ $"{PropsPerFrame} props/frame, "
				+ $"{FrameBudgetMilliseconds:0.0} ms budget ({MapPath})." );
			Log.Info( $"Titanfall 2 prop batch clustering: {_cells.Count} prop cells grouped into "
				+ $"{_clusters.Count} clusters of up to {BatchClusterCells}x{BatchClusterCells} cells each ({MapPath})." );
		}
		catch ( Exception exception )
		{
			_initializationFailed = true;
			_cells.Clear();
			_clusters.Clear();
			_totalProps = 0;
			Log.Warning( $"Failed to initialize Titanfall 2 prop streaming for '{MapPath}': {exception.Message}" );
		}
	}

	void RefreshCells( Vector3 anchor )
	{
		_lastAnchor = anchor;
		_hasAnchor = true;
		_refreshTimer.Restart();
		var loadDistanceSquared = LoadRadius * LoadRadius;
		_visiblePvsCells = ResolveVisiblePvsCells( anchor );

		// Distance culling is applied per instance and per batch (UpdateClusterVisibilityState),
		// never per cell: a cell can contain a large landmark that must stay visible while its
		// neighbours are culled.
		foreach ( var cluster in _clusters.Values )
			cluster.DistanceSquared = float.MaxValue;

		foreach ( var cell in _cells.Values )
		{
			cell.DistanceSquared = cell.Coordinate.DistanceSquaredToCell( anchor, CellSize );
			cell.Wanted = cell.DistanceSquared <= loadDistanceSquared;

			var cluster = cell.Cluster;
			if ( cell.DistanceSquared < cluster.DistanceSquared ) cluster.DistanceSquared = cell.DistanceSquared;
		}

		// Instance distances can cross the boundary without changing a cell or PVS.
		_runtimePvsUpdatePending = true;
		QueueRuntimeCellRefresh();

		if ( _completionLogged ) return;
		_workQueue.Clear();
		_workQueue.AddRange( _cells.Values.Where( static cell => cell.NextProp < cell.Props.Count ) );
		_workQueue.Sort( static ( left, right ) =>
		{
			var priority = right.Wanted.CompareTo( left.Wanted );
			return priority != 0 ? priority : left.DistanceSquared.CompareTo( right.DistanceSquared );
		} );
	}

	// The state pass is never rewound while it is walking: its inputs (cell distance and
	// render state) are already updated in place by RefreshCells, so only the visit order
	// is deferred. Restarting it from the nearest cluster on every refresh — which a player
	// moving 128 units triggers — kept it from ever reaching the far end of the map and
	// left _runtimePvsUpdatePending stuck at true.

	void RebuildRuntimeClusterQueue()
	{
		_runtimeClusterQueue.Clear();
		// Built in cell order before sorting, so each cluster appears once.
		foreach ( var cell in _cells.Values )
		{
			var cluster = cell.Cluster;
			if ( cluster.QueueMarked ) continue;
			cluster.QueueMarked = true;
			_runtimeClusterQueue.Add( cluster );
		}
		foreach ( var cluster in _runtimeClusterQueue ) cluster.QueueMarked = false;
		_runtimeClusterQueue.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
		_runtimeClusterCursor = 0;
		_runtimeQueueRefreshPending = false;
	}

	void RebuildCollisionQueue()
	{
		_collisionCellQueue.Clear();
		_collisionCellQueue.AddRange( _cells.Values );
		_collisionCellQueue.Sort( static ( left, right ) => left.DistanceSquared.CompareTo( right.DistanceSquared ) );
		_collisionCellCursor = 0;
		// A refresh re-evaluates every cell from the start, so any collision pass that
		// was interrupted by the per-frame cap begins again rather than resuming.
		foreach ( var cell in _collisionCellQueue ) cell.CollisionCursor = 0;
	}

	static string Encode( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> source, Titanfall2BspReader.BspVisibility visibility )
		=> EncodeWithFade( source, visibility, null );

	/// <param name="source">World props in BSP order.</param>
	/// <param name="visibility">PVS used to build each prop's cell mask.</param>
	/// <param name="fadeDistances">Optional per-prop fade distance, parallel to <paramref name="source"/>; 0 never fades.</param>
	static string EncodeWithFade( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> source, Titanfall2BspReader.BspVisibility visibility,
		IReadOnlyList<float> fadeDistances )
	{
		var validIndices = Enumerable.Range( 0, source.Count ).Where( index => !string.IsNullOrWhiteSpace( source[index].ModelPath ) ).ToArray();
		var valid = validIndices.Select( index => source[index] ).ToArray();
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
			for ( var validIndex = 0; validIndex < valid.Length; validIndex++ )
			{
				var prop = valid[validIndex];
				var sourceIndex = validIndices[validIndex];
				writer.Write( indices[NormalizeModelPath( prop.ModelPath )] );
				writer.Write( prop.Origin.X );
				writer.Write( prop.Origin.Y );
				writer.Write( prop.Origin.Z );
				writer.Write( prop.Angles.X );
				writer.Write( prop.Angles.Y );
				writer.Write( prop.Angles.Z );
				writer.Write( prop.Scale );
				writer.Write( prop.IsCollidable );
				// Irradiance is separate from modulation of albedo/emission.
				writer.Write( 1f );
				writer.Write( 1f );
				writer.Write( 1f );
				var pvsCells = visibility is { IsValid: true }
					? visibility.GetPropCellMask( prop.SourceIndex, prop.Origin ) : default;
				writer.Write( pvsCells.Low );
				writer.Write( pvsCells.High );
				var probe = Titanfall2ProbeData.FromSource( prop.BakedLightProbe );
				writer.Write( probe.Metadata.x > 0 );
				if ( probe.Metadata.x > 0 )
				{
					void Write( Vector4 v ) { writer.Write( v.x ); writer.Write( v.y ); writer.Write( v.z ); writer.Write( v.w ); }
					Write( probe.Red ); Write( probe.Green ); Write( probe.Blue ); Write( probe.SkyDirectionSunVisibility );
					// Preserve bit patterns as integers; some packed IDs are float NaNs.
					writer.Write( BitConverter.SingleToUInt32Bits( probe.Metadata.y ) );
					writer.Write( BitConverter.SingleToUInt32Bits( probe.Metadata.z ) );
					writer.Write( BitConverter.SingleToUInt32Bits( probe.Metadata.w ) );
				}
				writer.Write( prop.SourceIndex );
				writer.Write( prop.SolidType );
				writer.Write( prop.ReservedCollisionData );
				writer.Write( prop.CollisionFlagsRemove );
				writer.Write( prop.Skin );
				var fade = fadeDistances is not null && sourceIndex < fadeDistances.Count ? fadeDistances[sourceIndex] : 0f;
				writer.Write( float.IsFinite( fade ) && fade > 0f ? fade : 0f );
			}
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	static IEnumerable<StreamedProp> Decode( string encoded )
	{
		using var stream = new MemoryStream( Convert.FromBase64String( encoded ), false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != DataMagic ) throw new InvalidDataException( "Static prop data has an invalid signature." );
		var version = reader.ReadInt32();
		if ( version < 3 || version > DataVersion ) throw new InvalidDataException( "Static prop data has an unsupported version." );
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
			var probeColor = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var pvsCells = new Titanfall2BspReader.VisibilityCellMask( reader.ReadUInt64(), reader.ReadUInt64() );
			var probe = default( Titanfall2ProbeData );
			if ( version >= 4 && reader.ReadBoolean() )
			{
				Vector4 Read() => new( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
				probe = new Titanfall2ProbeData
				{
					Red = Read(),
					Green = Read(),
					Blue = Read(),
					SkyDirectionSunVisibility = Read(),
					Metadata = new Vector4( 1, 0, 0, 0 )
				};
				if ( version >= 5 ) probe.Metadata = new Vector4( 1, BitConverter.UInt32BitsToSingle( reader.ReadUInt32() ),
					BitConverter.UInt32BitsToSingle( reader.ReadUInt32() ), BitConverter.UInt32BitsToSingle( reader.ReadUInt32() ) );
			}
			// Legacy payloads only contain a collision bool; do not invent a solid
			// type or contents mask for them. Reimport supplies the original fields.
			var sourceIndex = version >= 6 ? reader.ReadInt32() : -1;
			var solidType = version >= 6 ? reader.ReadByte() : (byte)0;
			var reservedCollisionData = version >= 6 ? reader.ReadUInt32() : 0u;
			var collisionFlagsRemove = version >= 6 ? reader.ReadUInt32() : 0u;
			var skin = version >= 7 ? reader.ReadInt32() : 0;
			if ( skin < 0 ) throw new InvalidDataException( "Negative static prop skin." );
			// Older payloads carry no fade distance: 0 keeps them on the global render distance.
			var fadeDistance = version >= 8 ? reader.ReadSingle() : 0f;
			if ( !float.IsFinite( fadeDistance ) || fadeDistance < 0f ) fadeDistance = 0f;
			yield return new StreamedProp( models[modelIndex], position, rotation, scale, collidable, probeColor, probe, pvsCells,
				sourceIndex, solidType, reservedCollisionData, collisionFlagsRemove )
			{ Skin = skin, FadeDistance = fadeDistance };
		}
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? normalized[..^5] : normalized;
	}

	void LogCompletion()
	{
		if ( _completionLogged || _totalProps <= 0 || _liveProps + _failedProps < _totalProps ) return;
		BuildPendingBatches();
		_completionLogged = true;
		// The visible/culled counters in this line are only meaningful if they are
		// actually computed: this call was missing, so the line always reported 0/0
		// and made it look as though culling ran and found nothing to hide.
		UpdatePvsStatistics();
		Log.Info( $"Titanfall 2 prop streaming complete: {_liveProps}/{_totalProps} retained in "
			+ $"{_activeCells}/{_cells.Count} cells, {_instancedProps} GPU-instanced in {_batchCount} batches, "
			+ $"{_skinnedProps} frozen-skinned fallbacks, {_plainProps} individual rigid, "
			+ $"{_renderingProps} rendering, {_shadowCastingProps} casting shadows, "
			+ $"{_collidersCreated}/{_collidableProps} colliders active "
			+ $"({_collidersReleased} released, {_collisionFilteredProps} zero-contents, {_colliderFailures} unavailable, {_collidersDeferred} deferred to runtime, "
			+ $"{_collisionResumeCount} cell passes resumed), "
			+ $"PVS cell {_pvsCameraCell} ({_pvsVisibleProps} visible/{_pvsCulledProps} culled), "
			+ $"{_failedProps} skipped ({MapPath})." );
		LogRenderDistanceSummary();
		LogModelFailureSummary();
	}

	/// <summary>
	/// Loads a mounted model and remembers failures. The resource loader does not cache
	/// a failed load, so without this every retry re-reads and re-parses the model: a
	/// model referenced by many instances paid that cost once per instance, and again on
	/// every cell refresh. Failures back off for a while instead of being written off
	/// permanently, so a transient problem can recover.
	/// </summary>
	Model LoadModelWithRetryCache( string resourcePath, int skin = 0, bool collisionOnly = false )
	{
		var failureKey = $"{resourcePath}|{skin}|{collisionOnly}";
		if ( _modelFailureRetryAfter.TryGetValue( failureKey, out var retryAfter ) )
		{
			if ( Titanfall2Log.ElapsedSeconds < retryAfter ) return null;
			_modelFailureRetryAfter.Remove( failureKey );
		}

		Model model;
		try
		{
			model = Sandbox.Mounting.Directory.Get( MountIdent ) is Titanfall2Mount mount
				? mount.LoadModelVariant( resourcePath, skin, collisionOnly: collisionOnly ) : null;
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Unable to load Titanfall 2 prop model '{resourcePath}'." );
			model = null;
		}

		if ( !ModelLoader.IsUsableModel( model ) )
		{
			_modelFailureRetryAfter[failureKey] = Titanfall2Log.ElapsedSeconds + ModelLoader.FailureRetrySeconds;
			return null;
		}

		return model;
	}

	/// <summary>
	/// Reports how many model paths are being skipped because they failed recently.
	/// Without this the only visible symptom of the retry cache is a shorter log.
	/// </summary>
	void LogModelFailureSummary()
	{
		if ( _modelFailureRetryAfter.Count == 0 ) return;
		Log.Info( $"Titanfall 2 model load retry cache: {_modelFailureRetryAfter.Count} distinct paths failed and are "
			+ $"skipped for {ModelLoader.FailureRetrySeconds:0}s each ({MapPath})." );
	}
}

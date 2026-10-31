using Sandbox.Mounting.PerfKit;
using Titanfall2;

/// <summary>
/// Runtime bookkeeping types for <see cref="Titanfall2StaticPropStreamer"/>: one prop instance,
/// the per-(cell, model) pending batch, and the cell/cluster coordinates. Split out of the
/// streamer body so the streaming logic file stays readable.
/// </summary>
public sealed partial class Titanfall2StaticPropStreamer
{
	readonly record struct StreamedProp(
		string ModelPath,
		Vector3 Position,
		Angles Rotation,
		float Scale,
		bool Collidable,
		Vector3 ProbeColor,
		Titanfall2ProbeData Probe,
		Titanfall2BspReader.VisibilityCellMask PvsCells,
		int SourceIndex,
		byte SolidType,
		uint ReservedCollisionData,
		uint CollisionFlagsRemove )
	{
		public int Skin { get; init; }
		/// <summary>Original fade distance (sprp value, else MDL default); 0 means it never fades.</summary>
		public float FadeDistance { get; init; }
	}

	sealed class PropInstance(
		StreamedProp source,
		SceneObject renderObject,
		float radius,
		bool renderingEnabled,
		bool castShadows,
		bool isBatched )
	{
		public StreamedProp Source { get; } = source;
		public GameObject CollisionObject { get; set; }
		public SceneObject RenderObject { get; } = renderObject;
		public Vector3 Position => Source.Position;
		public float Radius { get; } = radius;
		public bool RenderingEnabled { get; set; } = renderingEnabled;
		public bool CastShadows { get; set; } = castShadows;
		public bool IsBatched { get; } = isBatched;
		public bool CollisionUnavailable { get; set; }
		public bool CollisionFiltered { get; set; }
	}

	sealed class PendingBatch( string modelPath, Model model, int currentLod, bool renderingEnabled, Titanfall2PropLodMode lodMode )
	{
		public string ModelPath { get; } = modelPath;
		public int Skin { get; init; }
		public Model Model { get; set; } = model;
		/// <summary>
		/// The key this batch is stored under in its cluster's dictionary. A legacy LOD swap
		/// changes <see cref="Model"/> but not the dictionary entry, so the two can differ;
		/// only a LOD mode rebuild re-keys the entry, and it must remove this key, not Model.
		/// </summary>
		public Model Key { get; set; } = model;
		public int CurrentLod { get; set; } = currentLod;
		public int DesiredLod { get; set; } = currentLod;
		public bool LodSwapQueued { get; set; }
		/// <summary>
		/// Which LOD generation this batch's model belongs to. Held per batch rather than read from
		/// the setting, because the two are only equal once a mode switch has finished rebuilding.
		/// </summary>
		public Titanfall2PropLodMode LodMode { get; set; } = lodMode;
		public bool RenderingEnabled { get; set; } = renderingEnabled;
		public Titanfall2BspReader.VisibilityCellMask PvsCells { get; private set; }
		public List<Transform> Transforms { get; } = new();
		Transform[] _transformArray;
		public List<float> Radii { get; } = new();
		/// <summary>Per-instance <see cref="StreamedProp.FadeDistance"/>, parallel to <see cref="Transforms"/>.</summary>
		public List<float> FadeDistances { get; } = new();
		/// <summary>Per-instance keep distances for the culler, rebuilt when <see cref="KeepDistanceKey"/> changes.</summary>
		public float[] KeepDistances { get; set; }
		public (float RenderDistance, float Hysteresis, bool Authored, float Scale) KeepDistanceKey { get; set; }
		public List<Vector3> ProbeColors { get; } = new();
		public List<Titanfall2ProbeData> Probes { get; } = new();
		public List<SceneObject> FallbackObjects { get; } = new();
		public MountedInstanceBatch RenderBatch { get; set; }
		/// <summary>Reusable instance-distance cull state for this batch's transform array.</summary>
		public MountedInstanceCull Culler { get; } = new();
		public int ShadowedInstanceCount { get; set; }

		public Vector3 AverageProbeColor => ProbeColors.Count == 0
			? Vector3.One
			: ProbeColors.Aggregate( Vector3.Zero, static ( sum, color ) => sum + color ) / ProbeColors.Count;

		/// <summary>
		/// The transform array this batch's instances are drawn with. It is built once and then
		/// shared with every later batch built for the same instance set - LOD swaps are the
		/// common case - which removes the per-swap copy of the whole array. It is only ever
		/// replaced, never written in place, and Transforms stops growing once the batch is
		/// built, so a built batch can never observe it change; the length check is a safety net
		/// that also keeps this correct if a batch were built before loading finished.
		/// </summary>
		public Transform[] GetOrBuildTransformArray()
		{
			if ( _transformArray is null || _transformArray.Length != Transforms.Count )
				_transformArray = Transforms.ToArray();
			return _transformArray;
		}

		public void Add( Transform transform, float radius, bool castShadows, Vector3 probeColor, Titanfall2ProbeData probe,
			Titanfall2BspReader.VisibilityCellMask pvsCells, float fadeDistance = 0f )
		{
			Transforms.Add( transform );
			Radii.Add( radius );
			FadeDistances.Add( fadeDistance );
			ProbeColors.Add( probeColor );
			Probes.Add( probe );
			PvsCells = PvsCells.Union( pvsCells );
			if ( castShadows ) ShadowedInstanceCount++;
		}

		public float DistanceToClosestInstance( Vector3 anchor )
		{
			var result = float.MaxValue;
			foreach ( var transform in Transforms ) result = MathF.Min( result, MathF.Sqrt( transform.Position.DistanceSquared( anchor ) ) );
			return result;
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

	/// <summary>
	/// A group of neighbouring BSP prop cells whose batches are shared. With a group size of
	/// one this is exactly one cell, which is why the default configuration behaves the same
	/// as it did when every batch was stored on the cell itself.
	/// </summary>
	sealed class PropCluster( ClusterCoordinate coordinate )
	{
		public ClusterCoordinate Coordinate { get; } = coordinate;
		public List<PropCell> Cells { get; } = new();
		public Dictionary<Model, PendingBatch> Batches { get; } = new();
		/// <summary>Distance from the anchor to the closest member cell; owned by RefreshCells.</summary>
		public float DistanceSquared { get; set; }
		/// <summary>Transient scratch flag used to build the runtime queue in cell order.</summary>
		public bool QueueMarked { get; set; }
	}

	readonly record struct ClusterCoordinate( int X, int Y )
	{
		/// <summary>
		/// Groups a block of <paramref name="cellsPerCluster"/> by
		/// <paramref name="cellsPerCluster"/> prop cells. Floor division keeps negative
		/// coordinates in the same block as their neighbours, and a size of one maps a cell
		/// to itself, which is what makes the default configuration identical.
		/// </summary>
		public static ClusterCoordinate FromCell( CellCoordinate cell, int cellsPerCluster )
		{
			var size = Math.Max( 1, cellsPerCluster );
			return new ClusterCoordinate( FloorDivide( cell.X, size ), FloorDivide( cell.Y, size ) );
		}

		static int FloorDivide( int value, int divisor ) => value >= 0
			? value / divisor
			: -((-value + divisor - 1) / divisor);
	}

	sealed class PropCell( CellCoordinate coordinate, PropCluster cluster )
	{
		public CellCoordinate Coordinate { get; } = coordinate;
		/// <summary>Batch cluster this cell belongs to; it owns every batch the cell's props join.</summary>
		public PropCluster Cluster { get; } = cluster;
		public List<StreamedProp> Props { get; } = new();
		public List<PropInstance> Instances { get; } = new();
		public int NextProp { get; set; }
		public float DistanceSquared { get; set; }
		public bool Active { get; set; }
		public bool Wanted { get; set; }
		/// <summary>Next instance index a collision pass still owes this cell.</summary>
		public int CollisionCursor { get; set; }
	}
}

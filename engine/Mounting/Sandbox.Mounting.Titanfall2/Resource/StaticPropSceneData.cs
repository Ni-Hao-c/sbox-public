using System.Security.Cryptography;
using Titanfall2;

/// <summary>
/// CPU-only map compilation and serialized-scene restoration. Payloads contain source
/// metadata and compiled spatial/PVS tables, never live models or GPU resources.
/// </summary>
static class Titanfall2StaticPropSceneData
{
	const int DataMagic = 0x32505354; // TSP2
	internal const int DataVersion = 9;
	const int MaximumModelCount = 65536;
	const int MaximumPropCount = 1_000_000;
	const int VisibilityMagic = 0x32565354; // TSV2
	const int VisibilityVersion = 2;
	// Encoded size of one element, used to bound a decoded count by the bytes that are
	// actually present instead of by a guessed maximum.
	const int PlaneBytes = 16;  // normal xyz + distance
	const int NodeBytes = 8;    // plane index + child
	const int PortalBytes = 8;  // from cell + to cell

	internal const int PvsPortalDepth = 16;
	internal readonly record struct StreamedProp(
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
		/// <summary>Dense, stable ID in the compiled scene; independent of BSP SourceIndex.</summary>
		public int InstanceId { get; init; }
		public int Skin { get; init; }
		/// <summary>Original fade distance (sprp value, else MDL default); 0 means it never fades.</summary>
		public float FadeDistance { get; init; }
	}


	internal sealed record Payload( string Props, string Visibility, string LocalLights );

	internal static Payload Compile( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> props,
		Titanfall2BspReader.BspVisibility visibility, IReadOnlyList<Titanfall2WorldLight> lights,
		IReadOnlyList<float> fadeDistances ) => new(
			EncodeWithFade( props, visibility, fadeDistances ), EncodeVisibility( visibility ), Titanfall2LocalLightBuffer.Encode( lights ) );

	internal static string EncodeVisibility( Titanfall2BspReader.BspVisibility visibility )
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
				Titanfall2CompiledVisibility.Compile( visibility, PvsPortalDepth ).Write( writer );
			}
			WriteChecksum( writer, stream );
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

	static void WriteChecksum( BinaryWriter writer, MemoryStream stream )
	{
		writer.Flush();
		writer.Write( SHA256.HashData( stream.GetBuffer().AsSpan( 0, checked((int)stream.Length) ) ) );
	}

	static void ReadChecksum( BinaryReader reader, byte[] payload )
	{
		var position = checked((int)reader.BaseStream.Position);
		var checksum = reader.ReadBytes( 32 );
		if ( checksum.Length != 32 ) throw new EndOfStreamException( "Truncated compiled scene checksum." );
		if ( !CryptographicOperations.FixedTimeEquals( SHA256.HashData( payload.AsSpan( 0, position ) ), checksum ) )
			throw new InvalidDataException( "Compiled scene checksum mismatch." );
	}

	internal static Titanfall2BspReader.BspVisibility ReadVisibility( string encoded, out Titanfall2CompiledVisibility compiled )
	{
		compiled = null;
		var payload = Convert.FromBase64String( encoded );
		using var stream = new MemoryStream( payload, false );
		using var reader = new BinaryReader( stream, System.Text.Encoding.UTF8, false );
		if ( reader.ReadInt32() != VisibilityMagic ) throw new InvalidDataException( "Visibility data has an invalid signature." );
		var version = reader.ReadInt32();
		if ( version < 1 || version > VisibilityVersion ) throw new InvalidDataException( "Visibility data has an unsupported version." );
		if ( !reader.ReadBoolean() )
		{
			if ( version >= 2 ) ReadChecksum( reader, payload );
			if ( stream.Position != stream.Length ) throw new InvalidDataException( "Trailing visibility data." );
			return null;
		}

		var cellCount = reader.ReadInt32();
		if ( cellCount <= 0 || cellCount > 128 ) throw new InvalidDataException( "Invalid visibility cell count." );
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

		var visibility = new Titanfall2BspReader.BspVisibility
		{
			CellCount = cellCount,
			AlwaysVisibleCells = alwaysVisible,
			Planes = planes,
			Nodes = nodes,
			Portals = portals
		};
		if ( !visibility.IsValid ) throw new InvalidDataException( "Invalid visibility tree." );
		compiled = version >= 2 ? Titanfall2CompiledVisibility.Read( reader, visibility, PvsPortalDepth )
			: Titanfall2CompiledVisibility.Compile( visibility, PvsPortalDepth );
		if ( version >= 2 ) ReadChecksum( reader, payload );
		if ( stream.Position != stream.Length ) throw new InvalidDataException( "Trailing visibility data." );
		return visibility;
	}

	/// <param name="source">World props in BSP order.</param>
	/// <param name="visibility">PVS used to build each prop's cell mask.</param>
	/// <param name="fadeDistances">Optional per-prop fade distance, parallel to <paramref name="source"/>; 0 never fades.</param>
	internal static string EncodeWithFade( IReadOnlyList<Titanfall2BspReader.StaticPropInstance> source, Titanfall2BspReader.BspVisibility visibility,
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
			var layout = Titanfall2CompiledPropLayout.Compile( valid.Select( prop => new Titanfall2CompiledPropLayout.Instance(
				new Vector3( prop.Origin.X, prop.Origin.Y, prop.Origin.Z ), indices[NormalizeModelPath( prop.ModelPath )], prop.Skin ) ).ToArray() );
			layout.Write( writer );
			WriteChecksum( writer, stream );
		}
		return Convert.ToBase64String( stream.GetBuffer(), 0, checked((int)stream.Length) );
	}

	internal sealed record CompiledPropScene( StreamedProp[] Props, Titanfall2CompiledPropLayout Layout, bool FromCompiledPayload );

	internal static CompiledPropScene ReadCompiledScene( string encoded )
	{
		var payload = Convert.FromBase64String( encoded );
		using var stream = new MemoryStream( payload, false );
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
		// Bound allocations by actual bytes as well as format limits. V3 is the smallest record.
		if ( !HasRoomFor( stream, propCount, 61 ) ) throw new EndOfStreamException( "Truncated static prop records." );
		var props = new StreamedProp[propCount];
		var layoutInstances = new Titanfall2CompiledPropLayout.Instance[propCount];
		for ( var index = 0; index < propCount; index++ )
		{
			var modelIndex = reader.ReadInt32();
			if ( modelIndex < 0 || modelIndex >= models.Length ) throw new InvalidDataException( $"Invalid model index {modelIndex}." );
			var position = new Vector3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var rotation = new Angles( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var scale = reader.ReadSingle();
			if ( !float.IsFinite( scale ) || !float.IsFinite( rotation.pitch ) || !float.IsFinite( rotation.yaw ) || !float.IsFinite( rotation.roll ) )
				throw new InvalidDataException( "Non-finite static prop transform." );
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
			props[index] = new StreamedProp( models[modelIndex], position, rotation, scale, collidable, probeColor, probe, pvsCells,
				sourceIndex, solidType, reservedCollisionData, collisionFlagsRemove )
			{ InstanceId = index, Skin = skin, FadeDistance = fadeDistance };
			layoutInstances[index] = new( position, modelIndex, skin );
		}
		var layout = version >= 9 ? Titanfall2CompiledPropLayout.Read( reader, layoutInstances )
			: Titanfall2CompiledPropLayout.Compile( layoutInstances );
		if ( version >= 9 ) ReadChecksum( reader, payload );
		if ( stream.Position != stream.Length ) throw new InvalidDataException( "Trailing static scene data." );
		return new( props, layout, version >= 9 );
	}

	static string NormalizeModelPath( string path )
	{
		var normalized = path.Replace( '\\', '/' ).TrimStart( '/' );
		return normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? normalized[..^5] : normalized;
	}

}

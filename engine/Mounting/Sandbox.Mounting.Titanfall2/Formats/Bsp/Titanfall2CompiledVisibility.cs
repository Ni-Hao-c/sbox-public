using static Titanfall2.Titanfall2BspReader;

namespace Titanfall2;

/// <summary>Immutable, depth-limited portal reachability. No portal traversal during gameplay.</summary>
internal sealed class Titanfall2CompiledVisibility
{
	readonly VisibilityCellMask[] _rows;
	internal int PortalDepth { get; }
	internal int CellCount => _rows.Length;
	internal VisibilityCellMask this[int cell] => (uint)cell < _rows.Length ? _rows[cell] : default;

	Titanfall2CompiledVisibility( int portalDepth, VisibilityCellMask[] rows )
	{
		PortalDepth = portalDepth;
		_rows = rows;
	}

	internal static Titanfall2CompiledVisibility Compile( BspVisibility visibility, int portalDepth )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( portalDepth );
		if ( visibility is not { IsValid: true } ) return null;
		var adjacency = new List<int>[visibility.CellCount];
		for ( var cell = 0; cell < adjacency.Length; ++cell ) adjacency[cell] = [];
		foreach ( var portal in visibility.Portals )
		{
			if ( (uint)portal.FromCell >= adjacency.Length || (uint)portal.ToCell >= adjacency.Length ) continue;
			adjacency[portal.FromCell].Add( portal.ToCell );
		}
		var rows = new VisibilityCellMask[visibility.CellCount];
		var queue = new int[visibility.CellCount];
		var depths = new int[visibility.CellCount];
		for ( var start = 0; start < rows.Length; ++start )
		{
			var mask = new VisibilityCellMask().Add( start );
			queue[0] = start;
			depths[0] = 0;
			var head = 0;
			var tail = 1;
			while ( head < tail )
			{
				var cell = queue[head];
				var depth = depths[head++];
				if ( depth >= portalDepth ) continue;
				foreach ( var next in adjacency[cell] )
				{
					if ( mask.Contains( next ) ) continue;
					mask = mask.Add( next );
					queue[tail] = next;
					depths[tail++] = depth + 1;
				}
			}
			rows[start] = mask.Union( visibility.AlwaysVisibleCells );
		}
		return new( portalDepth, rows );
	}

	internal void Write( BinaryWriter writer )
	{
		writer.Write( PortalDepth );
		writer.Write( CellCount );
		foreach ( var row in _rows ) { writer.Write( row.Low ); writer.Write( row.High ); }
	}

	internal static Titanfall2CompiledVisibility Read( BinaryReader reader, BspVisibility visibility, int portalDepth )
	{
		if ( reader.ReadInt32() != portalDepth || reader.ReadInt32() != visibility.CellCount )
			throw new InvalidDataException( "Compiled visibility policy or cell count mismatch." );
		var rows = new VisibilityCellMask[visibility.CellCount];
		for ( var cell = 0; cell < rows.Length; ++cell )
		{
			rows[cell] = new( reader.ReadUInt64(), reader.ReadUInt64() );
			if ( !rows[cell].Contains( cell ) || rows[cell].Union( visibility.AlwaysVisibleCells ) != rows[cell] )
				throw new InvalidDataException( "Invalid compiled visibility row." );
		}
		return new( portalDepth, rows );
	}
}

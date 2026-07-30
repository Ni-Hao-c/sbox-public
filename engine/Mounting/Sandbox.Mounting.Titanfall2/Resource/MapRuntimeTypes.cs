using Titanfall2;

readonly record struct CollisionCell( int X, int Y );

sealed class CollisionChunk
{
	readonly Dictionary<int, int> _sourceToLocal = new();
	public List<Vector3> Vertices { get; } = new();
	public List<int> Indices { get; } = new();

	public int RemapVertex( int sourceIndex, IReadOnlyList<System.Numerics.Vector3> sourceVertices )
	{
		if ( _sourceToLocal.TryGetValue( sourceIndex, out var localIndex ) ) return localIndex;
		localIndex = Vertices.Count;
		_sourceToLocal.Add( sourceIndex, localIndex );
		var source = sourceVertices[sourceIndex];
		Vertices.Add( new Vector3( source.X, source.Y, source.Z ) );
		return localIndex;
	}
}

sealed record SkyboxCandidate(
	string Name,
	Vector3 Origin,
	Angles Rotation,
	float Scale,
	Titanfall2SkyboxFog Fog,
	Titanfall2BspReader.StaticPropInstance[] Models );

readonly record struct VistaOriginKey( int X, int Y, int Z )
{
	const float Quantization = 8f;

	public static VistaOriginKey FromPosition( System.Numerics.Vector3 position ) => new(
		(int)MathF.Round( position.X / Quantization ),
		(int)MathF.Round( position.Y / Quantization ),
		(int)MathF.Round( position.Z / Quantization ) );
}

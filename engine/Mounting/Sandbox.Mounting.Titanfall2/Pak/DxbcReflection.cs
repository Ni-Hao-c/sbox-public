using System.Buffers.Binary;
using System.Text;

readonly record struct DxbcVariable( int StartOffset, int Size );

readonly record struct DxbcResourceBinding(
	string Name,
	int InputType,
	int ReturnType,
	int Dimension,
	int SampleCount,
	int BindPoint,
	int BindCount,
	uint Flags )
{
	// D3D_SIT_TEXTURE. Other RDEF inputs (samplers, constant buffers and UAVs)
	// do not correspond to entries in Titanfall's MATL texture handle table.
	public bool IsTexture => InputType == 2;
}

/// <summary>A bounds-checked DXBC RDEF constant-buffer layout.</summary>
sealed class DxbcConstantBufferLayout
{
	readonly Dictionary<string, DxbcVariable> _variables;

	public int Size { get; }
	public int VariableCount => _variables.Count;
	public IReadOnlyDictionary<string, DxbcVariable> Variables => _variables;

	internal DxbcConstantBufferLayout( int size, Dictionary<string, DxbcVariable> variables )
	{
		Size = size;
		_variables = variables;
	}

	public bool TryGetVariable( string name, out DxbcVariable variable ) => _variables.TryGetValue( name, out variable );

	public static bool TryParse( byte[] dxbc, string constantBufferName, out DxbcConstantBufferLayout layout, out string error )
	{
		layout = null;
		if ( !DxbcShaderReflection.TryParse( dxbc, out var reflection, out error ) ) return false;
		if ( reflection.TryGetConstantBuffer( constantBufferName, out layout ) ) return true;
		error = $"DXBC constant buffer '{constantBufferName}' was not found.";
		return false;
	}
}

/// <summary>
/// Minimal DXBC RDEF reflection used by RPAK materials. Resource bindings are
/// global to the shader and constant buffers are exposed independently, so a
/// material can still bind textures when its shader has no CBufUberStatic.
/// </summary>
sealed class DxbcShaderReflection
{
	readonly Dictionary<string, DxbcConstantBufferLayout> _constantBuffers;
	readonly Dictionary<int, DxbcResourceBinding> _texturesByBindPoint;

	public IReadOnlyList<DxbcResourceBinding> ResourceBindings { get; }
	public int ConstantBufferCount => _constantBuffers.Count;

	DxbcShaderReflection( List<DxbcResourceBinding> resources,
		Dictionary<string, DxbcConstantBufferLayout> constantBuffers )
	{
		ResourceBindings = resources;
		_constantBuffers = constantBuffers;
		_texturesByBindPoint = new Dictionary<int, DxbcResourceBinding>();
		foreach ( var resource in resources )
		{
			if ( !resource.IsTexture || resource.BindPoint < 0 ) continue;
			for ( var offset = 0; offset < Math.Max( 1, resource.BindCount ); offset++ )
				_texturesByBindPoint.TryAdd( resource.BindPoint + offset, resource with { BindPoint = resource.BindPoint + offset } );
		}
	}

	public bool TryGetConstantBuffer( string name, out DxbcConstantBufferLayout layout ) =>
		_constantBuffers.TryGetValue( name, out layout );

	public bool TryGetTexture( int bindPoint, out DxbcResourceBinding resource ) =>
		_texturesByBindPoint.TryGetValue( bindPoint, out resource );

	public static bool TryParse( byte[] dxbc, out DxbcShaderReflection reflection, out string error )
	{
		const uint DxbcMagic = 0x43425844; // DXBC
		const uint RdefMagic = 0x46454452; // RDEF
		const int DxbcHeaderSize = 32;
		const int RdefHeaderSize = 32;
		const int ConstantBufferEntrySize = 24;
		const int VariableEntrySize = 40;
		const int ResourceEntrySize = 32;
		reflection = null;
		error = null;

		if ( dxbc is null || dxbc.Length < DxbcHeaderSize || U32( dxbc, 0 ) != DxbcMagic )
		{
			error = "Shader CPU data is not a DXBC container.";
			return false;
		}

		var containerSize = I32( dxbc, 24 );
		var blobCount = I32( dxbc, 28 );
		if ( containerSize < DxbcHeaderSize || containerSize > dxbc.Length || blobCount < 0 || blobCount > 1024
			|| !HasRange( containerSize, DxbcHeaderSize, (long)blobCount * 4 ) )
		{
			error = "DXBC header contains an invalid container size or blob count.";
			return false;
		}

		for ( var blobIndex = 0; blobIndex < blobCount; blobIndex++ )
		{
			var blobOffset = I32( dxbc, DxbcHeaderSize + blobIndex * 4 );
			if ( !HasRange( containerSize, blobOffset, 8 ) ) continue;
			var blobSize = I32( dxbc, blobOffset + 4 );
			if ( blobSize < 0 || !HasRange( containerSize, blobOffset + 8L, blobSize ) ) continue;
			if ( U32( dxbc, blobOffset ) != RdefMagic || blobSize < RdefHeaderSize ) continue;

			var rdefOffset = blobOffset + 8;
			var constantBufferCount = I32( dxbc, rdefOffset );
			var constantBufferOffset = I32( dxbc, rdefOffset + 4 );
			var resourceCount = I32( dxbc, rdefOffset + 8 );
			var resourceOffset = I32( dxbc, rdefOffset + 12 );
			if ( constantBufferCount < 0 || constantBufferCount > 4096
				|| !HasRange( blobSize, constantBufferOffset, (long)constantBufferCount * ConstantBufferEntrySize )
				|| resourceCount < 0 || resourceCount > 16384
				|| !HasRange( blobSize, resourceOffset, (long)resourceCount * ResourceEntrySize ) )
			{
				error = "DXBC RDEF contains an invalid constant-buffer or resource-binding table.";
				return false;
			}

			var resources = new List<DxbcResourceBinding>( resourceCount );
			for ( var resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++ )
			{
				var entry = rdefOffset + resourceOffset + resourceIndex * ResourceEntrySize;
				if ( !TryReadString( dxbc, rdefOffset, blobSize, I32( dxbc, entry ), out var name ) ) continue;
				var bindPoint = I32( dxbc, entry + 20 );
				var bindCount = I32( dxbc, entry + 24 );
				if ( bindPoint < 0 || bindPoint > 65535 || bindCount < 0 || bindCount > 65536 - bindPoint ) continue;
				resources.Add( new DxbcResourceBinding(
					name,
					I32( dxbc, entry + 4 ),
					I32( dxbc, entry + 8 ),
					I32( dxbc, entry + 12 ),
					I32( dxbc, entry + 16 ),
					bindPoint,
					bindCount,
					U32( dxbc, entry + 28 ) ) );
			}

			var constantBuffers = new Dictionary<string, DxbcConstantBufferLayout>(
				constantBufferCount, StringComparer.OrdinalIgnoreCase );
			for ( var bufferIndex = 0; bufferIndex < constantBufferCount; bufferIndex++ )
			{
				var entryOffset = rdefOffset + constantBufferOffset + bufferIndex * ConstantBufferEntrySize;
				if ( !TryReadString( dxbc, rdefOffset, blobSize, I32( dxbc, entryOffset ), out var name ) ) continue;

				var variableCount = I32( dxbc, entryOffset + 4 );
				var variableOffset = I32( dxbc, entryOffset + 8 );
				var bufferSize = I32( dxbc, entryOffset + 12 );
				if ( variableCount < 0 || variableCount > 16384 || bufferSize < 0 || bufferSize > 16 * 1024 * 1024
					|| !HasRange( blobSize, variableOffset, (long)variableCount * VariableEntrySize ) ) continue;

				var variables = new Dictionary<string, DxbcVariable>( variableCount, StringComparer.OrdinalIgnoreCase );
				for ( var variableIndex = 0; variableIndex < variableCount; variableIndex++ )
				{
					var variableEntry = rdefOffset + variableOffset + variableIndex * VariableEntrySize;
					var startOffset = I32( dxbc, variableEntry + 4 );
					var size = I32( dxbc, variableEntry + 8 );
					if ( startOffset < 0 || size < 0 || startOffset > bufferSize || size > bufferSize - startOffset
						|| !TryReadString( dxbc, rdefOffset, blobSize, I32( dxbc, variableEntry ), out var variableName ) ) continue;
					variables.TryAdd( variableName, new DxbcVariable( startOffset, size ) );
				}
				constantBuffers.TryAdd( name, new DxbcConstantBufferLayout( bufferSize, variables ) );
			}

			resources.Sort( static ( left, right ) => left.BindPoint.CompareTo( right.BindPoint ) );
			reflection = new DxbcShaderReflection( resources, constantBuffers );
			return true;
		}

		error = "DXBC RDEF blob was not found.";
		return false;
	}

	static bool TryReadString( byte[] bytes, int rdefOffset, int rdefSize, int relativeOffset, out string value )
	{
		value = null;
		if ( relativeOffset < 0 || relativeOffset >= rdefSize ) return false;
		var start = rdefOffset + relativeOffset;
		var endLimit = rdefOffset + rdefSize;
		var end = start;
		while ( end < endLimit && bytes[end] != 0 && end - start < 4096 ) end++;
		if ( end >= endLimit || bytes[end] != 0 ) return false;
		value = Encoding.UTF8.GetString( bytes, start, end - start );
		return true;
	}

	static bool HasRange( long total, long offset, long length ) => offset >= 0 && length >= 0 && offset <= total && length <= total - offset;
	static int I32( byte[] bytes, int offset ) => BinaryPrimitives.ReadInt32LittleEndian( bytes.AsSpan( offset, 4 ) );
	static uint U32( byte[] bytes, int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( offset, 4 ) );
}

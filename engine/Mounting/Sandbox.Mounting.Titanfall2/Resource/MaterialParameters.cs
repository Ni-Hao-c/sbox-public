using System.Buffers.Binary;

readonly record struct Titanfall2UvTransform(
	float RowXx,
	float RowXy,
	float RowYx,
	float RowYy,
	float TranslateX,
	float TranslateY,
	bool Animated )
{
	public static Titanfall2UvTransform Identity( bool animated ) => new( 1f, 0f, 0f, 1f, 0f, 0f, animated );
}

readonly record struct Titanfall2MaterialParameters(
	Titanfall2UvTransform Uv1,
	Titanfall2UvTransform Uv2,
	Titanfall2UvTransform Uv3,
	float AlbedoTintR,
	float AlbedoTintG,
	float AlbedoTintB,
	float Opacity,
	float AlphaTestReference,
	float EmissiveTintR,
	float EmissiveTintG,
	float EmissiveTintB,
	float DistortionX,
	float DistortionY,
	float Distortion2X,
	float Distortion2Y,
	float GlossScale,
	float SpecularTintR,
	float SpecularTintG,
	float SpecularTintB,
	float MetalnessScale,
	float EmissiveStrength,
	float FresnelStrength,
	float DetailBlend,
	float NormalScale,
	float EnvironmentIntensity )
{
	public static Titanfall2MaterialParameters Read( DxbcConstantBufferLayout layout, byte[] data, string shaderSetName )
	{
		var uv1 = ReadUv( layout, data, 1, HasFeature( shaderSetName, "Uv1at" ) );
		var uv2 = ReadUv( layout, data, 2, HasFeature( shaderSetName, "Uv2at" ) );
		var uv3 = ReadUv( layout, data, 3, HasFeature( shaderSetName, "Uv3at" ) );
		Read3( layout, data, "c_albedoTint", 1f, 1f, 1f, out var albedoR, out var albedoG, out var albedoB );
		Read3( layout, data, "c_emissiveTint", 1f, 1f, 1f, out var emissiveR, out var emissiveG, out var emissiveB );
		Read2( layout, data, "c_uvDistortionIntensity", 0f, 0f, out var distortionX, out var distortionY );
		Read2( layout, data, "c_uvDistortion2Intensity", 0f, 0f, out var distortion2X, out var distortion2Y );
		var glossScale = Read1( layout, data, "c_perfGloss", 1f );
		Read3( layout, data, "c_perfSpecColor", 1f, 1f, 1f, out var specularR, out var specularG, out var specularB );
		var detailBlend = Read1( layout, data, "c_layerBlendRamp", 0f );
		var opacity = Read1( layout, data, "c_opacity", 1f );
		var alphaTestReference = Read1( layout, data, "c_alphaTestReference", 0.5f );

		return new Titanfall2MaterialParameters(
			uv1, uv2, uv3,
			albedoR, albedoG, albedoB,
			Math.Clamp( opacity, 0f, 1f ),
			Math.Clamp( alphaTestReference, 0f, 1f ),
			emissiveR, emissiveG, emissiveB,
			distortionX, distortionY, distortion2X, distortion2Y,
			Math.Clamp( glossScale, 0f, 16f ),
			specularR, specularG, specularB,
			1f, 1f, 0f, Math.Clamp( detailBlend, 0f, 1f ), 1f, 1f );
	}

	static Titanfall2UvTransform ReadUv( DxbcConstantBufferLayout layout, byte[] data, int index, bool animated )
	{
		Read2( layout, data, $"c_uv{index}RotScaleX", 1f, 0f, out var rowXx, out var rowXy );
		Read2( layout, data, $"c_uv{index}RotScaleY", 0f, 1f, out var rowYx, out var rowYy );
		Read2( layout, data, $"c_uv{index}Translate", 0f, 0f, out var translateX, out var translateY );
		return new Titanfall2UvTransform( rowXx, rowXy, rowYx, rowYy, translateX, translateY, animated );
	}

	static float Read1( DxbcConstantBufferLayout layout, byte[] data, string name, float fallback )
	{
		if ( !TryGetSpan( layout, data, name, 4, out var span ) ) return fallback;
		var value = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span ) );
		return IsUsable( value ) ? value : fallback;
	}

	static void Read2( DxbcConstantBufferLayout layout, byte[] data, string name, float fallbackX, float fallbackY, out float x, out float y )
	{
		x = fallbackX;
		y = fallbackY;
		if ( !TryGetSpan( layout, data, name, 8, out var span ) ) return;
		var candidateX = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span ) );
		var candidateY = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span[4..] ) );
		if ( IsUsable( candidateX ) ) x = candidateX;
		if ( IsUsable( candidateY ) ) y = candidateY;
	}

	static void Read3( DxbcConstantBufferLayout layout, byte[] data, string name, float fallbackX, float fallbackY, float fallbackZ,
		out float x, out float y, out float z )
	{
		x = fallbackX;
		y = fallbackY;
		z = fallbackZ;
		if ( !TryGetSpan( layout, data, name, 12, out var span ) ) return;
		var candidateX = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span ) );
		var candidateY = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span[4..] ) );
		var candidateZ = BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( span[8..] ) );
		if ( IsUsable( candidateX ) ) x = candidateX;
		if ( IsUsable( candidateY ) ) y = candidateY;
		if ( IsUsable( candidateZ ) ) z = candidateZ;
	}

	static bool TryGetSpan( DxbcConstantBufferLayout layout, byte[] data, string name, int minimumSize, out ReadOnlySpan<byte> span )
	{
		span = default;
		if ( layout is null || data is null || !layout.TryGetVariable( name, out var variable ) || variable.Size < minimumSize
			|| variable.StartOffset < 0 || variable.StartOffset > data.Length || minimumSize > data.Length - variable.StartOffset ) return false;
		span = data.AsSpan( variable.StartOffset, minimumSize );
		return true;
	}

	static bool HasFeature( string shaderSetName, string feature ) => !string.IsNullOrWhiteSpace( shaderSetName )
		&& shaderSetName.Contains( feature, StringComparison.OrdinalIgnoreCase );
	static bool IsUsable( float value ) => float.IsFinite( value ) && MathF.Abs( value ) <= 1_000_000f;
}

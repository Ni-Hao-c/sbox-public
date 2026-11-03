using Sandbox.Mounting.PerfKit;
using Titanfall2;

partial class MapLoader
{
	// Edge length of the spatial cells world triangles are grouped into. Larger cells submit fewer
	// fragments (fewer draws) at the cost of a coarser frustum cull. The value comes from
	// SBOX_TITANFALL2_WORLD_CELL_SIZE; builds cache it in a local and pass it down, because the
	// triangle loops must not read a static property per triangle.
	static float WorldRenderCellSize => Titanfall2StreamingSettings.WorldRenderCellSize;

	// G6a: upload the two largest lightmap channels as 16-bit formats. The shader keeps sampling
	// float4 values, so only precision changes (on retail pages RealTimeB is 93.7-96.7% binary and
	// loses ~0.2% on average, SkyB loses ~1% of its sky visibility), while the lightmap block drops
	// from 17 B/px to 13 B/px. SBOX_TITANFALL2_LIGHTMAP_16BIT=0 restores the RGBA8888 upload for A/B
	// and rollback. See PackRgb565/PackBgra4444 for the byte-order derivation.
	static bool Lightmap16Bit => Titanfall2StreamingSettings.Lightmap16Bit;

	/// <summary>
	/// Packs an RGBA8888 lightmap page into RGB565. The byte order follows the engine's own DDS
	/// path: <c>TextureLoader.FromDds</c> hands the file's mip bytes to <c>WithData</c> unchanged and
	/// maps the DDS masks R=0xF800 / G=0x07E0 / B=0x001F to <see cref="ImageFormat.RGB565"/>
	/// (Sandbox.Engine/Game/Mount/TextureLoader.DDS.cs:150), so a little-endian 16-bit word is
	/// <c>R &lt;&lt; 11 | G &lt;&lt; 5 | B</c>: red in the high five bits, blue in the low five.
	/// Note this is the DDS "RGB565" order and is the opposite of VTF's RGB565 (which keeps red in
	/// the low bits), so do not reuse the VTF table for this format.
	/// </summary>
	static byte[] PackRgb565( byte[] rgba, int pixelCount )
	{
		if ( rgba is null || rgba.Length != pixelCount * 4 ) return null;
		var packed = new byte[pixelCount * 2];
		for ( var pixel = 0; pixel < pixelCount; pixel++ )
		{
			var source = pixel * 4;
			// Round to nearest level: (v * levels + 127) / 255.
			var red = (rgba[source] * 31 + 127) / 255;
			var green = (rgba[source + 1] * 63 + 127) / 255;
			var blue = (rgba[source + 2] * 31 + 127) / 255;
			var value = (ushort)((red << 11) | (green << 5) | blue);
			packed[pixel * 2] = (byte)value;
			packed[pixel * 2 + 1] = (byte)(value >> 8);
		}
		return packed;
	}

	/// <summary>
	/// Packs an RGBA8888 lightmap page into BGRA4444 (four 4-bit channels, hardware filtered). Channel
	/// order matches the engine's other BGRA formats: the DDS table maps B=0x000000FF (lowest byte) to
	/// <see cref="ImageFormat.BGRA8888"/> (TextureLoader.DDS.cs:136), so here blue is the lowest nibble
	/// and alpha the highest: <c>A &lt;&lt; 12 | R &lt;&lt; 8 | G &lt;&lt; 4 | B</c>. The VTF decoder
	/// already feeds this format the same layout (VtfTextureDecoder.cs maps VTF format 19 straight
	/// through, and the VTF BGRA4444 decode uses B=bits 0-3 / G=4-7 / R=8-11 / A=12-15).
	/// </summary>
	static byte[] PackBgra4444( byte[] rgba, int pixelCount )
	{
		if ( rgba is null || rgba.Length != pixelCount * 4 ) return null;
		var packed = new byte[pixelCount * 2];
		for ( var pixel = 0; pixel < pixelCount; pixel++ )
		{
			var source = pixel * 4;
			var blue = (rgba[source + 2] * 15 + 127) / 255;
			var green = (rgba[source + 1] * 15 + 127) / 255;
			var red = (rgba[source] * 15 + 127) / 255;
			var alpha = (rgba[source + 3] * 15 + 127) / 255;
			var value = (ushort)((alpha << 12) | (red << 8) | (green << 4) | blue);
			packed[pixel * 2] = (byte)value;
			packed[pixel * 2 + 1] = (byte)(value >> 8);
		}
		return packed;
	}

	static int LightmapBytesPerPixel( ImageFormat format ) => format switch
	{
		ImageFormat.RGBA8888 or ImageFormat.BGRA8888 or ImageFormat.ARGB8888 or ImageFormat.ABGR8888 => 4,
		ImageFormat.RGB565 or ImageFormat.BGR565 or ImageFormat.BGRA4444 or ImageFormat.BGRA5551
			or ImageFormat.BGRX5551 => 2,
		ImageFormat.I8 or ImageFormat.A8 => 1,
		_ => 4
	};

	// ---- G6b: lightmap coverage, dilation and a short mip chain -----------------------------
	//
	// The atlas is tightly packed and the gaps between charts hold unrelated bytes, so a plain mip
	// chain smears that data into the lit texels. Before uploading we therefore dilate each chart
	// outward into the uncovered texels and only then build the chain from the dilated bytes.
	// Measured on retail pages (see .tmp/ttf2-review-verify/g6-lightmap.md): two dilation steps
	// already saturate the narrow gaps, four leave headroom; L0..L2 keeps the chain short because
	// deeper levels blur the thin charts more than they save bandwidth.
	const int LightmapDilationSteps = 4;
	const int LightmapMipLevelCount = 3;

	static bool LightmapMips => Titanfall2StreamingSettings.LightmapMips;

	static int LightmapChannelBytes( Texture texture )
	{
		if ( texture is null || !texture.IsValid() ) return 0;
		return LightmapChainBytes( texture.Width, texture.Height,
			LightmapBytesPerPixel( texture.ImageFormat ), Math.Max( 1, texture.Mips ) );
	}

	/// <summary>Bytes of a complete mip chain, level 0 first, matching the engine's level sizing.</summary>
	static int LightmapChainBytes( int width, int height, int bytesPerPixel, int mipLevels )
	{
		long total = 0;
		for ( var level = 0; level < mipLevels; level++ )
			total += (long)Math.Max( 1, width >> level ) * Math.Max( 1, height >> level ) * bytesPerPixel;
		return checked((int)total);
	}

	/// <summary>L0..L2 for the lightmap pages (the chain is only ever this short by design).</summary>
	static int LightmapLevels( int width, int height )
	{
		var full = System.Numerics.BitOperations.Log2( (uint)Math.Max( Math.Max( width, height ), 1 ) ) + 1;
		return Math.Min( LightmapMipLevelCount, full );
	}

	static int LightmapPageBytes( RuntimeLightmapPage page ) =>
		LightmapChannelBytes( page.SkyA ) + LightmapChannelBytes( page.SkyB )
		+ LightmapChannelBytes( page.RealTimeA ) + LightmapChannelBytes( page.RealTimeB )
		+ LightmapChannelBytes( page.SunChannels );

	static string LightmapChannelSummary( Texture texture ) => texture is null || !texture.IsValid()
		? "none"
		: $"{texture.ImageFormat} {texture.Width}x{texture.Height} ({LightmapBytesPerPixel( texture.ImageFormat )} B/px"
			+ $"{(texture.Mips > 1 ? $", {texture.Mips} mips" : string.Empty)})";

	static int _lightmapFormatFallbacks;

	static void WarnLightmapFormatFallback( ImageFormat format )
	{
		if ( System.Threading.Interlocked.Increment( ref _lightmapFormatFallbacks ) > 2 ) return;
		Titanfall2Log.Warning( $"Titanfall 2 could not create a {format} lightmap channel; "
			+ "falling back to RGBA8888 for that channel." );
	}

	/// <summary>
	/// Rasterises the lightmap UVs of the given geometry into a per-page "texel is covered" mask: a
	/// texel counts as covered when its centre falls inside a triangle whose vertices carry a
	/// lightmap UV. UVs outside the page and degenerate triangles are skipped (counted in
	/// <paramref name="skippedTriangles"/>). Pages with no geometry stay null. The mask is a plain
	/// <c>bool[]</c>, one byte per texel - about 1 MB per 1024² page.
	/// </summary>
	internal static bool[][] BuildLightmapCoverage( IEnumerable<Titanfall2BspReader.WorldMesh> meshes,
		IReadOnlyList<Titanfall2BspReader.BspLightmapPage> pages, out int skippedTriangles )
	{
		skippedTriangles = 0;
		if ( meshes is null || pages is null || pages.Count == 0 ) return null;
		var masks = new bool[pages.Count][];
		foreach ( var mesh in meshes )
		{
			if ( mesh is null || mesh.Vertices is null || mesh.Indices is null ) continue;
			var pageIndex = mesh.LightmapPage;
			if ( pageIndex < 0 || pageIndex >= pages.Count ) continue;
			var page = pages[pageIndex];
			if ( page is null || page.Width <= 0 || page.Height <= 0 ) continue;
			var mask = masks[pageIndex] ??= new bool[page.Width * page.Height];
			RasterizeLightmapMesh( mesh, mask, page.Width, page.Height, ref skippedTriangles );
		}
		return masks;
	}

	static void RasterizeLightmapMesh( Titanfall2BspReader.WorldMesh mesh, bool[] mask, int width, int height,
		ref int skippedTriangles )
	{
		var vertices = mesh.Vertices;
		var indices = mesh.Indices;
		for ( var triangle = 0; triangle + 2 < indices.Length; triangle += 3 )
		{
			var indexA = indices[triangle];
			var indexB = indices[triangle + 1];
			var indexC = indices[triangle + 2];
			if ( (uint)indexA >= (uint)vertices.Length || (uint)indexB >= (uint)vertices.Length
				|| (uint)indexC >= (uint)vertices.Length )
			{
				skippedTriangles++;
				continue;
			}

			var uvA = vertices[indexA].LightmapTexCoord;
			var uvB = vertices[indexB].LightmapTexCoord;
			var uvC = vertices[indexC].LightmapTexCoord;
			if ( !float.IsFinite( uvA.X ) || !float.IsFinite( uvA.Y ) || !float.IsFinite( uvB.X )
				|| !float.IsFinite( uvB.Y ) || !float.IsFinite( uvC.X ) || !float.IsFinite( uvC.Y ) )
			{
				skippedTriangles++;
				continue;
			}

			var minU = MathF.Min( uvA.X, MathF.Min( uvB.X, uvC.X ) );
			var maxU = MathF.Max( uvA.X, MathF.Max( uvB.X, uvC.X ) );
			var minV = MathF.Min( uvA.Y, MathF.Min( uvB.Y, uvC.Y ) );
			var maxV = MathF.Max( uvA.Y, MathF.Max( uvB.Y, uvC.Y ) );
			if ( minU < -0.01f || minV < -0.01f || maxU > 1.01f || maxV > 1.01f )
			{
				skippedTriangles++;
				continue;
			}

			var ax = uvA.X * width;
			var ay = uvA.Y * height;
			var bx = uvB.X * width;
			var by = uvB.Y * height;
			var cx = uvC.X * width;
			var cy = uvC.Y * height;
			var x0 = Math.Clamp( (int)MathF.Floor( MathF.Min( ax, MathF.Min( bx, cx ) ) - 0.5f ), 0, width - 1 );
			var x1 = Math.Clamp( (int)MathF.Ceiling( MathF.Max( ax, MathF.Max( bx, cx ) ) + 0.5f ), 0, width - 1 );
			var y0 = Math.Clamp( (int)MathF.Floor( MathF.Min( ay, MathF.Min( by, cy ) ) - 0.5f ), 0, height - 1 );
			var y1 = Math.Clamp( (int)MathF.Ceiling( MathF.Max( ay, MathF.Max( by, cy ) ) + 0.5f ), 0, height - 1 );
			var denominator = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
			if ( MathF.Abs( denominator ) < 1e-6f )
			{
				skippedTriangles++;
				continue;
			}

			for ( var y = y0; y <= y1; y++ )
			{
				var py = y + 0.5f;
				var row = y * width;
				for ( var x = x0; x <= x1; x++ )
				{
					var px = x + 0.5f;
					var w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / denominator;
					if ( w0 < 0f ) continue;
					var w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / denominator;
					if ( w1 < 0f ) continue;
					if ( 1f - w0 - w1 < 0f ) continue;
					mask[row + x] = true;
				}
			}
		}
	}

	/// <summary>
	/// Fills the uncovered texels of one lightmap channel from the nearest covered colour, one ring
	/// per step, so the mip chain averages chart colour instead of unrelated atlas bytes. Covered
	/// texels are never written: the result differs from <paramref name="rgba"/> only where
	/// <paramref name="covered"/> is false. Returns the input array itself when there is nothing to
	/// fill.
	/// </summary>
	internal static byte[] DilateLightmap( byte[] rgba, int width, int height, bool[] covered, int steps,
		out int filledTexels )
	{
		filledTexels = 0;
		var texelCount = width * height;
		if ( rgba is null || covered is null || steps <= 0 || texelCount <= 0
			|| rgba.Length != texelCount * 4 || covered.Length != texelCount ) return rgba;

		var known = (bool[])covered.Clone();
		var frontier = new List<int>( texelCount / 8 );
		for ( var index = 0; index < texelCount; index++ )
			if ( known[index] ) frontier.Add( index );
		if ( frontier.Count == 0 ) return rgba;

		var output = (byte[])rgba.Clone();
		var next = new List<int>();
		for ( var step = 0; step < steps && frontier.Count > 0; step++ )
		{
			next.Clear();
			for ( var frontierIndex = 0; frontierIndex < frontier.Count; frontierIndex++ )
			{
				var index = frontier[frontierIndex];
				var x = index % width;
				var y = index / width;
				for ( var dy = -1; dy <= 1; dy++ )
				{
					var ny = y + dy;
					if ( ny < 0 || ny >= height ) continue;
					for ( var dx = -1; dx <= 1; dx++ )
					{
						if ( dx == 0 && dy == 0 ) continue;
						var nx = x + dx;
						if ( nx < 0 || nx >= width ) continue;
						var neighbour = ny * width + nx;
						if ( known[neighbour] ) continue;

						// Average the known 8-neighbours: smoother than nearest on oblique surfaces.
						int red = 0, green = 0, blue = 0, alpha = 0, count = 0;
						for ( var sy = -1; sy <= 1; sy++ )
						{
							var sampleY = ny + sy;
							if ( sampleY < 0 || sampleY >= height ) continue;
							for ( var sx = -1; sx <= 1; sx++ )
							{
								var sampleX = nx + sx;
								if ( sampleX < 0 || sampleX >= width ) continue;
								var sample = sampleY * width + sampleX;
								if ( !known[sample] ) continue;
								var offset = sample * 4;
								red += output[offset];
								green += output[offset + 1];
								blue += output[offset + 2];
								alpha += output[offset + 3];
								count++;
							}
						}
						if ( count == 0 ) continue;

						var target = neighbour * 4;
						output[target] = (byte)(red / count);
						output[target + 1] = (byte)(green / count);
						output[target + 2] = (byte)(blue / count);
						output[target + 3] = (byte)(alpha / count);
						known[neighbour] = true;
						next.Add( neighbour );
						filledTexels++;
					}
				}
			}
			(frontier, next) = (next, frontier);
		}
		return output;
	}

	/// <summary>Box-filters an RGBA8888 page down to <paramref name="levels"/> levels (level 0 = input).</summary>
	internal static List<byte[]> BuildLightmapMipChain( byte[] rgba, int width, int height, int levels )
	{
		var chain = new List<byte[]>( Math.Max( 1, levels ) ) { rgba };
		if ( levels <= 1 || rgba is null ) return chain;

		var source = rgba;
		var sourceWidth = width;
		var sourceHeight = height;
		for ( var level = 1; level < levels; level++ )
		{
			var nextWidth = Math.Max( 1, width >> level );
			var nextHeight = Math.Max( 1, height >> level );
			var next = new byte[nextWidth * nextHeight * 4];
			for ( var y = 0; y < nextHeight; y++ )
			{
				var sourceY0 = Math.Min( sourceHeight - 1, y * 2 );
				var sourceY1 = Math.Min( sourceHeight - 1, y * 2 + 1 );
				for ( var x = 0; x < nextWidth; x++ )
				{
					var sourceX0 = Math.Min( sourceWidth - 1, x * 2 );
					var sourceX1 = Math.Min( sourceWidth - 1, x * 2 + 1 );
					var a = (sourceY0 * sourceWidth + sourceX0) * 4;
					var b = (sourceY0 * sourceWidth + sourceX1) * 4;
					var c = (sourceY1 * sourceWidth + sourceX0) * 4;
					var d = (sourceY1 * sourceWidth + sourceX1) * 4;
					var target = (y * nextWidth + x) * 4;
					for ( var channel = 0; channel < 4; channel++ )
						next[target + channel] = (byte)((source[a + channel] + source[b + channel]
							+ source[c + channel] + source[d + channel]) / 4);
				}
			}
			chain.Add( next );
			source = next;
			sourceWidth = nextWidth;
			sourceHeight = nextHeight;
		}
		return chain;
	}

	/// <summary>
	/// Uploads one lightmap channel. With <paramref name="levels"/> above one the complete chain is
	/// built on the CPU and passed with the base level, so the result does not depend on the
	/// backend's mip generation (the engine's TSPEC_TEXTURE_GEN_MIP_MAPS flag is documented as
	/// render-target only). <paramref name="packer"/> converts each level's RGBA bytes to the
	/// upload format, because packing after filtering is the only correct order for 565/4444.
	/// </summary>
	static Texture CreateLightmapTexture( byte[] rgba, int width, int height, ImageFormat format, int levels,
		Func<byte[], int, byte[]> packer )
	{
		if ( rgba is null ) return null;
		levels = Math.Max( 1, levels );
		if ( levels <= 1 )
		{
			var single = packer is null ? rgba : packer( rgba, width * height );
			return CreateLightmapTextureRaw( single, width, height, format, 1 );
		}

		var chain = BuildLightmapMipChain( rgba, width, height, levels );
		var bytesPerPixel = LightmapBytesPerPixel( format );
		var total = LightmapChainBytes( width, height, bytesPerPixel, levels );
		var full = new byte[total];
		var offset = 0;
		for ( var level = 0; level < chain.Count; level++ )
		{
			var levelWidth = Math.Max( 1, width >> level );
			var levelHeight = Math.Max( 1, height >> level );
			var levelPixels = levelWidth * levelHeight;
			var levelData = packer is null ? chain[level] : packer( chain[level], levelPixels );
			if ( levelData is null || levelData.Length != levelPixels * bytesPerPixel ) return null;
			Buffer.BlockCopy( levelData, 0, full, offset, levelData.Length );
			offset += levelData.Length;
		}
		return CreateLightmapTextureRaw( full, width, height, format, levels );
	}

	static Texture CreateLightmapTextureRaw( byte[] data, int width, int height, ImageFormat format, int mipLevels )
	{
		if ( data is null ) return null;
		mipLevels = Math.Max( 1, mipLevels );
		if ( data.Length != LightmapChainBytes( width, height, LightmapBytesPerPixel( format ), mipLevels ) ) return null;
		try
		{
			var builder = Texture.Create( width, height, format );
			if ( mipLevels > 1 ) builder = builder.WithMips( mipLevels );
			var texture = builder.WithData( data ).Finish();
			if ( texture is not null && texture.IsValid() ) return texture;
		}
		catch ( Exception )
		{
			// Fall through: the caller retries with the untouched RGBA8888 page.
		}
		WarnLightmapFormatFallback( format );
		return null;
	}

	// Tries the packed 16-bit upload first, then the untouched RGBA8888 page, so a backend without
	// the 16-bit format degrades instead of losing the channel. The same retry drops the mip chain:
	// the base level is what LOD 0 and clamped samplers read, so it is worth keeping even if the
	// backend rejects a multi-level upload.
	static Texture CreateLightmapChannel( byte[] rgba, int width, int height, ImageFormat format, int levels,
		Func<byte[], int, byte[]> packer )
	{
		var texture = CreateLightmapTexture( rgba, width, height, format, levels, packer );
		if ( texture is not null ) return texture;
		if ( levels > 1 ) texture = CreateLightmapTexture( rgba, width, height, format, 1, packer );
		if ( texture is not null ) return texture;
		return CreateLightmapTexture( rgba, width, height, ImageFormat.RGBA8888, 1, null );
	}

	Titanfall2MaterialPrefetch _materialPrefetch;
	WorldBuildResult BuildWorldModel( Titanfall2BspReader.ParsedBsp parsed, bool createFallbackCollision )
	{
		var vertexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Vertices.Length ?? 0 );
		var indexCount = parsed.World.Meshes.Sum( static mesh => mesh?.Indices.Length ?? 0 );
		if ( vertexCount == 0 || indexCount == 0 ) return null;
		// Cached once per build: the parallel triangle workers get it as an argument.
		var cellSize = WorldRenderCellSize;

		var groups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var translucentGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var decalGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var godrayGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var waterGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var lightmaps = Titanfall2StreamingSettings.UseBspLightmaps
			? CreateLightmapTextures( parsed )
			: null;
		var worldMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var decalMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var godrayMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var cutoutMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var translucentMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var waterMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var shadowOnlyGroups = new Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>();
		var shadowOnlyMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var shadowOnlyMeshCount = 0;
		var skippedToolMeshes = 0;
		var skippedGodrayMeshes = 0;
		var decalMeshCount = 0;
		var godrayMeshCount = 0;
		var visualEffectCollisionMeshCount = 0;
		// Classification stays serial: material metadata, the material name sets and the
		// fallback collision counters are shared state. Only the triangle work is parallel.
		var sources = new List<WorldMeshSource>( parsed.World.Meshes.Count );
		foreach ( var sourceMesh in parsed.World.Meshes )
		{
			if ( sourceMesh is null || sourceMesh.Vertices.Length == 0 || sourceMesh.Indices.Length < 3 ) continue;
			var materialName = NormalizeMaterialName( sourceMesh.MaterialName );
			// tools/toolsblocklight is an invisible light blocker in the original game. Drawing it
			// showed green "LIGHT BLOCK" walls; it now only enters the shadow pass (or nothing,
			// when map shadows are off), so it still keeps sunlight out of the spaces it seals.
			var isShadowOnly = Titanfall2CollisionFilter.IsNonSolidToolName( materialName );
			if ( ShouldSkipWorldMaterial( materialName ) || (isShadowOnly && !Titanfall2StreamingSettings.MapShadows) )
			{
				skippedToolMeshes++;
				continue;
			}
			var isGodray = MaterialLoader.IsGodrayMaterial( materialName );
			if ( isGodray && !Titanfall2StreamingSettings.Godrays )
			{
				// Additive light-shaft cards: skipping them removes the bright cards that can
				// flash while the camera moves, without touching opaque world geometry.
				skippedGodrayMeshes++;
				continue;
			}
			var materialMetadata = GetMaterialMetadata( materialName );
			var isDecal = !isGodray && materialMetadata.IsDecal;
			var allowsFallbackCollision = Titanfall2CollisionFilter.ShouldIncludeMaterial( materialName, materialMetadata );
			var includeCollision = createFallbackCollision && allowsFallbackCollision;
			if ( !isDecal && materialMetadata.Mode == Titanfall2MaterialMode.Cutout ) cutoutMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsTranslucent ) translucentMaterials.Add( materialName );
			if ( !isDecal && materialMetadata.IsWater ) waterMaterials.Add( materialName );
			// Aggregate scene objects do not supply framebuffer copies for water.
			// Keep refractive surfaces on a dedicated translucent renderer.
			// Native aggregate objects enter the opaque/prepass lists. Transparent
			// grids and screens need ordinary renderers to reach the translucent
			// pass with correct depth testing and sorting.
			// Native s&box lighting does not need one material/draw group per
			// Respawn lightmap page. Merge those pages when the experimental
			// lightmap path is disabled to reduce both memory and draw calls.
			var lightmapPage = Titanfall2StreamingSettings.UseBspLightmaps && !isGodray && !isDecal && !isShadowOnly
				? sourceMesh.LightmapPage
				: -1;
			var category = isShadowOnly ? WorldGroupCategory.ShadowOnly
				: isGodray ? WorldGroupCategory.Godray
				: isDecal ? WorldGroupCategory.Decal
				: materialMetadata.IsWater ? WorldGroupCategory.Water
				: materialMetadata.IsTranslucent ? WorldGroupCategory.Translucent
				: WorldGroupCategory.Opaque;
			var targetMaterials = isShadowOnly ? shadowOnlyMaterials : isGodray ? godrayMaterials : isDecal ? decalMaterials : worldMaterials;
			if ( isShadowOnly ) shadowOnlyMeshCount++;
			targetMaterials.Add( materialName );
			if ( isGodray ) godrayMeshCount++;
			if ( isDecal ) decalMeshCount++;
			sources.Add( new WorldMeshSource
			{
				Mesh = sourceMesh,
				MaterialKey = new WorldMaterialKey( materialName, lightmapPage ),
				Category = category,
				IsDecal = isDecal,
				IncludeCollision = includeCollision
			} );
			if ( createFallbackCollision && !allowsFallbackCollision && !isGodray && !isDecal )
				visualEffectCollisionMeshCount++;
		}

		// Bucket each source mesh's triangles by spatial cell. A worker only touches the mesh it
		// owns, so nothing here is shared and no lock is needed.
		var groupingTimer = System.Diagnostics.Stopwatch.StartNew();
		var buckets = new WorldMeshCellBuckets[sources.Count];
		Parallel.For( 0, sources.Count, index => buckets[index] = BucketMeshTriangles( sources[index], cellSize ) );

		// Collect one contribution list per cell in source mesh order, so the cell workers below
		// emit triangles in exactly the order the previous serial loop emitted them.
		var cellContributions = new Dictionary<WorldRenderCell, List<WorldCellContribution>>();
		var cells = new List<WorldRenderCell>( sources.Count );
		for ( var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++ )
		{
			foreach ( var cellTriangles in buckets[sourceIndex].Cells )
			{
				if ( !cellContributions.TryGetValue( cellTriangles.Cell, out var contributions ) )
				{
					contributions = new List<WorldCellContribution>();
					cellContributions.Add( cellTriangles.Cell, contributions );
					cells.Add( cellTriangles.Cell );
				}
				contributions.Add( new WorldCellContribution( sourceIndex, cellTriangles.TriangleStarts ) );
			}
		}

		var cellWork = new List<WorldCellContribution>[cells.Count];
		for ( var cellIndex = 0; cellIndex < cells.Count; cellIndex++ )
			cellWork[cellIndex] = cellContributions[cells[cellIndex]];

		// One worker per spatial cell: every group a worker creates belongs to that cell, so no
		// other worker can reach the same MapMeshGroup.
		var cellGroups = new WorldCellGroups[cells.Count];
		Parallel.For( 0, cells.Count, cellIndex =>
			cellGroups[cellIndex] = BuildCellGroups( cellWork[cellIndex], sources ) );

		// Publish each finished cell into its category's dictionary. The cell already owns its
		// groups, so this moves them into place instead of copying vertex data.
		for ( var cellIndex = 0; cellIndex < cells.Count; cellIndex++ )
		{
			var materials = cellGroups[cellIndex].Materials;
			for ( var categoryIndex = 0; categoryIndex < materials.Count; categoryIndex++ )
			{
				var cellMaterials = materials[categoryIndex];
				if ( cellMaterials is null || cellMaterials.Count == 0 ) continue;
				var groupCategory = (WorldGroupCategory)categoryIndex;
				var targetGroups = groupCategory switch
				{
					WorldGroupCategory.Translucent => translucentGroups,
					WorldGroupCategory.Decal => decalGroups,
					WorldGroupCategory.Godray => godrayGroups,
					WorldGroupCategory.Water => waterGroups,
					WorldGroupCategory.ShadowOnly => shadowOnlyGroups,
					_ => groups
				};
				targetGroups.Add( cells[cellIndex], cellMaterials );
			}
		}

		// Fallback collision consumes the same triangle corners in the same order as before, so it
		// comes from the same per-mesh buckets. Retail maps with complete CM collision never build
		// this, and then neither list is allocated.
		List<Vector3> collisionPositions = null;
		List<int> collisionIndices = null;
		if ( createFallbackCollision )
		{
			collisionPositions = new List<Vector3>( indexCount );
			collisionIndices = new List<int>( indexCount );
			for ( var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++ )
			{
				var meshBuckets = buckets[sourceIndex];
				if ( meshBuckets.CollisionPositions is null || meshBuckets.CollisionPositions.Count == 0 ) continue;
				var baseVertex = collisionPositions.Count;
				collisionPositions.AddRange( meshBuckets.CollisionPositions );
				foreach ( var collisionIndex in meshBuckets.CollisionIndices )
					collisionIndices.Add( baseVertex + collisionIndex );
			}
		}
		groupingTimer.Stop();

		if ( groups.Count == 0 && translucentGroups.Count == 0 && waterGroups.Count == 0 ) return null;
		var worldGroupCount = groups.Values.Sum( static cell => cell.Count );
		var decalGroupCount = decalGroups.Values.Sum( static cell => cell.Count );
		var godrayGroupCount = godrayGroups.Values.Sum( static cell => cell.Count );
		// Largest single (cell, material) group; every group of the non-aggregate categories becomes
		// one mesh draw, so this is the size a bigger cell must keep bounded. The aggregate path
		// reports its own fragment sizes.
		var largestGroup = LargestGroup( groups, translucentGroups, decalGroups, godrayGroups, waterGroups, shadowOnlyGroups );
		// One fragment is a sub-range of one mesh buffer, and the aggregate path already uploads a
		// whole material per mesh, so a large cell cannot hit a vertex-count limit the current build
		// does not already hit. A group this big is still a 44 MB draw, though: say so instead of
		// letting it show up as a first-draw hitch.
		const int LargestGroupWarningVertices = 1_000_000;
		if ( largestGroup.Vertices > LargestGroupWarningVertices )
			Log.Warning( $"Titanfall 2 world cell {cellSize:0}u produces a {largestGroup.Vertices} vertex group "
				+ $"({largestGroup.Indices} indices); lower SBOX_TITANFALL2_WORLD_CELL_SIZE if it stalls." );
		Log.Info( $"Titanfall 2 map geometry grouped: {Path} ({worldMaterials.Count} world materials across "
			+ $"{groups.Count} spatial cells/{worldGroupCount} local meshes including "
			+ $"{cutoutMaterials.Count} cutout, {translucentMaterials.Count} translucent and {waterMaterials.Count} water; "
			+ $"{decalMaterials.Count} decal materials across {decalGroups.Count} cells/{decalGroupCount} local meshes, "
			+ $"{godrayMaterials.Count} Godray materials across {godrayGroups.Count} cells/{godrayGroupCount} local meshes, "
			+ $"{vertexCount} source vertices, {indexCount / 3} source triangles, {skippedToolMeshes} invisible tool meshes skipped, "
			+ $"{(skippedGodrayMeshes > 0 ? $"{skippedGodrayMeshes} godray meshes skipped by switch" : "godrays enabled")}, "
			+ $"{decalMeshCount} decal, {godrayMeshCount} Godray and {visualEffectCollisionMeshCount} "
			+ (createFallbackCollision
				? "visual FX meshes excluded from fallback collision"
				: "render collision disabled; authoritative rBSP contents used")
			+ $", cell {cellSize:0}u, largest group {largestGroup.Vertices}V/{largestGroup.Indices}I, "
			+ $"grouped in {groupingTimer.Elapsed.TotalSeconds:0.00}s)" );
		var renderTimer = System.Diagnostics.Stopwatch.StartNew();
		// Match consumption order. A queued water material held until the end must
		// not occupy every look-ahead slot while opaque materials load synchronously.
		var worldOrder = worldMaterials.Except( waterMaterials, StringComparer.OrdinalIgnoreCase )
			.Except( translucentMaterials, StringComparer.OrdinalIgnoreCase ).OrderBy( static name => name, StringComparer.OrdinalIgnoreCase );
		using var prefetch = new Titanfall2MaterialPrefetch( Host,
			worldOrder.Concat( EnumerateChunkMaterials( translucentGroups ) ).Concat( EnumerateChunkMaterials( decalGroups ) )
				.Concat( EnumerateChunkMaterials( godrayGroups ) ).Concat( EnumerateChunkMaterials( waterGroups ) )
				.Select( name => Host.TryGetMaterialPath( name, out var registered ) ? registered : name ) );
		_materialPrefetch = prefetch;
		// G4: world shadows can come from a depth-only copy of the opaque world instead of the
		// forward aggregates; the copy still exists here so this stays independent of SHADOW_CASCADES.
		var useShadowGeometry = Titanfall2StreamingSettings.MapShadows && Titanfall2StreamingSettings.ShadowGeometry;
		var worldAggregates = Titanfall2WorldAggregator.Build(
			groups, parsed.MapName, "world", cellSize, Path, Log,
			( materialKey, meshIndex ) => CreateWorldLightmappedMaterial(
				materialKey,
				meshIndex,
				lightmaps ),
			out var worldRenderMeshCount,
			out var worldAggregateKeys );
		// Per-phase timings show whether world geometry, decals or Godrays moved the load time.
		var worldPhase = renderTimer.Elapsed;
		var translucentChunks = BuildRenderChunks( translucentGroups, parsed.MapName, "translucent",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateRuntimeMaterial( $"{Path}/translucent_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var translucentRenderMeshCount );
		var translucentPhase = renderTimer.Elapsed - worldPhase;
		var decalChunks = BuildRenderChunks(
			decalGroups, parsed.MapName, "decals",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateDecalMaterial( $"{Path}/decal_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var decalRenderMeshCount );
		var decalPhase = renderTimer.Elapsed - worldPhase - translucentPhase;
		var godrayChunks = BuildRenderChunks(
			godrayGroups, parsed.MapName, "godrays",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateGodrayMaterial( $"{Path}/godray_fallback_{meshIndex}", materialKey.MaterialName ),
			out var godrayRenderMeshCount );
		var godrayPhase = renderTimer.Elapsed - worldPhase - translucentPhase - decalPhase;
		var waterChunks = BuildRenderChunks( waterGroups, parsed.MapName, "water",
			( materialKey, meshIndex ) => LoadWorldMaterial( materialKey.MaterialName )
				?? MaterialLoader.CreateWaterMaterial( $"{Path}/water_fallback_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var waterRenderMeshCount );
		var waterPhase = renderTimer.Elapsed - worldPhase - translucentPhase - decalPhase - godrayPhase;
		// Only depth reaches the shadow map, so a plain runtime material replaces the tool texture.
		var shadowOnlyChunks = BuildRenderChunks( shadowOnlyGroups, parsed.MapName, "shadow_only",
			( materialKey, meshIndex ) => MaterialLoader.CreateRuntimeMaterial( $"{Path}/shadow_only_{meshIndex}", GetMaterialMetadata( materialKey.MaterialName ) ),
			out var shadowOnlyRenderMeshCount );
		if ( worldAggregates.Count == 0 && translucentChunks.Count == 0 && waterChunks.Count == 0 ) return null;
		Log.Info( $"Titanfall 2 spatial render models created: {Path} "
			+ $"({worldAggregates.Count} world aggregates/{worldRenderMeshCount} meshes, "
			+ $"{translucentChunks.Count} translucent chunks/{translucentRenderMeshCount} meshes, "
			+ $"{decalChunks.Count} decal chunks/{decalRenderMeshCount} meshes, "
			+ $"{godrayChunks.Count} Godray chunks/{godrayRenderMeshCount} meshes; "
			+ $"{waterChunks.Count} water chunks/{waterRenderMeshCount} meshes, "
			+ $"{shadowOnlyChunks.Count} shadow-only light-blocker chunks/{shadowOnlyRenderMeshCount} meshes from {shadowOnlyMeshCount} source meshes; "
			+ $"world {worldPhase.TotalSeconds:0.00}s, translucent {translucentPhase.TotalSeconds:0.00}s, decals {decalPhase.TotalSeconds:0.00}s, "
			+ $"Godrays {godrayPhase.TotalSeconds:0.00}s, "
			+ $"water {waterPhase.TotalSeconds:0.00}s, "
			+ $"{renderTimer.Elapsed.TotalSeconds:0.00}s)" );

		// Depth-only world shadow geometry: same opaque world triangles, one mesh per spatial cell
		// (so the cascades still cull per cell) and one shared runtime material (so the shadow pass
		// binds a single depth shader instead of every forward material). Built after the null check
		// above so a rejected build never creates models nobody owns.
		IReadOnlyList<WorldRenderChunk> worldShadowChunks = null;
		if ( useShadowGeometry )
		{
			var shadowTimer = System.Diagnostics.Stopwatch.StartNew();
			var shadowMaterial = MaterialLoader.CreateRuntimeMaterial( $"{Path}/world_shadow_only" );
			worldShadowChunks = BuildWorldShadowChunks( groups, cutoutMaterials, parsed.MapName, shadowMaterial,
				out var shadowTriangleCount, out var shadowBytes );
			Log.Info( $"Titanfall 2 shadow-only world geometry: {Path} ({worldShadowChunks.Count} chunks, "
				+ $"{shadowTriangleCount} triangles, {shadowBytes / (1024.0 * 1024.0):0.0} MB, "
				+ $"built in {shadowTimer.Elapsed.TotalSeconds:0.00}s)" );
			if ( worldShadowChunks.Count == 0 )
			{
				// Never trade the world's shadows for nothing: an empty copy means the aggregates
				// keep casting exactly as before.
				Log.Warning( $"Titanfall 2 shadow-only world geometry was empty; world aggregates keep casting ({Path})." );
				worldShadowChunks = null;
				useShadowGeometry = false;
			}
		}

		// Alpha-tested (cutout) materials are not part of the depth-only copy: a plain depth material
		// would make them cast solid shadows instead of their authored cut-outs, so they keep casting
		// through their own aggregate. Every other aggregate stops casting when the copy exists.
		var aggregateCastShadows = new bool[worldAggregates.Count];
		for ( var index = 0; index < aggregateCastShadows.Length; index++ )
			aggregateCastShadows[index] = useShadowGeometry
				? index < worldAggregateKeys.Count && cutoutMaterials.Contains( worldAggregateKeys[index].MaterialName )
				: Titanfall2StreamingSettings.MapShadows;

		Model collisionModel = null;
		if ( collisionPositions is { Count: > 0 } && collisionIndices is { Count: > 0 } )
		{
			var collisionBuilder = Model.Builder.WithName( $"titanfall2/map/{parsed.MapName}_collision" );
			// List overloads upload straight from the existing lists; ToArray() here copied 37MB of
			// collision data for nothing.
			collisionBuilder.AddCollisionMesh( collisionPositions, collisionIndices );
			collisionBuilder.AddTraceMesh( collisionPositions, collisionIndices );
			collisionModel = collisionBuilder.Create();
			Log.Info( $"Titanfall 2 fallback render collision submitted: {Path} ({collisionIndices.Count / 3} triangles)" );
		}
		return new WorldBuildResult( translucentChunks, decalChunks, godrayChunks, waterChunks, worldAggregates, collisionModel,
			lightmaps?.OwnedResources ?? Array.Empty<IDisposable>(), shadowOnlyChunks, worldShadowChunks, aggregateCastShadows );
	}

	/// <summary>
	/// Builds the depth-only world shadow geometry: merges all opaque (non-cutout) world groups of one
	/// spatial cell into a single mesh that shares one runtime material with every other cell. The
	/// triangle set, winding order and vertex data are the ones the forward aggregates already hold,
	/// so the shadow map sees the same surfaces at the same depth; only the submission cost changes.
	/// </summary>
	List<WorldRenderChunk> BuildWorldShadowChunks(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		HashSet<string> excludedMaterials,
		string mapName,
		Material material,
		out int triangleCount,
		out long bytes )
	{
		var result = new List<WorldRenderChunk>( chunks.Count );
		triangleCount = 0;
		bytes = 0;
		foreach ( var (cell, materials) in chunks
			.OrderBy( static pair => pair.Key.X )
			.ThenBy( static pair => pair.Key.Y )
			.ThenBy( static pair => pair.Key.Z ) )
		{
			var groups = materials
				.Where( static pair => pair.Value.Vertices.Count > 0 && pair.Value.Indices.Count > 0 )
				.Where( pair => !excludedMaterials.Contains( pair.Key.MaterialName ) )
				.OrderBy( static pair => pair.Key.MaterialName, StringComparer.OrdinalIgnoreCase )
				.ThenBy( static pair => pair.Key.LightmapPage )
				.ToArray();
			if ( groups.Length == 0 ) continue;

			var vertexCount = 0;
			var indexCount = 0;
			foreach ( var (_, group) in groups )
			{
				vertexCount += group.Vertices.Count;
				indexCount += group.Indices.Count;
			}

			var vertices = new List<TitanfallMapVertex>( vertexCount );
			var indices = new List<int>( indexCount );
			var bounds = groups[0].Value.Bounds;
			foreach ( var (_, group) in groups )
			{
				var offset = vertices.Count;
				vertices.AddRange( group.Vertices );
				foreach ( var index in group.Indices )
					indices.Add( offset + index );
			}
			for ( var index = 1; index < groups.Length; index++ )
				bounds = bounds.AddBBox( groups[index].Value.Bounds );

			var builder = Model.Builder.WithName( $"titanfall2/map/{mapName}_shadow_{cell}" );
			var mesh = new Mesh( $"{Path}_shadow_{cell}", material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = bounds;
			builder.AddMesh( mesh );
			result.Add( new WorldRenderChunk( cell, builder.Create() ) );
			triangleCount += indices.Count / 3;
			bytes += (long)vertices.Count * System.Runtime.InteropServices.Marshal.SizeOf<TitanfallMapVertex>()
				+ (long)indices.Count * sizeof( int );
		}
		return result;
	}

	static IEnumerable<string> EnumerateChunkMaterials( Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks ) =>
		chunks.OrderBy( pair => pair.Key.X ).ThenBy( pair => pair.Key.Y ).ThenBy( pair => pair.Key.Z )
			.SelectMany( pair => pair.Value.Keys.OrderBy( key => key.MaterialName, StringComparer.OrdinalIgnoreCase )
				.ThenBy( key => key.LightmapPage ) ).Select( key => key.MaterialName );

	/// <summary>
	/// Largest single (cell, material) group across the given dictionaries, as vertices and indices.
	/// Reported so a cell-size change can be judged against the per-draw buffer size it produces.
	/// </summary>
	static (int Vertices, int Indices) LargestGroup(
		params Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>>[] groupSets )
	{
		var vertices = 0;
		var indices = 0;
		foreach ( var chunkSet in groupSets )
		{
			foreach ( var materials in chunkSet.Values )
			{
				foreach ( var group in materials.Values )
				{
					vertices = Math.Max( vertices, group.Vertices.Count );
					indices = Math.Max( indices, group.Indices.Count );
				}
			}
		}
		return (vertices, indices);
	}

	internal static TitanfallMapVertex CreateWorldVertex( Titanfall2BspReader.WorldVertex sourceVertex, bool isDecal )
	{
		var normal = ToSandbox( sourceVertex.Normal ).Normal;
		var position = ToSandbox( sourceVertex.Position );
		if ( isDecal ) position = DecalOffsetPosition( position, normal );
		return new TitanfallMapVertex
		{
			Position = position,
			Normal = normal,
			TexCoord = ToSandbox( sourceVertex.TexCoord ),
			LightmapTexCoord = ToSandbox( sourceVertex.LightmapTexCoord ),
			Color = new Color32( sourceVertex.Color.R, sourceVertex.Color.G, sourceVertex.Color.B, sourceVertex.Color.A )
		};
	}

	/// <summary>
	/// Position a triangle corner is grouped and collided at. Uses the same decal offset as
	/// <see cref="CreateWorldVertex"/> so cell selection keeps matching the stored vertex.
	/// </summary>
	static Vector3 SourceTrianglePosition( Titanfall2BspReader.WorldVertex vertex, bool isDecal )
	{
		var position = ToSandbox( vertex.Position );
		if ( !isDecal ) return position;
		return DecalOffsetPosition( position, ToSandbox( vertex.Normal ).Normal );
	}

	static Vector3 DecalOffsetPosition( Vector3 position, Vector3 normal ) => position + normal * DecalNormalOffset;

	/// <summary>
	/// Buckets one source mesh's triangles by spatial cell and, when fallback collision needs it,
	/// emits the triangle corner positions the serial loop used to append.
	/// </summary>
	static WorldMeshCellBuckets BucketMeshTriangles( WorldMeshSource source, float cellSize )
	{
		var mesh = source.Mesh;
		var vertices = mesh.Vertices;
		var indices = mesh.Indices;
		var result = new WorldMeshCellBuckets
		{
			CollisionPositions = source.IncludeCollision ? new List<Vector3>( indices.Length ) : null,
			CollisionIndices = source.IncludeCollision ? new List<int>( indices.Length ) : null
		};
		var cells = new Dictionary<WorldRenderCell, List<int>>();
		for ( var index = 0; index + 2 < indices.Length; index += 3 )
		{
			var sourceIndex0 = indices[index];
			var sourceIndex1 = indices[index + 1];
			var sourceIndex2 = indices[index + 2];
			if ( (uint)sourceIndex0 >= vertices.Length
				|| (uint)sourceIndex1 >= vertices.Length
				|| (uint)sourceIndex2 >= vertices.Length ) continue;

			var position0 = SourceTrianglePosition( vertices[sourceIndex0], source.IsDecal );
			var position1 = SourceTrianglePosition( vertices[sourceIndex1], source.IsDecal );
			var position2 = SourceTrianglePosition( vertices[sourceIndex2], source.IsDecal );
			var cell = WorldRenderCell.FromTriangle( position0, position1, position2, cellSize );
			if ( !cells.TryGetValue( cell, out var triangles ) )
			{
				triangles = new List<int>();
				cells.Add( cell, triangles );
				result.Cells.Add( new WorldCellTriangles( cell, triangles ) );
			}
			triangles.Add( index );
			if ( result.CollisionPositions is null ) continue;

			var collisionBase = result.CollisionPositions.Count;
			result.CollisionPositions.Add( position0 );
			result.CollisionPositions.Add( position1 );
			result.CollisionPositions.Add( position2 );
			result.CollisionIndices.Add( collisionBase );
			result.CollisionIndices.Add( collisionBase + 2 );
			result.CollisionIndices.Add( collisionBase + 1 );
		}
		return result;
	}

	/// <summary>
	/// Builds every group of one spatial cell. The worker that owns the cell is the only thread that
	/// touches these groups, so the material dictionaries and vertex lists need no lock.
	/// </summary>
	static WorldCellGroups BuildCellGroups( IReadOnlyList<WorldCellContribution> contributions, IReadOnlyList<WorldMeshSource> sources )
	{
		var result = new WorldCellGroups();
		foreach ( var contribution in contributions )
		{
			var source = sources[contribution.SourceIndex];
			var mesh = source.Mesh;
			var group = result.GetGroup( source.Category, source.MaterialKey );
			// One slot per source vertex inside this cell. The BSP reader already compacted the
			// source triangles onto a vertex table, so an int array beats a dictionary here.
			var remap = new int[mesh.Vertices.Length];
			Array.Fill( remap, -1 );
			foreach ( var start in contribution.TriangleStarts )
			{
				var local0 = AppendGroupVertex( group, remap, mesh.Vertices, mesh.Indices[start], source.IsDecal, out var position0 );
				var local1 = AppendGroupVertex( group, remap, mesh.Vertices, mesh.Indices[start + 1], source.IsDecal, out var position1 );
				var local2 = AppendGroupVertex( group, remap, mesh.Vertices, mesh.Indices[start + 2], source.IsDecal, out var position2 );
				// Same corner data and winding flip as the serial loop; only repeated vertices are shared.
				group.Indices.Add( local0 );
				group.Indices.Add( local2 );
				group.Indices.Add( local1 );
				group.Bounds = group.Bounds
					.AddPoint( position0 )
					.AddPoint( position1 )
					.AddPoint( position2 );
			}
		}
		return result;
	}

	/// <summary>Returns the group-local index of one source vertex inside a cell, adding it on first use.</summary>
	static int AppendGroupVertex( MapMeshGroup group, int[] remap, Titanfall2BspReader.WorldVertex[] vertices,
		int sourceIndex, bool isDecal, out Vector3 position )
	{
		var slot = remap[sourceIndex];
		if ( slot >= 0 )
		{
			position = group.Vertices[slot].Position;
			return slot;
		}

		var vertex = CreateWorldVertex( vertices[sourceIndex], isDecal );
		slot = group.Vertices.Count;
		group.Vertices.Add( vertex );
		remap[sourceIndex] = slot;
		position = vertex.Position;
		return slot;
	}

	List<WorldRenderChunk> BuildRenderChunks(
		Dictionary<WorldRenderCell, Dictionary<WorldMaterialKey, MapMeshGroup>> chunks,
		string mapName,
		string category,
		Func<WorldMaterialKey, int, Material> materialFactory,
		out int totalMeshCount )
	{
		var result = new List<WorldRenderChunk>( chunks.Count );
		totalMeshCount = 0;
		foreach ( var (cell, materials) in chunks
			.OrderBy( static pair => pair.Key.X )
			.ThenBy( static pair => pair.Key.Y )
			.ThenBy( static pair => pair.Key.Z ) )
		{
			var builder = Model.Builder.WithName( $"titanfall2/map/{mapName}_{category}_{cell}" );
			var chunkMeshCount = 0;
			foreach ( var (materialKey, group) in materials
				.OrderBy( static pair => pair.Key.MaterialName, StringComparer.OrdinalIgnoreCase )
				.ThenBy( static pair => pair.Key.LightmapPage ) )
			{
				if ( group.Vertices.Count == 0 || group.Indices.Count == 0 ) continue;
				var material = materialFactory( materialKey, totalMeshCount );
				var mesh = new Mesh( $"{Path}_{category}_{cell}_{totalMeshCount}", material );
				mesh.CreateVertexBuffer( group.Vertices.Count, group.Vertices );
				mesh.CreateIndexBuffer( group.Indices.Count, group.Indices );
				mesh.Bounds = group.Bounds;
				builder.AddMesh( mesh );
				chunkMeshCount++;
				totalMeshCount++;
			}
			if ( chunkMeshCount > 0 ) result.Add( new WorldRenderChunk( cell, builder.Create() ) );
		}
		return result;
	}

	Material CreateWorldLightmappedMaterial( WorldMaterialKey key, int meshIndex, RuntimeLightmaps lightmaps )
	{
		var sourceMaterial = LoadWorldMaterial( key.MaterialName )
			?? MaterialLoader.CreateRuntimeMaterial( $"{Path}/world_fallback_{meshIndex}", GetMaterialMetadata( key.MaterialName ) );
		if ( !Titanfall2StreamingSettings.UseBspLightmaps )
			return sourceMaterial;
		var metadata = GetMaterialMetadata( key.MaterialName );
		if ( lightmaps is null || key.LightmapPage < 0 || key.LightmapPage >= lightmaps.Pages.Count
			|| metadata.Mode != Titanfall2MaterialMode.Opaque || metadata.IsUnlit || metadata.IsWater )
			return sourceMaterial;

		var page = lightmaps.Pages[key.LightmapPage];
		if ( page.SkyA is null || !page.SkyA.IsValid || page.SkyB is null || !page.SkyB.IsValid )
			return sourceMaterial;

		return CreateLightmappedMaterial( $"{Path}/world_lightmapped_{meshIndex}_{key.LightmapPage}", sourceMaterial, page, lightmaps.SkyColor );
	}

	internal static Material CreateLightmappedMaterial( string name, Material sourceMaterial, RuntimeLightmapPage page, Color skyColor )
	{
		var material = Material.Create( name, "shaders/titanfall2_world_lightmapped.shader" );
		CopyWorldMaterialParameters( sourceMaterial, material );
		material.Set( "g_tLightmapSkyA", page.SkyA );
		material.Set( "g_tLightmapSkyB", page.SkyB );
		if ( page.RealTimeA is not null ) material.Set( "g_tLightmapRealTimeA", page.RealTimeA );
		if ( page.RealTimeB is not null ) material.Set( "g_tLightmapRealTimeB", page.RealTimeB );
		if ( page.SunChannels is not null ) material.Set( "g_tLightmapSunChannels", page.SunChannels );
		material.Set( "g_flT2HasSunLightmap", page.SunChannels is not null ? 1f : 0f );
		material.Set( "g_flT2LightmapIntensity", 1f );
		material.Set( "g_vT2LightmapSkyColor", new Vector3( skyColor.r, skyColor.g, skyColor.b ) );
		material.Set( "g_flT2RealTimeLightContribution", 0f );
		return material;
	}

	static void CopyWorldMaterialParameters( Material source, Material target )
	{
		string[] textureNames =
		[
			"g_tAlbedo", "g_tNormal", "g_tGloss", "g_tSpecular", "g_tAO", "g_tCavity", "g_tOpacity",
			"g_tEmissive", "g_tEmissiveMultiply", "g_tDetail", "g_tDistortion", "g_tEnvironment",
			"g_tAlbedo2", "g_tNormal2", "g_tGloss2", "g_tSpecular2", "g_tLayerBlend"
		];
		foreach ( var name in textureNames )
		{
			var texture = source?.GetTexture( name );
			if ( texture is not null && texture.IsValid )
				target.Set( name, texture );
		}

		string[] vectorNames =
		[
			"g_flT2GlossScale", "g_vT2SpecularTint", "g_flT2EmissiveStrength",
			"g_flT2DetailBlend", "g_vT2UvDistortion", "g_flT2HasEnvironment",
			"g_flT2EnvironmentIntensity", "g_flT2FresnelStrength", "g_vT2Uv1RotScale",
			"g_vT2Uv1Translate", "g_vT2AlbedoTint", "g_vT2EmissiveTint",
			"g_flT2MaterialOpacity", "g_flT2AlphaTestReference", "g_flUseVertexColor",
			"g_flT2Layered", "g_flT2LayerBlendRamp", "g_flHasNormalMap", "g_flHasNormalMap2",
			"g_flT2SpecularWorkflow",
			"g_flT2HasCavity", "g_flT2CavityUvChannel",
			"g_flT2HasEmissiveMultiply", "g_flT2EmissiveMultiplyUvChannel", "g_vT2AuxUvChannels",
			"g_vT2SurfaceUvChannels", "g_vT2SurfaceUvChannels2", "g_flT2LayerBlendUvChannel",
			"g_vT2Uv2RotScale", "g_vT2Uv2Translate", "g_vT2Uv3RotScale", "g_vT2Uv3Translate",
			"g_flT2NormalSlope", "g_flT2NormalSlope2", "g_flT2NormalTwoChannel", "g_flT2NormalTwoChannel2"
		];
		foreach ( var name in vectorNames )
			target.Set( name, source?.GetVector4( name ) ?? default );
	}

	RuntimeLightmaps CreateLightmapTextures( Titanfall2BspReader.ParsedBsp parsed )
	{
		var source = parsed.Lightmaps;
		if ( source is null || !source.IsValid ) return null;
		// The coverage mask is only needed by the dilate + mip path: one pass over the world lightmap
		// UVs. A default run (mips off) does not pay for it and logs nothing extra.
		var mips = LightmapMips;
		var skippedTriangles = 0;
		var coverage = mips ? BuildLightmapCoverage( parsed.World.Meshes, source.Pages, out skippedTriangles ) : null;
		var mipTimer = mips ? System.Diagnostics.Stopwatch.StartNew() : null;
		var pages = new List<RuntimeLightmapPage>( source.Pages.Count );
		long totalBytes = 0;
		for ( var index = 0; index < source.Pages.Count; index++ )
		{
			var page = source.Pages[index];
			var runtime = CreateLightmapPage( page, source.RealTimeLightPages, parsed.WorldLights,
				coverage is not null && index < coverage.Length ? coverage[index] : null, Path );
			var pageBytes = LightmapPageBytes( runtime );
			totalBytes += pageBytes;
			pages.Add( runtime );
			Log.Info( $"Titanfall 2 lightmap page {index}: {page.Width}x{page.Height}, "
				+ $"SkyA {LightmapChannelSummary( runtime.SkyA )}, SkyB {LightmapChannelSummary( runtime.SkyB )}, "
				+ $"RealTimeA {LightmapChannelSummary( runtime.RealTimeA )}, "
				+ $"RealTimeB {LightmapChannelSummary( runtime.RealTimeB )}, "
				+ $"sun {LightmapChannelSummary( runtime.SunChannels )}; "
				+ $"{pageBytes / (1024.0 * 1024.0):0.00} MB ({Path})." );
		}
		Log.Info( $"Titanfall 2 lightmap upload: {pages.Count} pages, {totalBytes / (1024.0 * 1024.0):0.00} MB total, "
			+ $"{(Lightmap16Bit ? "16-bit SkyB/RealTimeB (RGB565/BGRA4444)" : "RGBA8888 SkyB/RealTimeB")}"
			+ $"{(mips ? $", mips L0..L{LightmapMipLevelCount - 1} (dilate {LightmapDilationSteps})" : string.Empty)}"
			+ $" ({Path})." );
		if ( mipTimer is not null )
		{
			mipTimer.Stop();
			var coveredPages = coverage is null ? 0 : Array.FindAll( coverage, static mask => mask is not null ).Length;
			Log.Info( $"Titanfall 2 lightmap coverage: {coveredPages} pages with geometry, {skippedTriangles} skipped triangles, "
				+ $"{mipTimer.Elapsed.TotalMilliseconds:0.0} ms for dilation and mips ({Path})." );
			if ( mipTimer.Elapsed.TotalMilliseconds > 120.0 )
				Titanfall2Log.ReportSpike( mipTimer, "lightmap dilation/mips for {0}", Path );
		}
		return new RuntimeLightmaps( pages, ReadBakedSkyColor( parsed ) );
	}

	internal static RuntimeLightmapPage CreateLightmapPage( Titanfall2BspReader.BspLightmapPage page,
		IReadOnlyList<short> lightPages, IReadOnlyList<Titanfall2WorldLight> worldLights,
		bool[] coverage = null, string context = null )
	{
		// Respawn's atlas contains tightly packed lightmap charts and the gaps hold unrelated bytes,
		// so a plain mip chain smears those into the lit texels (soft rectangular blotches at
		// grazing angles). With mips enabled the chain is therefore built from a dilated copy: the
		// uncovered texels are filled from the nearest chart colour first, and the covered texels are
		// never written.
		var mipLevels = LightmapMips && coverage is not null ? LightmapLevels( page.Width, page.Height ) : 1;
		var stopwatch = mipLevels > 1 ? System.Diagnostics.Stopwatch.StartNew() : null;
		var skyA = page.SkyA;
		var skyB = page.SkyB;
		var realTimeB = page.RealTimeB;
		var dilatedTexels = 0;
		if ( mipLevels > 1 )
		{
			skyA = DilateLightmap( skyA, page.Width, page.Height, coverage, LightmapDilationSteps, out dilatedTexels );
			skyB = DilateLightmap( skyB, page.Width, page.Height, coverage, LightmapDilationSteps, out _ );
			realTimeB = DilateLightmap( realTimeB, page.Width, page.Height, coverage, LightmapDilationSteps, out _ );
		}

		var sunChannels = Titanfall2WorldLight.CreateSunChannels( page.RealTimeA, lightPages, worldLights );
		var sunTexture = sunChannels.Length == page.Width * page.Height
			? Texture.Create( page.Width, page.Height, ImageFormat.I8 ).WithData( sunChannels ).Finish() : null;
		// RealTimeA and SunChannels are point-sampled with Load in the shader, so they get neither
		// dilation nor a mip chain (it would only cost memory). SkyA, SkyB and RealTimeB are filtered
		// through Tex2DS and are the three channels that benefit.
		// No shader samples the authored RealTimeC page, so no texture is created or bound for it;
		// Titanfall2BspReader.ReadLightmaps also skips its copy. That is one page-sized upload less
		// (1 B/px, i.e. 1/17 of the lightmap block) with no visual change.
		var runtime = new RuntimeLightmapPage(
			CreateLightmapChannel( skyA, page.Width, page.Height, ImageFormat.RGBA8888, mipLevels, null ),
			Lightmap16Bit
				? CreateLightmapChannel( skyB, page.Width, page.Height, ImageFormat.RGB565, mipLevels, PackRgb565 )
				: CreateLightmapChannel( skyB, page.Width, page.Height, ImageFormat.RGBA8888, mipLevels, null ),
			CreateLightmapTextureRaw( page.RealTimeA, page.Width, page.Height, ImageFormat.RGBA8888, 1 ),
			Lightmap16Bit
				? CreateLightmapChannel( realTimeB, page.Width, page.Height, ImageFormat.BGRA4444, mipLevels, PackBgra4444 )
				: CreateLightmapChannel( realTimeB, page.Width, page.Height, ImageFormat.RGBA8888, mipLevels, null ),
			sunTexture );

		if ( stopwatch is not null && coverage is not null )
		{
			stopwatch.Stop();
			var covered = 0;
			foreach ( var value in coverage )
				if ( value ) covered++;
			Log.Info( $"Titanfall 2 lightmap page mips: {page.Width}x{page.Height}, covered {covered}/{coverage.Length} texels "
				+ $"({(coverage.Length == 0 ? 0.0 : covered * 100.0 / coverage.Length):0.0}%), "
				+ $"dilated {dilatedTexels} texels in {LightmapDilationSteps} steps, mips {mipLevels} (L0..L{mipLevels - 1}), "
				+ $"{stopwatch.Elapsed.TotalMilliseconds:0.0} ms ({context ?? "lightmap"})." );
		}
		return runtime;
	}

	Color ReadBakedSkyColor( Titanfall2BspReader.ParsedBsp parsed )
	{
		var entities = ReadEntities( _environmentEntitySource );
		return ReadBakedSkyColor( parsed, entities.Count == 0 ? parsed.Entities : entities );
	}

	internal static Color ReadBakedSkyColor( Titanfall2BspReader.ParsedBsp parsed, IReadOnlyList<Dictionary<string, string>> entities )
	{
		var lightValues = Titanfall2LightEnvironment.Default( entities );
		var compiled = SelectBakedEnvironmentLight( parsed, lightValues, Titanfall2WorldLight.SkyAmbientType );
		if ( compiled is not null ) return new Color( compiled.Intensity.X, compiled.Intensity.Y, compiled.Intensity.Z );
		var color = TryParseSourceLightColor( lightValues?.GetValueOrDefault( "_ambient" ), 0.30f, out var authored )
			? authored : new Color( 0.22f, 0.28f, 0.36f );
		return color * AmbientScale;
	}

	Material LoadWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) || !Host.TryGetMaterialPath( materialName, out var registeredPath ) )
			return null;
		_materialPrefetch?.Wait( registeredPath );
		try
		{
			var material = Material.Load( $"mount://{Host.Ident}/{registeredPath}.vmat" );
			return material is not null && material.IsValid ? material : null;
		}
		finally { _materialPrefetch?.Consumed( registeredPath ); }
	}

	Titanfall2MaterialMetadata GetMaterialMetadata( string materialName )
	{
		if ( Host.TryGetMaterialMetadata( materialName, out var metadata ) ) return metadata;
		return MaterialLoader.InferMetadata( materialName );
	}

	static string NormalizeMaterialName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return string.Empty;
		var normalized = name.Replace( '\\', '/' ).Trim( '/' );
		foreach ( var extension in new[] { ".vmat", ".rpak", ".matl" } )
		{
			if ( normalized.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) )
				normalized = normalized[..^extension.Length];
		}
		return normalized;
	}

	static bool ShouldSkipWorldMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return false;
		return materialName.Contains( "toolsskybox", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsnodraw", StringComparison.OrdinalIgnoreCase )
			|| materialName.Contains( "toolsinvisible", StringComparison.OrdinalIgnoreCase );
	}

	static float DistanceSquared( System.Numerics.Vector3 source, Vector3 target )
	{
		var x = source.X - target.x;
		var y = source.Y - target.y;
		var z = source.Z - target.z;
		return x * x + y * y + z * z;
	}
}

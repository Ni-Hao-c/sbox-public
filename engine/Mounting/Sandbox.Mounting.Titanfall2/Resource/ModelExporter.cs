using System.Globalization;
using System.Text;
using Titanfall2.Formats;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;

/// <summary>
/// Exports a mounted Titanfall 2 model to editable Source model inputs.
/// The runtime mount stays independent from Sandbox.Tools; ModelDoc/contentbuilder
/// consumes the generated DMX and VMDL files in the editor afterwards.
/// </summary>
public partial class Titanfall2Mount
{
	const string ExportRootEnvironmentVariable = "SBOX_TITANFALL2_EXPORT_ROOT";
	static readonly UTF8Encoding ExportTextEncoding = new( false );
	static readonly Sandbox.Diagnostics.Logger ExportLog = new( "Titanfall2Export" );

	/// <summary>
	/// Export a mounted MDL/RMDL into an addon's Assets directory.
	/// Example:
	/// titanfall2_export_model "mount://titanfall2/models/robots/stalker/robot_stalker.mdl.vmdl" "D:/sbox/sbox-public/game/addons/myaddon/Assets"
	/// </summary>
	[ConCmd( "titanfall2_export_model", Help = "Export a mounted Titanfall 2 model to editable DMX/VMDL source files." )]
	public static void ExportModelCommand( string mountedPath, string outputRoot = null, bool overwrite = true )
	{
		var mount = Sandbox.Mounting.Directory.Get( "titanfall2" ) as Titanfall2Mount;
		if ( mount is null || !mount.IsMounted )
		{
			ExportLog.Error( "Titanfall 2 must be mounted before exporting a model." );
			return;
		}

		outputRoot = ResolveExportRoot( outputRoot );
		if ( mount.TryExportModel( mountedPath, outputRoot, overwrite, out var result, out var error ) )
		{
			ExportLog.Info( $"Titanfall 2 model exported: {result.SourcePath} -> {result.VmdlPath} "
				+ $"({result.MeshCount} meshes, {result.VertexCount} vertices, {result.TriangleCount} triangles, "
				+ $"{result.BoneCount} bones, {result.MaterialCount} material slots)." );
			if ( result.NonUnitScaleBoneCount > 0 )
			{
				ExportLog.Warning( $"The DMX model format stores only uniform bone scale; "
					+ $"{result.NonUnitScaleBoneCount} non-unit bind-pose scales were omitted. "
					+ "A later DMX/glTF export pass is required for those models." );
			}
			return;
		}

		ExportLog.Error( $"Titanfall 2 model export failed for '{mountedPath}': {error}" );
	}

	/// <summary>Exports one mounted model and returns the generated source paths.</summary>
	public bool TryExportModel(
		string mountedPath,
		string outputRoot,
		bool overwrite,
		out Titanfall2ModelExportResult result,
		out string error )
	{
		result = default;
		error = null;
		var sourcePath = NormalizeModelSourcePath( mountedPath );
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !IsModelSourcePath( sourcePath ) )
		{
			error = "Expected a mount://titanfall2/...mdl.vmdl or ...rmdl.vmdl path.";
			return false;
		}
		if ( !_modelSources.TryGetValue( sourcePath, out var source ) )
		{
			error = $"Mounted model source was not found: {sourcePath}";
			return false;
		}
		if ( string.IsNullOrWhiteSpace( outputRoot ) )
		{
			error = "The export root is empty. Pass an addon's Assets directory or set " + ExportRootEnvironmentVariable + ".";
			return false;
		}
		if ( !source.TryReadAllBytes( out var data, out error ) ) return false;
		return TryExportModelData( data, sourcePath, outputRoot, overwrite, out result, out error );
	}

	/// <summary>
	/// Exports one mounted model to an exact VMDL path inside an addon's Assets directory.
	/// This entry point is intended for editor Save As integrations.
	/// </summary>
	public bool TryExportModelToPath(
		string mountedPath,
		string assetsRoot,
		string destinationVmdl,
		bool overwrite,
		out Titanfall2ModelExportResult result,
		out string error )
	{
		result = default;
		error = null;
		var sourcePath = NormalizeModelSourcePath( mountedPath );
		if ( string.IsNullOrWhiteSpace( sourcePath ) || !IsModelSourcePath( sourcePath ) )
		{
			error = "Expected a mount://titanfall2/...mdl.vmdl or ...rmdl.vmdl path.";
			return false;
		}
		if ( !_modelSources.TryGetValue( sourcePath, out var source ) )
		{
			error = $"Mounted model source was not found: {sourcePath}";
			return false;
		}

		try
		{
			var root = Path.GetFullPath( assetsRoot );
			var destination = Path.GetFullPath( destinationVmdl );
			if ( !destination.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
			{
				error = "The destination file must use the .vmdl extension.";
				return false;
			}

			var relativeVmdl = Path.GetRelativePath( root, destination );
			if ( relativeVmdl.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal )
				|| Path.IsPathRooted( relativeVmdl ) )
			{
				error = "The destination model must be inside the active addon's Assets directory.";
				return false;
			}

			var relativeStem = relativeVmdl[..^Path.GetExtension( relativeVmdl ).Length];
			if ( !source.TryReadAllBytes( out var data, out error ) ) return false;
			return TryExportModelData( data, sourcePath, root, overwrite, out result, out error, relativeStem );
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>Exports an unpacked Titanfall 2 MDL/RMDL file with the same pipeline as a mounted model.</summary>
	public static bool TryExportModelFile(
		string sourceFile,
		string outputRoot,
		bool overwrite,
		out Titanfall2ModelExportResult result,
		out string error )
	{
		result = default;
		error = null;
		try
		{
			var fullPath = Path.GetFullPath( sourceFile );
			if ( !File.Exists( fullPath ) )
			{
				error = $"Model file was not found: {fullPath}";
				return false;
			}
			var sourcePath = NormalizeModelSourcePath( Path.GetFileName( fullPath ) );
			return TryExportModelData( File.ReadAllBytes( fullPath ), sourcePath, outputRoot, overwrite, out result, out error );
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	static bool TryExportModelData(
		byte[] data,
		string sourcePath,
		string outputRoot,
		bool overwrite,
		out Titanfall2ModelExportResult result,
		out string error,
		string relativeStemOverride = null )
	{
		result = default;
		error = null;
		try
		{
			var parsed = Titanfall2Mdl53Reader.Parse( data );
			var lod = Titanfall2MeshExtractor.ExtractLod( parsed, 0 );
			if ( lod.Meshes.Length == 0 )
			{
				error = "The model contains no exportable LOD0 geometry.";
				return false;
			}

			var relativeStem = string.IsNullOrWhiteSpace( relativeStemOverride )
				? GetExportRelativeStem( sourcePath )
				: relativeStemOverride;
			var root = Path.GetFullPath( outputRoot );
			var dmxPath = GetSafeExportPath( root, relativeStem + ".dmx" );
			var vmdlPath = GetSafeExportPath( root, relativeStem + ".vmdl" );
			if ( !overwrite && (File.Exists( dmxPath ) || File.Exists( vmdlPath )) )
			{
				error = $"Export already exists: {vmdlPath}";
				return false;
			}

			System.IO.Directory.CreateDirectory( Path.GetDirectoryName( dmxPath )! );
			var skeleton = BuildExportSkeleton( parsed );
			var materials = BuildExportMaterials( root, lod, overwrite );
			var dmxText = BuildReferenceDmx( Path.GetFileNameWithoutExtension( vmdlPath ), lod, skeleton, materials );
			var contentRelativeDmx = Path.GetRelativePath( root, dmxPath ).Replace( '\\', '/' );
			var vmdlText = BuildVmdl( Path.GetFileNameWithoutExtension( vmdlPath ), contentRelativeDmx );
			WriteExportFile( dmxPath, dmxText, overwrite );
			WriteExportFile( vmdlPath, vmdlText, overwrite );

			result = new Titanfall2ModelExportResult(
				sourcePath,
				dmxPath,
				vmdlPath,
				lod.Meshes.Length,
				lod.VertexCount,
				lod.TriangleCount,
				skeleton.Names.Length,
				materials.Count,
				skeleton.NonUnitScaleBoneCount );
			return true;
		}
		catch ( Exception exception )
		{
			error = exception.Message;
			return false;
		}
	}

	static string ResolveExportRoot( string requestedRoot )
	{
		if ( !string.IsNullOrWhiteSpace( requestedRoot ) ) return requestedRoot.Trim().Trim( '"' );
		var configured = Environment.GetEnvironmentVariable( ExportRootEnvironmentVariable );
		if ( !string.IsNullOrWhiteSpace( configured ) ) return configured.Trim().Trim( '"' );
		return Path.Combine( Environment.CurrentDirectory, "addons", "titanfall2_exports", "Assets" );
	}

	internal static string NormalizeModelSourcePath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return string.Empty;
		var normalized = path.Trim().Trim( '"' ).Replace( '\\', '/' );
		const string mountPrefix = "mount://titanfall2/";
		if ( normalized.StartsWith( mountPrefix, StringComparison.OrdinalIgnoreCase ) )
			normalized = normalized[mountPrefix.Length..];
		else if ( normalized.StartsWith( "titanfall2/", StringComparison.OrdinalIgnoreCase ) )
			normalized = normalized["titanfall2/".Length..];
		normalized = normalized.TrimStart( '/' );
		if ( normalized.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
			normalized = normalized[..^".vmdl".Length];
		var staticSuffix = normalized.IndexOf( ModelLoader.StaticInstanceSuffix, StringComparison.OrdinalIgnoreCase );
		if ( staticSuffix >= 0 ) normalized = normalized[..staticSuffix];
		return normalized;
	}

	static bool IsModelSourcePath( string path )
		=> path.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".rmdl", StringComparison.OrdinalIgnoreCase );

	static string GetExportRelativeStem( string sourcePath )
	{
		var extension = Path.GetExtension( sourcePath );
		var withoutExtension = sourcePath[..^extension.Length];
		var parts = withoutExtension.Split( '/', StringSplitOptions.RemoveEmptyEntries )
			.Select( SanitizePathPart )
			.Where( static part => !string.IsNullOrWhiteSpace( part ) )
			.ToArray();
		if ( parts.Length == 0 ) throw new InvalidOperationException( "The model path has no usable filename." );
		return Path.Combine( parts );
	}

	static string SanitizePathPart( string value )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var builder = new StringBuilder( value.Length );
		foreach ( var character in value )
			builder.Append( invalid.Contains( character ) || character is '.' or ':' ? '_' : character );
		return builder.ToString().Trim();
	}

	static string GetSafeExportPath( string root, string relativePath )
	{
		var full = Path.GetFullPath( Path.Combine( root, relativePath ) );
		var rootWithSeparator = root.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar;
		if ( !full.StartsWith( rootWithSeparator, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( "The requested export path escapes the export root." );
		return full;
	}

	static ExportSkeleton BuildExportSkeleton( Titanfall2Mdl53Reader.ParsedModel parsed )
	{
		if ( parsed.Bones.Length == 0 )
		{
			return new ExportSkeleton(
				["root"],
				[-1],
				[NumericsVector3.Zero],
				[NumericsQuaternion.Identity],
				0 );
		}

		var names = new string[parsed.Bones.Length];
		var parents = new int[parsed.Bones.Length];
		var positions = new NumericsVector3[parsed.Bones.Length];
		var rotations = new NumericsQuaternion[parsed.Bones.Length];
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var nonUnitScaleBoneCount = 0;
		for ( var boneIndex = 0; boneIndex < parsed.Bones.Length; boneIndex++ )
		{
			var requestedName = parsed.Bones[boneIndex].Name;
			if ( string.IsNullOrWhiteSpace( requestedName ) ) requestedName = $"bone_{boneIndex}";
			var name = requestedName;
			for ( var suffix = 1; !usedNames.Add( name ); suffix++ ) name = $"{requestedName}_{suffix}";
			names[boneIndex] = EscapeSmdName( name );
			parents[boneIndex] = boneIndex < parsed.LinearBones.Length
				? parsed.LinearBones[boneIndex].Parent
				: parsed.Bones[boneIndex].Bone.Parent;
			positions[boneIndex] = boneIndex < parsed.LinearBones.Length
				? parsed.LinearBones[boneIndex].Position
				: parsed.Bones[boneIndex].Bone.Position;
			rotations[boneIndex] = NumericsQuaternion.Normalize( boneIndex < parsed.LinearBones.Length
				? parsed.LinearBones[boneIndex].RotationQuaternion
				: parsed.Bones[boneIndex].Bone.RotationQuaternion );

			var scale = parsed.Bones[boneIndex].Bone.Scale;
			if ( !NearlyEqual( scale.x, 1f ) || !NearlyEqual( scale.y, 1f ) || !NearlyEqual( scale.z, 1f ) )
				nonUnitScaleBoneCount++;
		}
		return new ExportSkeleton( names, parents, positions, rotations, nonUnitScaleBoneCount );
	}

	static Dictionary<string, string> BuildExportMaterials(
		string root,
		Titanfall2MeshExtractor.ExtractedLod lod,
		bool overwrite )
	{
		var materials = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var mesh in lod.Meshes )
		{
			var sourceName = string.IsNullOrWhiteSpace( mesh.MaterialName )
				? $"missing_material_{mesh.MaterialIndex}"
				: mesh.MaterialName.Replace( '\\', '/' ).Trim().TrimStart( '/' );
			if ( materials.ContainsKey( sourceName ) ) continue;
			var parts = sourceName.Split( '/', StringSplitOptions.RemoveEmptyEntries )
				.Select( SanitizePathPart )
				.ToArray();
			var relative = Path.Combine( new[] { "materials", "titanfall2_exports" }.Concat( parts ).ToArray() );
			if ( !relative.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) ) relative += ".vmat";
			var absolute = GetSafeExportPath( root, relative );
			System.IO.Directory.CreateDirectory( Path.GetDirectoryName( absolute )! );
			if ( overwrite || !File.Exists( absolute ) )
				File.WriteAllText( absolute, BuildPlaceholderVmat( sourceName ), ExportTextEncoding );
			materials.Add( sourceName, Path.GetRelativePath( root, absolute ).Replace( '\\', '/' ) );
		}
		return materials;
	}

	static string BuildReferenceDmx(
		string modelName,
		Titanfall2MeshExtractor.ExtractedLod lod,
		ExportSkeleton skeleton,
		IReadOnlyDictionary<string, string> materials )
	{
		var meshes = lod.Meshes
			.Where( static mesh => mesh.Vertices.Length > 0 && mesh.Indices.Length >= 3 )
			.ToArray();
		var vertexOffsets = new int[meshes.Length];
		var vertexCount = 0;
		var faceSets = new DmxFaceSetData[meshes.Length];
		for ( var meshIndex = 0; meshIndex < meshes.Length; meshIndex++ )
		{
			var mesh = meshes[meshIndex];
			vertexOffsets[meshIndex] = vertexCount;
			var sourceMaterial = string.IsNullOrWhiteSpace( mesh.MaterialName )
				? $"missing_material_{mesh.MaterialIndex}"
				: mesh.MaterialName.Replace( '\\', '/' ).Trim().TrimStart( '/' );
			var material = materials.TryGetValue( sourceMaterial, out var mapped )
				? mapped
				: "materials/dev/reflectivity_30.vmat";
			faceSets[meshIndex] = new DmxFaceSetData(
				$"mesh_{meshIndex}_{SanitizeDmxName( Path.GetFileNameWithoutExtension( sourceMaterial ) )}",
				material,
				BuildDmxFaces( mesh, vertexCount ) );
			vertexCount += mesh.Vertices.Length;
		}

		var rootId = NewDmxId();
		var modelId = NewDmxId();
		var modelTransformId = NewDmxId();
		var axisId = NewDmxId();
		var meshDagId = NewDmxId();
		var meshTransformId = NewDmxId();
		var meshId = NewDmxId();
		var vertexDataId = NewDmxId();
		var boneIds = new string[skeleton.Names.Length];
		var boneTransformIds = new string[skeleton.Names.Length];
		for ( var index = 0; index < skeleton.Names.Length; index++ )
		{
			boneIds[index] = NewDmxId();
			boneTransformIds[index] = NewDmxId();
		}
		var faceSetIds = faceSets.Select( static _ => NewDmxId() ).ToArray();
		var materialIds = faceSets.Select( static _ => NewDmxId() ).ToArray();

		var text = new StringBuilder( Math.Max( 16384, vertexCount * 320 ) );
		text.AppendLine( "<!-- dmx encoding keyvalues2 4 format model 22 -->" );
		text.AppendLine( "\"DmElement\"" );
		text.AppendLine( "{" );
		AppendDmxAttribute( text, 1, "id", "elementid", rootId );
		AppendDmxAttribute( text, 1, "name", "string", "root" );
		AppendDmxAttribute( text, 1, "model", "element", modelId );
		AppendDmxAttribute( text, 1, "skeleton", "element", modelId );
		text.AppendLine( "}" );
		text.AppendLine();

		text.AppendLine( "\"DmeModel\"" );
		text.AppendLine( "{" );
		AppendDmxAttribute( text, 1, "id", "elementid", modelId );
		AppendDmxAttribute( text, 1, "name", "string", modelName );
		AppendDmxTransform( text, 1, modelTransformId, modelName, NumericsVector3.Zero, NumericsQuaternion.Identity, 1f );
		AppendDmxAttribute( text, 1, "visible", "bool", "1" );
		AppendDmxElementArrayStart( text, 1, "children" );
		var rootBones = Enumerable.Range( 0, skeleton.Names.Length )
			.Where( index => skeleton.Parents[index] < 0 || skeleton.Parents[index] >= skeleton.Names.Length )
			.Select( index => boneIds[index] )
			.Append( meshDagId )
			.ToArray();
		AppendDmxElementReferences( text, 2, rootBones );
		AppendDmxArrayEnd( text, 1 );
		AppendDmxElementArrayStart( text, 1, "jointList" );
		AppendDmxElementReferences( text, 2, boneIds );
		AppendDmxArrayEnd( text, 1 );
		AppendDmxAttribute( text, 1, "upAxis", "string", "Z" );
		AppendIndent( text, 1 ).AppendLine( "\"axisSystem\" \"DmeAxisSystem\"" );
		AppendIndent( text, 1 ).AppendLine( "{" );
		AppendDmxAttribute( text, 2, "id", "elementid", axisId );
		AppendDmxAttribute( text, 2, "upAxis", "int", "3" );
		AppendDmxAttribute( text, 2, "forwardParity", "int", "-2" );
		AppendDmxAttribute( text, 2, "coordSys", "int", "0" );
		AppendIndent( text, 1 ).AppendLine( "}" );
		text.AppendLine( "}" );
		text.AppendLine();

		for ( var boneIndex = 0; boneIndex < skeleton.Names.Length; boneIndex++ )
		{
			text.AppendLine( "\"DmeJoint\"" );
			text.AppendLine( "{" );
			AppendDmxAttribute( text, 1, "id", "elementid", boneIds[boneIndex] );
			AppendDmxAttribute( text, 1, "name", "string", skeleton.Names[boneIndex] );
			AppendDmxTransform( text, 1, boneTransformIds[boneIndex], skeleton.Names[boneIndex],
				skeleton.Positions[boneIndex], skeleton.Rotations[boneIndex], 1f );
			AppendDmxAttribute( text, 1, "visible", "bool", "1" );
			var children = Enumerable.Range( 0, skeleton.Names.Length )
				.Where( index => skeleton.Parents[index] == boneIndex )
				.Select( index => boneIds[index] )
				.ToArray();
			if ( children.Length > 0 )
			{
				AppendDmxElementArrayStart( text, 1, "children" );
				AppendDmxElementReferences( text, 2, children );
				AppendDmxArrayEnd( text, 1 );
			}
			text.AppendLine( "}" );
			text.AppendLine();
		}

		text.AppendLine( "\"DmeDag\"" );
		text.AppendLine( "{" );
		AppendDmxAttribute( text, 1, "id", "elementid", meshDagId );
		AppendDmxAttribute( text, 1, "name", "string", modelName );
		AppendDmxTransform( text, 1, meshTransformId, modelName, NumericsVector3.Zero, NumericsQuaternion.Identity, 1f );
		AppendIndent( text, 1 ).AppendLine( "\"shape\" \"DmeMesh\"" );
		AppendIndent( text, 1 ).AppendLine( "{" );
		AppendDmxAttribute( text, 2, "id", "elementid", meshId );
		AppendDmxAttribute( text, 2, "name", "string", modelName );
		AppendDmxAttribute( text, 2, "visible", "bool", "1" );
		AppendDmxAttribute( text, 2, "currentState", "element", vertexDataId );
		AppendDmxElementArrayStart( text, 2, "baseStates" );
		AppendDmxElementReferences( text, 3, [vertexDataId] );
		AppendDmxArrayEnd( text, 2 );
		AppendDmxElementArrayStart( text, 2, "deltaStates" );
		AppendDmxArrayEnd( text, 2 );
		AppendDmxElementArrayStart( text, 2, "faceSets" );
		for ( var faceSetIndex = 0; faceSetIndex < faceSets.Length; faceSetIndex++ )
		{
			var faceSet = faceSets[faceSetIndex];
			AppendIndent( text, 3 ).AppendLine( "\"DmeFaceSet\"" );
			AppendIndent( text, 3 ).AppendLine( "{" );
			AppendDmxAttribute( text, 4, "id", "elementid", faceSetIds[faceSetIndex] );
			AppendDmxAttribute( text, 4, "name", "string", faceSet.Name );
			AppendDmxIntArray( text, 4, "faces", faceSet.Faces );
			AppendIndent( text, 4 ).AppendLine( "\"material\" \"DmeMaterial\"" );
			AppendIndent( text, 4 ).AppendLine( "{" );
			AppendDmxAttribute( text, 5, "id", "elementid", materialIds[faceSetIndex] );
			AppendDmxAttribute( text, 5, "name", "string", Path.GetFileNameWithoutExtension( faceSet.MaterialPath ) );
			AppendDmxAttribute( text, 5, "mtlName", "string", faceSet.MaterialPath );
			AppendIndent( text, 4 ).AppendLine( "}" );
			AppendIndent( text, 3 ).Append( '}' );
			if ( faceSetIndex + 1 < faceSets.Length ) text.Append( ',' );
			text.AppendLine();
		}
		AppendDmxArrayEnd( text, 2 );
		AppendDmxVector2ArrayStartAndEnd( text, 2, "deltaStateWeights" );
		AppendDmxVector2ArrayStartAndEnd( text, 2, "deltaStateWeightsLagged" );
		AppendIndent( text, 1 ).AppendLine( "}" );
		AppendDmxAttribute( text, 1, "visible", "bool", "1" );
		AppendDmxElementArrayStart( text, 1, "children" );
		AppendDmxArrayEnd( text, 1 );
		text.AppendLine( "}" );
		text.AppendLine();

		text.AppendLine( "\"DmeVertexData\"" );
		text.AppendLine( "{" );
		AppendDmxAttribute( text, 1, "id", "elementid", vertexDataId );
		AppendDmxAttribute( text, 1, "name", "string", "bind" );
		AppendDmxStringArray( text, 1, "vertexFormat",
			["position$0", "normal$0", "texcoord$0", "blendweights$0", "blendindices$0"] );
		AppendDmxAttribute( text, 1, "jointCount", "int", "4" );
		AppendDmxAttribute( text, 1, "flipVCoordinates", "bool", "0" );
		AppendDmxVertexVector3Array( text, 1, "position$0", meshes, static vertex => vertex.Position );
		AppendDmxIdentityIndices( text, 1, "position$0Indices", vertexCount );
		AppendDmxVertexVector3Array( text, 1, "normal$0", meshes, static vertex => vertex.Normal );
		AppendDmxIdentityIndices( text, 1, "normal$0Indices", vertexCount );
		AppendDmxVertexVector2Array( text, 1, "texcoord$0", meshes );
		AppendDmxIdentityIndices( text, 1, "texcoord$0Indices", vertexCount );
		AppendDmxSkinArrays( text, 1, meshes, skeleton.Names.Length );
		text.AppendLine( "}" );
		return text.ToString();
	}

	static int[] BuildDmxFaces( Titanfall2MeshExtractor.MeshGeometry mesh, int vertexOffset )
	{
		var faces = new List<int>( mesh.Indices.Length + mesh.TriangleCount );
		for ( var index = 0; index + 2 < mesh.Indices.Length; index += 3 )
		{
			var a = mesh.Indices[index];
			var b = mesh.Indices[index + 1];
			var c = mesh.Indices[index + 2];
			if ( a < 0 || b < 0 || c < 0 || a >= mesh.Vertices.Length || b >= mesh.Vertices.Length || c >= mesh.Vertices.Length )
				continue;
			faces.Add( vertexOffset + a );
			faces.Add( vertexOffset + c );
			faces.Add( vertexOffset + b );
			faces.Add( -1 );
		}
		return faces.ToArray();
	}

	static void AppendDmxTransform( StringBuilder text, int indent, string id, string name,
		NumericsVector3 position, NumericsQuaternion orientation, float scale )
	{
		AppendIndent( text, indent ).AppendLine( "\"transform\" \"DmeTransform\"" );
		AppendIndent( text, indent ).AppendLine( "{" );
		AppendDmxAttribute( text, indent + 1, "id", "elementid", id );
		AppendDmxAttribute( text, indent + 1, "name", "string", name );
		AppendDmxAttribute( text, indent + 1, "position", "vector3", FormatVector3( position ) );
		AppendDmxAttribute( text, indent + 1, "orientation", "quaternion", FormatQuaternion( orientation ) );
		AppendDmxAttribute( text, indent + 1, "scale", "float", FormatFloat( scale ) );
		AppendIndent( text, indent ).AppendLine( "}" );
	}

	static void AppendDmxVertexVector3Array( StringBuilder text, int indent, string name,
		IReadOnlyList<Titanfall2MeshExtractor.MeshGeometry> meshes,
		Func<Titanfall2MeshExtractor.ExtractedVertex, Vector3> selector )
	{
		var count = meshes.Sum( static mesh => mesh.Vertices.Length );
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"vector3_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		var emitted = 0;
		foreach ( var mesh in meshes )
		foreach ( var vertex in mesh.Vertices )
		{
			var value = selector( vertex );
			AppendDmxArrayValue( text, indent + 1, $"{FormatFloat( value.x )} {FormatFloat( value.y )} {FormatFloat( value.z )}", ++emitted < count );
		}
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxVertexVector2Array( StringBuilder text, int indent, string name,
		IReadOnlyList<Titanfall2MeshExtractor.MeshGeometry> meshes )
	{
		var count = meshes.Sum( static mesh => mesh.Vertices.Length );
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"vector2_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		var emitted = 0;
		foreach ( var mesh in meshes )
		foreach ( var vertex in mesh.Vertices )
			AppendDmxArrayValue( text, indent + 1,
				$"{FormatFloat( vertex.TexCoord.x )} {FormatFloat( vertex.TexCoord.y )}", ++emitted < count );
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxSkinArrays( StringBuilder text, int indent,
		IReadOnlyList<Titanfall2MeshExtractor.MeshGeometry> meshes, int boneCount )
	{
		var vertexCount = meshes.Sum( static mesh => mesh.Vertices.Length );
		var valueCount = vertexCount * 4;
		AppendIndent( text, indent ).AppendLine( "\"blendweights$0\" \"float_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		var emitted = 0;
		Span<int> bones = stackalloc int[4];
		Span<float> weights = stackalloc float[4];
		foreach ( var mesh in meshes )
		foreach ( var vertex in mesh.Vertices )
		{
			bones.Clear();
			weights.Clear();
			GetNormalizedInfluences( vertex, boneCount, bones, weights );
			for ( var influence = 0; influence < 4; influence++ )
				AppendDmxArrayValue( text, indent + 1, FormatFloat( weights[influence] ), ++emitted < valueCount );
		}
		AppendDmxArrayEnd( text, indent );
		AppendIndent( text, indent ).AppendLine( "\"blendindices$0\" \"int_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		emitted = 0;
		foreach ( var mesh in meshes )
		foreach ( var vertex in mesh.Vertices )
		{
			bones.Clear();
			weights.Clear();
			GetNormalizedInfluences( vertex, boneCount, bones, weights );
			for ( var influence = 0; influence < 4; influence++ )
				AppendDmxArrayValue( text, indent + 1, bones[influence].ToString( CultureInfo.InvariantCulture ), ++emitted < valueCount );
		}
		AppendDmxArrayEnd( text, indent );
	}

	static void GetNormalizedInfluences( Titanfall2MeshExtractor.ExtractedVertex vertex, int boneCount,
		Span<int> bones, Span<float> weights )
	{
		var count = 0;
		var sum = 0f;
		for ( var index = 0; index < vertex.BoneIds.Length && index < vertex.BoneWeights.Length && count < 4; index++ )
		{
			var bone = vertex.BoneIds[index];
			var weight = vertex.BoneWeights[index];
			if ( bone >= boneCount || !float.IsFinite( weight ) || weight <= 0f ) continue;
			bones[count] = bone;
			weights[count] = weight;
			sum += weight;
			count++;
		}
		if ( count == 0 )
		{
			bones[0] = 0;
			weights[0] = 1f;
			return;
		}
		for ( var index = 0; index < count; index++ ) weights[index] /= sum;
	}

	static void AppendDmxIdentityIndices( StringBuilder text, int indent, string name, int count )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"int_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		for ( var index = 0; index < count; index++ )
			AppendDmxArrayValue( text, indent + 1, index.ToString( CultureInfo.InvariantCulture ), index + 1 < count );
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxIntArray( StringBuilder text, int indent, string name, IReadOnlyList<int> values )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"int_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		for ( var index = 0; index < values.Count; index++ )
			AppendDmxArrayValue( text, indent + 1, values[index].ToString( CultureInfo.InvariantCulture ), index + 1 < values.Count );
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxStringArray( StringBuilder text, int indent, string name, IReadOnlyList<string> values )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"string_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		for ( var index = 0; index < values.Count; index++ )
			AppendDmxArrayValue( text, indent + 1, values[index], index + 1 < values.Count );
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxElementArrayStart( StringBuilder text, int indent, string name )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"element_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
	}

	static void AppendDmxElementReferences( StringBuilder text, int indent, IReadOnlyList<string> ids )
	{
		for ( var index = 0; index < ids.Count; index++ )
		{
			AppendIndent( text, indent ).Append( "\"element\" \"" ).Append( ids[index] ).Append( '"' );
			if ( index + 1 < ids.Count ) text.Append( ',' );
			text.AppendLine();
		}
	}

	static void AppendDmxVector2ArrayStartAndEnd( StringBuilder text, int indent, string name )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( name ).AppendLine( "\" \"vector2_array\"" );
		AppendIndent( text, indent ).AppendLine( "[" );
		AppendDmxArrayEnd( text, indent );
	}

	static void AppendDmxArrayValue( StringBuilder text, int indent, string value, bool comma )
	{
		AppendIndent( text, indent ).Append( '\"' ).Append( EscapeDmx( value ) ).Append( '"' );
		if ( comma ) text.Append( ',' );
		text.AppendLine();
	}

	static void AppendDmxArrayEnd( StringBuilder text, int indent ) => AppendIndent( text, indent ).AppendLine( "]" );

	static void AppendDmxAttribute( StringBuilder text, int indent, string name, string type, string value )
		=> AppendIndent( text, indent ).Append( '\"' ).Append( name ).Append( "\" \"" ).Append( type )
			.Append( "\" \"" ).Append( EscapeDmx( value ) ).AppendLine( "\"" );

	static StringBuilder AppendIndent( StringBuilder text, int indent ) => text.Append( '\t', indent );
	static string NewDmxId() => Guid.NewGuid().ToString( "D", CultureInfo.InvariantCulture );
	static string SanitizeDmxName( string value ) => string.IsNullOrWhiteSpace( value ) ? "material" : value.Replace( '"', '_' );
	static string EscapeDmx( string value ) => value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ).Replace( "\r", " " ).Replace( "\n", " " );
	static string FormatFloat( float value ) => float.IsFinite( value ) ? value.ToString( "0.########", CultureInfo.InvariantCulture ) : "0";
	static string FormatVector3( NumericsVector3 value ) => $"{FormatFloat( value.X )} {FormatFloat( value.Y )} {FormatFloat( value.Z )}";
	static string FormatQuaternion( NumericsQuaternion value )
	{
		if ( !float.IsFinite( value.X ) || !float.IsFinite( value.Y ) || !float.IsFinite( value.Z ) || !float.IsFinite( value.W ) )
			value = NumericsQuaternion.Identity;
		else value = NumericsQuaternion.Normalize( value );
		return $"{FormatFloat( value.X )} {FormatFloat( value.Y )} {FormatFloat( value.Z )} {FormatFloat( value.W )}";
	}

	readonly record struct DmxFaceSetData( string Name, string MaterialPath, int[] Faces );

	static string BuildVmdl( string name, string contentRelativeDmx )
		=> $$"""
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->
{
	rootNode =
	{
		_class = "RootNode"
		children =
		[
			{
				_class = "RenderMeshList"
				children =
				[
					{
						_class = "RenderMeshFile"
						name = "{{EscapeKv3( name )}}"
						filename = "{{EscapeKv3( contentRelativeDmx )}}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 1.0
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{
							exclude_by_default = false
							exception_list = [  ]
						}
					},
				]
			},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
		importer_notes = "Exported from Titanfall 2 MDL53; material files are editable placeholders until native texture export completes."
	}
}
""";

	static string BuildPlaceholderVmat( string sourceMaterial )
		=> $$"""
// Titanfall 2 material slot: {{sourceMaterial.Replace( "\r", " " ).Replace( "\n", " " )}}
// Placeholder generated by titanfall2_export_model. Assign exported textures in Material Editor.
Layer0
{
	shader "shaders/complex.shader"
	F_SPECULAR 1
	g_flModelTintAmount "1.000"
	g_vColorTint "[1.000000 1.000000 1.000000 0.000000]"
	TextureColor "materials/default/default_color.tga"
	TextureNormal "materials/default/default_normal.tga"
	TextureRoughness "materials/default/default_rough.tga"
	g_flRoughnessScaleFactor "1.000"
	g_flMetalness "0.000"
	g_bFogEnabled "1"
}
""";

	static void WriteExportFile( string path, string contents, bool overwrite )
	{
		if ( !overwrite && File.Exists( path ) )
			throw new IOException( $"Export already exists: {path}" );
		File.WriteAllText( path, contents, ExportTextEncoding );
	}

	static string EscapeSmdName( string value ) => value.Replace( '\\', '_' ).Replace( '"', '_' );
	static string EscapeKv3( string value ) => value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
	static bool NearlyEqual( float left, float right ) => MathF.Abs( left - right ) <= 0.0001f;

	readonly record struct ExportSkeleton(
		string[] Names,
		int[] Parents,
		NumericsVector3[] Positions,
		NumericsQuaternion[] Rotations,
		int NonUnitScaleBoneCount );
}

public readonly record struct Titanfall2ModelExportResult(
	string SourcePath,
	string DmxPath,
	string VmdlPath,
	int MeshCount,
	int VertexCount,
	int TriangleCount,
	int BoneCount,
	int MaterialCount,
	int NonUnitScaleBoneCount );

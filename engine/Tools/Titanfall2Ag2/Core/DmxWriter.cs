using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Titanfall2.Formats;
using static Titanfall2.Formats.Titanfall2Mdl53AnimationReader;

namespace Titanfall2Ag2;

/// <summary>Source 2 model DMX, with quaternion animation channels and uniform scale.</summary>
public sealed class DmxWriter
{
	sealed record Element( string Type, string Id, StringBuilder Text );
	readonly List<Element> elements = [];
	Element Add( string type, string name )
	{
		var id = new Guid( MD5.HashData( Encoding.UTF8.GetBytes( $"tf2ag2/{elements.Count}/{type}/{name}" ) ) ).ToString();
		var e = new Element( type, id, new() );
		elements.Add( e );
		Attr( e, "id", "elementid", id );
		Attr( e, "name", "string", name );
		return e;
	}

	public static string Quote( string value ) => "\"" + value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ).Replace( "\r", "\\r" ).Replace( "\n", "\\n" ) + "\"";
	static string F( float value ) => float.IsFinite( value ) ? value.ToString( "G9", CultureInfo.InvariantCulture ) : throw new InvalidDataException( "Non-finite DMX value." );
	static string V( System.Numerics.Vector3 v ) => $"{F( v.X )} {F( v.Y )} {F( v.Z )}";
	static string Q( Quaternion value )
	{
		value = Quaternion.Normalize( value );
		return $"{F( value.X )} {F( value.Y )} {F( value.Z )} {F( value.W )}";
	}
	static void Attr( Element e, string key, string type, string value ) => e.Text.AppendLine( $"\t{Quote( key )} {Quote( type )} {Quote( value )}" );
	static void Ref( Element e, string key, Element target ) => Attr( e, key, "element", target.Id );
	static void Array( Element e, string key, string type, IEnumerable<string> values, bool references = false )
	{
		e.Text.AppendLine( $"\t{Quote( key )} {Quote( type + "_array" )}\n\t[" );
		e.Text.AppendLine( string.Join( ",\n", values.Select( value => "\t\t" + (references ? "\"element\" " : "") + Quote( value ) ) ) );
		e.Text.AppendLine( "\t]" );
	}
	static void Refs( Element e, string key, IEnumerable<Element> values ) => Array( e, key, "element", values.Select( v => v.Id ), true );
	Element Transform( string name, BonePose pose )
	{
		ModelData.ValidatePose( pose, name );
		var e = Add( "DmeTransform", name );
		Attr( e, "position", "vector3", V( pose.Position ) );
		Attr( e, "orientation", "quaternion", Q( pose.Rotation ) );
		Attr( e, "scale", "float", F( pose.Scale.X ) );
		return e;
	}

	public string Write( Bone[] bones, Titanfall2MeshExtractor.ExtractedLod geometry = null, string[] materials = null, AnimationData animation = null )
	{
		if ( elements.Count > 0 ) throw new InvalidOperationException( "Use a new DMX writer for each document." );
		var root = Add( "DmElement", "root" );
		var model = Add( "DmeModel", "model" );
		Ref( root, "model", model );
		Ref( root, "skeleton", model );
		Ref( model, "transform", Transform( "model", new( System.Numerics.Vector3.Zero, Quaternion.Identity, System.Numerics.Vector3.One ) ) );
		var axis = Add( "DmeAxisSystem", "axisSystem" );
		Attr( axis, "upAxis", "int", "3" );
		Attr( axis, "forwardParity", "int", "-2" );
		Attr( axis, "coordSys", "int", "0" );
		Ref( model, "axisSystem", axis );
		Attr( model, "upAxis", "string", "Z" );
		var joints = bones.Select( b => Add( "DmeJoint", b.Name ) ).ToArray();
		var transforms = bones.Select( b => Transform( b.Name, b.Reference ) ).ToArray();
		var children = joints.Where( ( _, i ) => bones[i].Parent < 0 ).ToList();
		for ( var i = 0; i < bones.Length; i++ )
		{
			Ref( joints[i], "transform", transforms[i] );
			Refs( joints[i], "children", joints.Where( ( _, j ) => bones[j].Parent == i ) );
		}
		Refs( model, "jointList", joints );
		var bind = Add( "DmeTransformList", "bind" );
		Refs( bind, "transforms", bones.Select( bone => Transform( bone.Name, bone.Reference ) ).ToArray() );
		Refs( model, "baseStates", [bind] );
		if ( geometry is not null )
		{
			for ( var i = 0; i < geometry.Meshes.Length; i++ ) children.Add( Mesh( geometry.Meshes[i], i, materials[i], bones.Length ) );
		}
		Refs( model, "children", children );
		if ( animation is not null ) WriteAnimation( root, model, bones, transforms, animation );
		var text = new StringBuilder( "<!-- dmx encoding keyvalues2 4 format model 22 -->\n" );
		foreach ( var e in elements ) text.AppendLine( $"{Quote( e.Type )}\n{{\n{e.Text}}}" );
		return text.ToString();
	}

	Element Mesh( Titanfall2MeshExtractor.MeshGeometry source, int index, string materialPath, int boneCount )
	{
		var dag = Add( "DmeDag", $"mesh_{index}" );
		Ref( dag, "transform", Transform( $"mesh_{index}", new( System.Numerics.Vector3.Zero, Quaternion.Identity, System.Numerics.Vector3.One ) ) );
		var mesh = Add( "DmeMesh", $"mesh_{index}" );
		Ref( dag, "shape", mesh );
		var data = Add( "DmeVertexData", "bind" );
		Ref( mesh, "currentState", data );
		Ref( mesh, "bindState", data );
		Refs( mesh, "baseStates", [data] );
		Array( data, "vertexFormat", "string", ["position$0", "normal$0", "texcoord$0", "blendweights$0", "blendindices$0"] );
		Attr( data, "jointCount", "int", "4" );
		Attr( data, "flipVCoordinates", "bool", "0" );
		Array( data, "position$0", "vector3", source.Vertices.Select( v => V( v.Position ) ) );
		Array( data, "normal$0", "vector3", source.Vertices.Select( v => V( v.Normal ) ) );
		Array( data, "texcoord$0", "vector2", source.Vertices.Select( v => $"{F( v.TexCoord.x )} {F( v.TexCoord.y )}" ) );
		var indices = Enumerable.Range( 0, source.Vertices.Length ).Select( i => i.ToString( CultureInfo.InvariantCulture ) ).ToArray();
		foreach ( var field in new[] { "position$0Indices", "normal$0Indices", "texcoord$0Indices" } ) Array( data, field, "int", indices );
		var weights = new List<string>();
		var blendIndices = new List<string>();
		foreach ( var vertex in source.Vertices )
		{
			var ids = vertex.BoneIds.ToArray();
			var values = vertex.BoneWeights.ToArray();
			var influences = ids.Select( ( id, i ) => (Id: (int)id, Weight: values[i]) )
				.Where( pair => pair.Id < boneCount && pair.Weight > 0 && float.IsFinite( pair.Weight ) ).Take( 4 ).ToArray();
			if ( influences.Length == 0 ) influences = [(0, 1f)];
			var sum = influences.Sum( p => p.Weight );
			for ( var i = 0; i < 4; i++ )
			{
				blendIndices.Add( (i < influences.Length ? influences[i].Id : 0).ToString( CultureInfo.InvariantCulture ) );
				weights.Add( F( i < influences.Length ? influences[i].Weight / sum : 0 ) );
			}
		}
		Array( data, "blendweights$0", "float", weights );
		Array( data, "blendindices$0", "int", blendIndices );
		if ( source.Indices.Length % 3 != 0 || source.Indices.Any( i => i < 0 || i >= source.Vertices.Length ) )
			throw new InvalidDataException( $"Invalid triangle indices in mesh {index}." );
		var faces = Add( "DmeFaceSet", $"mesh_{index}" );
		var faceIndices = Enumerable.Range( 0, source.Indices.Length / 3 ).SelectMany( i => new[] { source.Indices[i * 3], source.Indices[i * 3 + 2], source.Indices[i * 3 + 1], -1 } );
		Array( faces, "faces", "int", faceIndices.Select( i => i.ToString( CultureInfo.InvariantCulture ) ) );
		var material = Add( "DmeMaterial", materialPath );
		Attr( material, "mtlName", "string", materialPath );
		Ref( faces, "material", material );
		Refs( mesh, "faceSets", [faces] );
		return dag;
	}

	void WriteAnimation( Element root, Element model, Bone[] bones, Element[] transforms, AnimationData animation )
	{
		var list = Add( "DmeAnimationList", "animations" );
		Ref( root, "animationList", list );
		var clip = Add( "DmeChannelsClip", animation.Name );
		Refs( list, "animations", [clip] );
		Ref( clip, "skeleton", model );
		Attr( clip, "frameRate", "float", F( animation.FrameRate ) );
		var timeFrame = Add( "DmeTimeFrame", "timeFrame" );
		Attr( timeFrame, "start", "time", "0" );
		Attr( timeFrame, "duration", "time", F( (animation.Frames.Length - 1) / animation.FrameRate ) );
		Attr( timeFrame, "offset", "time", "0" );
		Attr( timeFrame, "scale", "float", "1" );
		Ref( clip, "timeFrame", timeFrame );
		var channels = new List<Element>();
		for ( var bone = 0; bone < bones.Length; bone++ )
		{
			var values = animation.Frames.Select( frame => frame[bone] ).ToArray();
			Channel( "position", "Vector3", "vector3", values.Select( p => V( p.Position ) ) );
			Channel( "orientation", "Quaternion", "quaternion", values.Select( p => Q( p.Rotation ) ) );
			Channel( "scale", "Float", "float", values.Select( p => F( p.Scale.X ) ) );
			void Channel( string attribute, string classType, string dataType, IEnumerable<string> samples )
			{
				var channel = Add( "DmeChannel", $"{bones[bone].Name}_{attribute}" );
				Ref( channel, "toElement", transforms[bone] );
				Attr( channel, "toAttribute", "string", attribute );
				Attr( channel, "mode", "int", "3" );
				var log = Add( $"Dme{classType}Log", "log" );
				var layer = Add( $"Dme{classType}LogLayer", "layer" );
				Array( layer, "times", "time", Enumerable.Range( 0, animation.Frames.Length ).Select( i => F( i / animation.FrameRate ) ) );
				Array( layer, "values", dataType, samples );
				Refs( log, "layers", [layer] );
				Ref( channel, "log", log );
				channels.Add( channel );
			}
		}
		Refs( clip, "channels", channels );
	}
}

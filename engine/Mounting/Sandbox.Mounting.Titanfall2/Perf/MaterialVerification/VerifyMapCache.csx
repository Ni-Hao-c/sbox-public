// Homestead's LightingOrigin metadata contains NaN values. Check the actual
// persisted map against a fresh source parse, including the binary geometry.
var host = Sandbox.Mounting.Directory.Get( "titanfall2" );
var path = "mount://titanfall2/maps/mp_homestead.bsp.scene";
var loader = host.GetByPath( path );
var instanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var staticFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var source = loader.GetType().GetField( "_source", instanceFlags ).GetValue( loader );
var type = host.GetType().Assembly.GetType( "Titanfall2MapCache" );
var identity = type.GetMethod( "Identity", staticFlags ).Invoke( null, new object[] { host, source } );
var read = new object[] { identity, null };
if ( !(bool)type.GetMethod( "TryRead", staticFlags ).Invoke( null, read ) )
	return new { passed = false, reason = "Map cache did not hit. Load Homestead with the default BSP mode first." };
var cached = (Titanfall2.Titanfall2BspReader.ParsedBsp)read[1];
var bytes = new object[] { null, null };
if ( !(bool)source.GetType().GetMethod( "TryReadAllBytes" ).Invoke( source, bytes ) ) throw new Exception( bytes[1]?.ToString() );
if ( !Titanfall2.Titanfall2BspReader.TryRead( (byte[])bytes[0], "mp_homestead", null, false, false, false, out var original, out var error ) )
	throw new Exception( error );
var options = (System.Text.Json.JsonSerializerOptions)host.GetType().Assembly.GetType( "Titanfall2ProcessedCache" )
	.GetField( "JsonOptions", staticFlags ).GetValue( null );
string Json<T>( T value ) => System.Text.Json.JsonSerializer.Serialize( value, options );
bool props = Json( original.StaticProps ) == Json( cached.StaticProps );
bool visibility = Json( original.Visibility ) == Json( cached.Visibility );
bool geometry = original.World.Meshes.Count == cached.World.Meshes.Count;
long vertices = 0, indices = 0;
if ( geometry )
{
	for ( int j = 0; j < original.World.Meshes.Count; j++ )
	{
		var a = original.World.Meshes[j]; var b = cached.World.Meshes[j];
		geometry &= a.MaterialName == b.MaterialName && a.LightmapPage == b.LightmapPage
			&& a.Cubemap == b.Cubemap && a.VisibilityObjectIndex == b.VisibilityObjectIndex
			&& System.Runtime.InteropServices.MemoryMarshal.AsBytes( a.Vertices.AsSpan() )
				.SequenceEqual( System.Runtime.InteropServices.MemoryMarshal.AsBytes( b.Vertices.AsSpan() ) )
			&& a.Indices.AsSpan().SequenceEqual( b.Indices.AsSpan() );
		vertices += a.Vertices.Length; indices += a.Indices.Length;
	}
}
return new { passed = props && visibility && geometry, props, visibility, geometry,
	meshes = original.World.Meshes.Count, vertices, indices, numberHandling = options.NumberHandling.ToString() };

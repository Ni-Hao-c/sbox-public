var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed ) => rows.Add( new { name, passed } );
var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var encode = typeof( Titanfall2StaticPropStreamer ).GetMethod( "Encode", flags );
var decode = typeof( Titanfall2StaticPropStreamer ).GetMethod( "Decode", flags );
object[] Decode( string value ) => ((System.Collections.IEnumerable)decode.Invoke( null, new object[] { value } )).Cast<object>().ToArray();
var probe = new Titanfall2.Titanfall2LightProbe
{
	Red = new System.Numerics.Vector4( -.25f, .125f, .0625f, .5f ),
	Green = new System.Numerics.Vector4( .125f, -.25f, .0625f, .25f ),
	Blue = new System.Numerics.Vector4( .0625f, .125f, -.25f, .75f ),
	SkyDirectionSunVisibility = new System.Numerics.Vector4( -.5f, .25f, .125f, .375f ),
	PackedLightWeights = 0xffffffff, LightIndex0 = 12, LightIndex1 = 1157, LightIndex2 = ushort.MaxValue, LightIndex3 = ushort.MaxValue
};
var source = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = 0, ModelPath = "models/probe.mdl", BakedLightProbe = probe },
	new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = 1, ModelPath = "models/probe.mdl" } };
var encoded = (string)encode.Invoke( null, new object[] { source, new Titanfall2.Titanfall2BspReader.BspVisibility() } );
var restored = Decode( encoded );
var property = restored[0].GetType().GetProperty( "Probe" );
var first = (Titanfall2ProbeData)property.GetValue( restored[0] );
var second = (Titanfall2ProbeData)property.GetValue( restored[1] );
var expected = Titanfall2ProbeData.FromSource( probe );
Check( "gpu-record-is-80-bytes", System.Runtime.InteropServices.Marshal.SizeOf<Titanfall2ProbeData>() == 80 );
Check( "streamer-keeps-direction-and-sky", first.Red == expected.Red && first.Green == expected.Green
	&& first.Blue == expected.Blue && first.SkyDirectionSunVisibility == expected.SkyDirectionSunVisibility && first.Metadata.x == 1 );
Check( "missing-probe-remains-native-fallback", second.Metadata.x == 0 );
Check( "local-weights-and-full-source-ids-survive-nan-bit-patterns", BitConverter.SingleToUInt32Bits( first.Metadata.y ) == 0xffffffff
	&& BitConverter.SingleToUInt32Bits( first.Metadata.z ) == (12u | 1157u << 16) && BitConverter.SingleToUInt32Bits( first.Metadata.w ) == 0xffffffff );
using ( var stream = new System.IO.MemoryStream() )
{
	using ( var writer = new System.IO.BinaryWriter( stream, System.Text.Encoding.UTF8, true ) )
	{
		writer.Write( 0x32505354 ); writer.Write( 3 ); writer.Write( 1 ); writer.Write( "models/probe.mdl" ); writer.Write( 1 );
		writer.Write( 0 ); for ( var i = 0; i < 6; ++i ) writer.Write( 0f ); writer.Write( 1f ); writer.Write( false );
		for ( var i = 0; i < 3; ++i ) writer.Write( 1f ); writer.Write( 0UL ); writer.Write( 0UL );
	}
	var legacy = Decode( Convert.ToBase64String( stream.ToArray() ) );
	Check( "version-three-payload-still-loads", legacy.Length == 1 && ((Titanfall2ProbeData)property.GetValue( legacy[0] )).Metadata.x == 0 );
	var old = stream.ToArray().ToList();
	old[4] = 4; old.Add( 1 );
	foreach ( var v in new[] { expected.Red, expected.Green, expected.Blue, expected.SkyDirectionSunVisibility } )
		foreach ( var x in new[] { v.x, v.y, v.z, v.w } ) old.AddRange( BitConverter.GetBytes( x ) );
	var v4 = (Titanfall2ProbeData)property.GetValue( Decode( Convert.ToBase64String( old.ToArray() ) )[0] );
	Check( "version-four-restores-sh-with-no-invented-lamps", v4.Metadata == new Vector4( 1, 0, 0, 0 ) && v4.Red == expected.Red );
}
var truncated = Convert.FromBase64String( encoded ); Array.Resize( ref truncated, truncated.Length - 1 );
try { Decode( Convert.ToBase64String( truncated ) ); Check( "truncated-payload-rejected", false ); }
catch ( System.IO.EndOfStreamException ) { Check( "truncated-payload-rejected", true ); }
var culler = new Sandbox.Mounting.PerfKit.MountedInstanceCull();
var transforms = new[] { new Transform( new Vector3( 10, 0, 0 ) ), new Transform( new Vector3( 10, 0, 0 ) ), new Transform( new Vector3( 100, 0, 0 ) ) };
culler.Update( transforms, Vector3.Zero, 5, new float[] { 0, 10, 0 } );
Check( "subset-retains-original-index", culler.SubsetIndices.SequenceEqual( new[] { 1 } ) );
var changed = culler.Update( transforms, Vector3.Zero, 5, new float[] { 10, 0, 0 } );
Check( "duplicate-transform-membership-is-not-aliased", changed && culler.SubsetIndices.SequenceEqual( new[] { 0 } ) );
culler.Update( transforms, Vector3.Zero, 1000 );
Check( "full-subset-restores-identity-indices", ReferenceEquals( culler.Subset, transforms ) && culler.SubsetIndices is null );
culler.Reset();
Check( "reset-clears-subset-identity", !culler.IsInitialized && culler.SubsetIndices.Length == 0 );
var output = System.IO.Path.Combine( exchangeDirectory, "probe-transport-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };

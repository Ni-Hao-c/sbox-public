// Run with sbox-cli run_csharp. The optional source audit reads previously
// exported retail LIGHTPROBES / STATIC_PROP_LIGHTPROBE_INDICES lump files.
var rows = new System.Collections.Generic.List<object>();
void Check( string name, bool passed, object details = null ) => rows.Add( new { name, passed, details } );
byte[] Record( short[] coefficients )
{
	var bytes = new byte[Titanfall2.Titanfall2LightProbe.RecordBytes];
	for ( var i = 0; i < coefficients.Length; ++i )
		System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian( bytes.AsSpan( i * 2, 2 ), coefficients[i] );
	return bytes;
}
var neutral = Record( new short[] { 0, 0, 0, 8192, 0, 0, 0, 8192, 0, 0, 0, 8192, 0, 0, 0, 0 } );
Titanfall2.Titanfall2LightProbe.TryRead( neutral, out var neutralProbe );
Check( "retail-fallback-is-unit-irradiance", neutralProbe.Evaluate( System.Numerics.Vector3.UnitZ ) == System.Numerics.Vector3.One );
var directional = Record( new short[] { 8192, 0, 0, 4096, 0, -4096, 0, 2048, 0, 0, 2048, 1024, -8192, 4096, -2048, 8192 } );
Titanfall2.Titanfall2LightProbe.TryRead( directional, out var probe );
Check( "signed-q13-positive-axis", probe.Evaluate( System.Numerics.Vector3.UnitX ) == new System.Numerics.Vector3( 1.5f, 0.25f, 0.125f ) );
Check( "signed-q13-negative-clamp", probe.Evaluate( -System.Numerics.Vector3.UnitX ) == new System.Numerics.Vector3( 0, 0.25f, 0.125f ) );
Check( "separate-sky-and-sun-visibility", probe.SkyDirectionSunVisibility == new System.Numerics.Vector4( -1, 0.5f, -0.25f, 1 ) );
Check( "truncated-record-rejected", !Titanfall2.Titanfall2LightProbe.TryRead( directional.AsSpan( 0, 47 ), out var truncated ) && truncated is null );
var packed = (byte[])directional.Clone();
new byte[] { 10, 20, 30, 40, 0x34, 0x12, 0xff, 0xff, 0x78, 0x56, 0xbc, 0x9a, 1, 2, 3, 4 }.CopyTo( packed, 32 );
Titanfall2.Titanfall2LightProbe.TryRead( packed, out var packedProbe );
Check( "local-light-data-preserved", packedProbe.PackedLightWeights == 0x281e140a && packedProbe.LightIndex0 == 0x1234
	&& packedProbe.LightIndex1 == 0xffff && packedProbe.LightIndex2 == 0x5678 && packedProbe.LightIndex3 == 0x9abc && packedProbe.Reserved == 0x04030201 );
var apply = typeof( Titanfall2.Titanfall2BspReader ).GetMethod( "ApplyStaticPropLightProbes",
	System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic );
var records = neutral.Concat( directional ).ToArray();
byte[] Indices( params uint[] values )
{
	var bytes = new byte[values.Length * 4];
	for ( var i = 0; i < values.Length; ++i )
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian( bytes.AsSpan( i * 4, 4 ), values[i] );
	return bytes;
}
var filtered = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = 2 } };
apply.Invoke( null, new object[] { filtered, records, Indices( 0, 0, 1 ) } );
Check( "filtered-props-use-original-source-index", filtered[0].BakedLightProbe?.Red == probe.Red );
Check( "probe-does-not-tint-albedo-or-emission", filtered[0].LightProbeColor == System.Numerics.Vector3.One );
var fallback = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance() };
apply.Invoke( null, new object[] { fallback, records, Indices( 1 ) } );
Check( "programmatic-prop-index-fallback", fallback[0].BakedLightProbe?.Red == probe.Red );
var invalid = new[] { new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = 0 },
	new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = 10 } };
apply.Invoke( null, new object[] { invalid, records, Indices( uint.MaxValue ) } );
Check( "invalid-associations-retain-neutral-fallback", invalid.All( p => p.BakedLightProbe is null && p.LightProbeColor == System.Numerics.Vector3.One ) );
apply.Invoke( null, new object[] { invalid, records, new byte[] { 1, 2, 3 } } );
Check( "truncated-index-table-is-safe", invalid.All( p => p.BakedLightProbe is null ) );
var root = @"C:\Users\Administrator\.codex\visualizations\2026\10\24\01a16c2c-16d3-7f51-b93e-2cde2bd4796a\tf2-lighting-calibration";
foreach ( var map in new[] { "mp_forwardbase_kodai", "mp_angel_city", "mp_homestead" } )
{
	var probePath = System.IO.Path.Combine( root, map + "-lump-101.bin" );
	var indexPath = System.IO.Path.Combine( root, map + "-lump-102.bin" );
	if ( !System.IO.File.Exists( probePath ) || !System.IO.File.Exists( indexPath ) ) continue;
	var bytes = System.IO.File.ReadAllBytes( probePath );
	var indices = System.IO.File.ReadAllBytes( indexPath );
	var props = Enumerable.Range( 0, indices.Length / 4 ).Select( i => new Titanfall2.Titanfall2BspReader.StaticPropInstance { SourceIndex = i } ).ToArray();
	apply.Invoke( null, new object[] { props, bytes, indices } );
	var decoded = props.Count( p => p.BakedLightProbe is not null );
	Check( map + "-retail-prop-associations", decoded == props.Length && props.All( p => p.LightProbeColor == System.Numerics.Vector3.One ),
		new { probes = bytes.Length / 48, props = props.Length, decoded } );
}
var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
var emptyJson = System.Text.Json.JsonSerializer.Serialize( new Titanfall2.Titanfall2BspReader.StaticPropInstance(), options );
Check( "default-cache-omits-null-probe", !emptyJson.Contains( "BakedLightProbe" ) );
var propJson = System.Text.Json.JsonSerializer.Serialize( filtered[0], options );
var restored = System.Text.Json.JsonSerializer.Deserialize<Titanfall2.Titanfall2BspReader.StaticPropInstance>( propJson, options );
Check( "probe-json-roundtrip-keeps-direction", restored?.BakedLightProbe?.Red == probe.Red
	&& restored?.BakedLightProbe?.SkyDirectionSunVisibility == probe.SkyDirectionSunVisibility );
var output = System.IO.Path.Combine( exchangeDirectory, "light-probe-data-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };

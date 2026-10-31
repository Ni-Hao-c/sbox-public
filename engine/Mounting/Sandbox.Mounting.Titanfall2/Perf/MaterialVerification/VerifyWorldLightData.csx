

var rows = new System.Collections.Generic.List<object>();
var pages = Enumerable.Repeat( (short)-1, 126 ).ToArray();
pages[0] = 0; pages[1] = 1; pages[63 + 2] = 1;
var lights = new[] {
	new Titanfall2.Titanfall2WorldLight { Type = 1, Flags = 4 }, // baked-only: excluded from RTL indices
	new Titanfall2.Titanfall2WorldLight { Type = 2, Flags = 8 },
	new Titanfall2.Titanfall2WorldLight { Type = 3, Flags = 24 } };
uint Pack( int a = 63, int b = 63, int c = 63, int d = 63, int page = 0 ) => (uint)(a | b << 6 | c << 12 | d << 18 | page << 24);
byte[] Masks( params uint[] ids ) => Titanfall2.Titanfall2WorldLight.CreateSunChannels( ids.SelectMany( BitConverter.GetBytes ).ToArray(), pages, lights );
foreach ( var channel in Enumerable.Range( 0, 4 ) )
{
	var ids = new[] { 63, 63, 63, 63 }; ids[channel] = 1;
	var actual = Masks( Pack( ids[0], ids[1], ids[2], ids[3] ) );
	rows.Add( new { name = "sun-channel-" + channel, passed = actual.SequenceEqual( new byte[] { (byte)(1 << channel) } ) } );
}
rows.Add( new { name = "skip-local-first-sun-and-page", passed = Masks( Pack( 0, 1 ), Pack( 2, page: 1 ), Pack(), Pack( 1, 1 ) ).SequenceEqual( new byte[] { 2, 1, 0, 1 } ) } );
rows.Add( new { name = "invalid-page", passed = Masks( Pack( 0, page: 2 ) ).Length == 0 } );
rows.Add( new { name = "invalid-light", passed = Masks( Pack( 3 ) ).Length == 0 } );
rows.Add( new { name = "unused-page-255", passed = Masks( uint.MaxValue ).SequenceEqual( new byte[] { 0 } ) } );
foreach ( var version in new[] { 1, 2, 3 } )
{
	var bytes = new byte[version == 1 ? 100 : version == 2 ? 108 : 112];
	void Float( int offset, float value ) => BitConverter.GetBytes( value ).CopyTo( bytes, offset );
	Float( 0, 123 ); Float( 12, 8 ); Float( 16, 6.81f ); Float( 20, 5.27f ); Float( 24, -.3345f );
	Float( 60, .9f ); Float( 64, .5f ); Float( 68, 1 ); Float( 72, 256 ); Float( 76, 1 ); Float( 80, -.01f ); Float( 84, .0001f );
	if ( version >= 2 ) { Float( 100, 10 ); Float( 104, .25f ); }
	if ( version >= 3 ) Float( 108, .2f );
	BitConverter.GetBytes( 3 ).CopyTo( bytes, 52 ); BitConverter.GetBytes( 24u ).CopyTo( bytes, 88 );
	var actual = Titanfall2.Titanfall2WorldLight.Read( bytes, version );
	rows.Add( new { name = "world-light-v" + version, passed = actual.Count == 1 && actual[0].Origin.X == 123
		&& actual[0].Intensity.X == 8 && actual[0].Intensity.Y == 6.81f && actual[0].Direction.X == -.3345f
		&& actual[0].Type == 3 && actual[0].IsRealTime && actual[0].InnerConeCosine == .9f && actual[0].OuterConeCosine == .5f
		&& actual[0].Radius == 256 && actual[0].LinearAttenuation == -.01f && actual[0].QuadraticAttenuation == .0001f
		&& actual[0].EmitterRadius == (version >= 2 ? 10 : 0) && actual[0].SpecularIntensity == (version >= 3 ? .2f : 1) } );
}
rows.Add( new { name = "invalid-stride-version", passed = Titanfall2.Titanfall2WorldLight.Read( new byte[111], 3 ).Count == 0
	&& Titanfall2.Titanfall2WorldLight.Read( new byte[112], 99 ).Count == 0 } );
var directory = @"C:\Users\Administrator\.codex\visualizations\2026\10\24\01a16c2c-16d3-7f51-b93e-2cde2bd4796a\tf2-lighting-calibration";
foreach ( var map in new[] { "mp_forwardbase_kodai", "mp_angel_city", "mp_homestead" } )
{
	var compiled = Titanfall2.Titanfall2WorldLight.Read( System.IO.File.ReadAllBytes( System.IO.Path.Combine( directory, map + "-lump-54.bin" ) ), 3 );
	var pageBytes = System.IO.File.ReadAllBytes( System.IO.Path.Combine( directory, map + "-lump-122.bin" ) );
	var lightPages = Enumerable.Range( 0, pageBytes.Length / 2 ).Select( i => BitConverter.ToInt16( pageBytes, i * 2 ) ).ToArray();
	var header = System.IO.File.ReadAllBytes( System.IO.Path.Combine( directory, map + "-lump-83.bin" ) );
	var rtl = System.IO.File.ReadAllBytes( System.IO.Path.Combine( directory, map + "-lump-105.bin" ) );
	var offset = 0; var maskPixels = 0; var valid = true;
	for ( var h = 0; h < header.Length; h += 8 )
	{
		var width = BitConverter.ToUInt16( header, h + 4 ); var height = BitConverter.ToUInt16( header, h + 6 );
		var size = width * height * 4;
		var channels = Titanfall2.Titanfall2WorldLight.CreateSunChannels( rtl.AsSpan( offset, size ), lightPages, compiled );
		valid &= channels.Length == width * height;
		maskPixels += channels.Count( b => b != 0 ); offset += size * 2 + width * height;
	}
	rows.Add( new { name = "retail-rtl-" + map, lights = compiled.Count, runtimeLights = compiled.Count( l => l.IsRealTime ), maskPixels,
		passed = valid && offset == rtl.Length && maskPixels > 10000 } );
}
var output = System.IO.Path.Combine( exchangeDirectory, "world-light-data-results.json" );
System.IO.File.WriteAllText( output, System.Text.Json.JsonSerializer.Serialize( rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true } ) );
return new { output, rows };

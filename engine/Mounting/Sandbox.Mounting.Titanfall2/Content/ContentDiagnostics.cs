using System.Text.Json;

/// <summary>Uses the mount's existing console registration; no separate host hooks.</summary>
public static class Titanfall2ContentDiagnostics
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2Content" );
	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	static Titanfall2Mount Mount => Sandbox.Mounting.Directory.Get( "titanfall2" ) as Titanfall2Mount;

	[ConCmd( "titanfall2_content_stats", Help = "Report the immutable TF2 content revision, counts and Northstar diagnostics." )]
	public static void Stats() => Log.Info( JsonSerializer.Serialize( Mount?.GetContentStatistics(), JsonOptions ) );

	[ConCmd( "titanfall2_content_list", Help = "List virtual files and logical assets: prefix, offset, count (max 1000)." )]
	public static void List( string prefix = "", int offset = 0, int count = 100 ) =>
		Log.Info( JsonSerializer.Serialize( Mount?.GetContentFiles( prefix, offset, count ), JsonOptions ) );

	[ConCmd( "titanfall2_content_explain", Help = "Explain a virtual path's winner and overridden candidates. Kind defaults to file; RPAK uses txtr/matl/shds/shdr." )]
	public static void Explain( string path, string kind = "file" ) =>
		Log.Info( JsonSerializer.Serialize( Mount?.ExplainContent( path, kind ), JsonOptions ) );
}

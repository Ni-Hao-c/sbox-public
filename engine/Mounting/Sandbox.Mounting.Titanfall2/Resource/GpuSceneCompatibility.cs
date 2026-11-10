using System.Reflection;
using Sandbox.Rendering;

/// <summary>
/// The only managed engine compatibility boundary of the mount renderer. Stock CommandList
/// can record indexed MDI but does not expose in-place playback. Bind that existing managed
/// method and its managed action recorder once, never reflect per draw and never access
/// native contexts/interop from the mount. Actions let pre-recorded profiling scopes wrap
/// compute with the slot's private attributes, without leaking them into Graphics.Attributes.
/// An incompatible engine keeps the reference renderer.
/// </summary>
static class Titanfall2GpuSceneCompatibility
{
	static readonly Action<CommandList> Playback = Bind();
	static readonly Action<CommandList, Action> RecordAction = BindRecordAction();
	public static bool Available => Playback is not null && RecordAction is not null;
	static Action<CommandList, Action> BindRecordAction()
	{
		try
		{
			var method = typeof( CommandList ).GetMethod( "AddAction", BindingFlags.Instance | BindingFlags.NonPublic,
				null, [typeof( Action )], null );
			return method?.ReturnType == typeof( void ) ? method.CreateDelegate<Action<CommandList, Action>>() : null;
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Titanfall 2 GPU command recording unavailable; retaining CPU renderer: {exception.Message}" );
			return null;
		}
	}
	// Used only while building immutable lists, never to create delegates during rendering.
	public static void Record( CommandList commands, Action action ) => RecordAction( commands, action );
	static Action<CommandList> Bind()
	{
		try
		{
			var method = typeof( CommandList ).GetMethod( "ExecuteOnRenderThread", BindingFlags.Instance | BindingFlags.NonPublic,
				null, Type.EmptyTypes, null );
			return method?.ReturnType == typeof( void ) ? method.CreateDelegate<Action<CommandList>>() : null;
		}
		catch ( Exception exception )
		{
			Titanfall2Log.Warning( $"Titanfall 2 GPU scene playback unavailable; retaining CPU renderer: {exception.Message}" );
			return null;
		}
	}
	public static void Execute( CommandList commands ) => Playback( commands );
}


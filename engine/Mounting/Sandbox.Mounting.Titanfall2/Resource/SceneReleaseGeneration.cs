using System;
using System.Threading;

/// <summary>Invalidates delayed map work without letting an older continuation own a new generation.</summary>
sealed class Titanfall2SceneReleaseGeneration
{
	CancellationTokenSource _cancellation = new();
	long _generation;

	internal readonly record struct Ticket( long Generation, CancellationToken Cancellation );
	internal Ticket Capture() => new( _generation, _cancellation.Token );
	internal bool IsCurrent( Ticket ticket ) => ticket.Generation == _generation && !ticket.Cancellation.IsCancellationRequested;

	// Called on the engine thread, as are Capture and IsCurrent. Tasks only observe the token.
	internal void Advance()
	{
		var previous = _cancellation;
		_cancellation = new();
		_generation++;
		previous.Cancel();
		previous.Dispose();
	}
}

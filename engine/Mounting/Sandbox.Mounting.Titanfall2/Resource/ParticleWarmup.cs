using System.Diagnostics;

/// <summary>Bounded entity pre-roll. Pauses pending systems so loading frames cannot advance them twice.</summary>
internal sealed class Titanfall2ParticleWarmup
{
	readonly (ParticleEffect Effect, float Remaining)[] _systems;
	readonly bool _pause;
	readonly Stopwatch _timer = new();
	internal bool Complete { get; private set; }

	internal Titanfall2ParticleWarmup( IEnumerable<(ParticleEffect Effect, float AlreadyWarmed)> systems, float seconds, bool pause )
	{
		_systems = systems.Select( system => (system.Effect, MathF.Max( 0f, seconds - system.AlreadyWarmed )) ).ToArray();
		_pause = pause;
		Complete = _systems.All( system => system.Remaining <= 0f );
		foreach ( var system in _systems )
			if ( system.Effect.IsValid() ) system.Effect.Paused = !Complete || pause;
	}

	internal bool Advance( int maximumSteps, float maximumMilliseconds )
	{
		if ( Complete ) return true;
		_timer.Restart();
		var steps = 0;
		for ( var i = 0; i < _systems.Length; i++ )
		{
			ref var system = ref _systems[i];
			while ( system.Remaining > 0f && system.Effect.IsValid() )
			{
				if ( steps++ >= maximumSteps || _timer.Elapsed.TotalMilliseconds >= maximumMilliseconds ) return false;
				var step = MathF.Min( system.Remaining, 1f / 30f );
				system.Effect.Paused = false;
				try { system.Effect.Step( step ); }
				finally { system.Effect.Paused = true; }
				system.Remaining -= step;
			}
		}
		Complete = true;
		foreach ( var system in _systems )
			if ( system.Effect.IsValid() ) system.Effect.Paused = _pause;
		return true;
	}
}

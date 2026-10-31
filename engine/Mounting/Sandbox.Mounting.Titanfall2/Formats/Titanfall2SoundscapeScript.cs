using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Titanfall2;

/// <summary>
/// Parses Respawn's soundscape script (<c>scripts/audio/audio_soundscapes.txt</c>).
/// Each named block can declare <c>"playlooping" "&lt;sound&gt;"</c>; the <c>play_event</c>
/// key of a <c>trigger_soundscape</c> entity references the block name.
/// Only the looping sound mapping is needed for map ambience.
/// </summary>
public static class Titanfall2SoundscapeScript
{
	static readonly Regex BlockPattern = new( @"""(?<name>[^""]+)""\s*\{(?<body>.*?)\}",
		RegexOptions.Singleline | RegexOptions.Compiled );
	static readonly Regex LoopPattern = new( @"""playlooping""\s*""(?<sound>[^""]*)""",
		RegexOptions.IgnoreCase | RegexOptions.Compiled );

	public static Dictionary<string, string> Parse( string text )
	{
		var loops = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		if ( string.IsNullOrEmpty( text ) ) return loops;

		foreach ( Match block in BlockPattern.Matches( text ) )
		{
			var loop = LoopPattern.Match( block.Groups["body"].Value );
			if ( !loop.Success ) continue;

			var sound = loop.Groups["sound"].Value.Trim();
			if ( sound.Length == 0 ) continue;

			loops[block.Groups["name"].Value.Trim()] = sound;
		}

		return loops;
	}
}

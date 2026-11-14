using System.Numerics;
using Titanfall2.Formats;
using static Titanfall2.Formats.Titanfall2Mdl53AnimationReader;

namespace Titanfall2Ag2;

public sealed record Bone( string Name, int Parent, BonePose Reference );
public sealed record AnimationData( string Name, float FrameRate, bool Looping, BonePose[][] Frames );
public sealed record ModelInput( SourceFile File, Titanfall2Mdl53Reader.ParsedModel Model );

public static class ModelData
{
	public static List<ModelInput> ReadDependencies( ModelSource source, string modelPath )
	{
		var result = new List<ModelInput>();
		var visited = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		void Read( string path )
		{
			path = ModelSource.Normalize( path );
			if ( !visited.Add( path ) ) return;
			if ( visited.Count > 64 ) throw new InvalidDataException( "More than 64 included animation models." );
			var file = source.Read( path );
			var parsed = Titanfall2Mdl53Reader.Parse( file.Bytes );
			result.Add( new( file, parsed ) );
			foreach ( var include in parsed.IncludedModels ) Read( include.Name );
		}
		Read( modelPath );
		return result;
	}

	public static Bone[] Skeleton( Titanfall2Mdl53Reader.ParsedModel model )
	{
		if ( model.Bones.Length == 0 ) return [new( "root", -1, new( Vector3.Zero, Quaternion.Identity, Vector3.One ) )];
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var bones = new Bone[model.Bones.Length];
		for ( var i = 0; i < bones.Length; i++ )
		{
			var bone = model.Bones[i];
			var name = string.IsNullOrWhiteSpace( bone.Name ) ? $"bone_{i}" : bone.Name;
			var unique = name;
			for ( var suffix = 1; !names.Add( unique ); suffix++ ) unique = $"{name}_{suffix}";
			var linear = i < model.LinearBones.Length ? model.LinearBones[i] : null;
			var parent = linear?.Parent ?? bone.Bone.Parent;
			if ( parent < -1 || parent >= i ) throw new InvalidDataException( $"Bone '{name}' has invalid or unordered parent {parent}." );
			var pose = new BonePose( linear?.Position ?? bone.Bone.Position, linear?.RotationQuaternion ?? bone.Bone.RotationQuaternion, bone.Bone.Scale );
			ValidatePose( pose, name );
			bones[i] = new( unique, parent, pose with { Rotation = Quaternion.Normalize( pose.Rotation ) } );
		}
		return bones;
	}

	public static IEnumerable<AnimationData> Animations( IReadOnlyList<ModelInput> inputs, Bone[] target, string[] selected, List<string> warnings )
	{
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var found = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var input in inputs )
		{
			var map = input.Model.Bones.Select( bone => Array.FindIndex( target, b => b.Name.Equals( bone.Name, StringComparison.OrdinalIgnoreCase ) ) ).ToArray();
			if ( !map.Any( index => index >= 0 ) ) continue;
			var missingBones = target.Where( bone => !input.Model.Bones.Any( sourceBone => sourceBone.Name.Equals( bone.Name, StringComparison.OrdinalIgnoreCase ) ) ).ToArray();
			var warnedMissing = false;
			var entries = input.Model.Sequences.Length > 0
				? input.Model.Sequences.SelectMany( sequence => sequence.AnimationIndices.Distinct().Where( index => index >= 0 && index < input.Model.Animations.Length )
					.Select( ( index, blend ) => (Index: (int)index, Name: sequence.Label.TrimStart( '@' ), Blend: blend, Flags: sequence.Sequence.Flags) ) )
				: input.Model.Animations.Select( animation => (Index: animation.Index, Name: animation.Name.TrimStart( '@' ), Blend: 0, Flags: animation.Animation.Flags) );
			foreach ( var entry in entries )
			{
				if ( selected.Length > 0 && !selected.Contains( entry.Name, StringComparer.OrdinalIgnoreCase ) ) continue;
				found.Add( entry.Name );
				var name = entry.Blend == 0 ? entry.Name : $"{entry.Name}__blend_{entry.Blend}";
				if ( !names.Add( name ) ) continue;
				var decoded = Decode( input.File.Bytes, input.Model, entry.Index, name, entry.Flags );
				if ( decoded.Delta )
				{
					// Delta conversion needs verified subtraction/composition semantics; don't label it as an ordinary pose.
					if ( selected.Length > 0 ) throw new NotSupportedException( $"'{name}' is additive. Additive conversion is not supported yet." );
					warnings.Add( $"Skipped additive animation: {name}" );
					continue;
				}
				if ( !warnedMissing && missingBones.Length > 0 )
				{
					warnings.Add( $"{input.File.Path}: {missingBones.Length} target bones have no matching source track and retain their reference pose." );
					warnedMissing = true;
				}
				var frames = decoded.Frames.Select( frame =>
				{
					var output = target.Select( bone => bone.Reference ).ToArray();
					for ( var i = 0; i < frame.Length; i++ )
					{
						if ( map[i] < 0 ) continue;
						ValidatePose( frame[i], $"{name}/{target[map[i]].Name}" );
						output[map[i]] = frame[i];
					}
					return output;
				} ).ToArray();
				yield return new( name, decoded.FramesPerSecond, decoded.Looping, frames );
			}
		}
		var missing = selected.Except( found, StringComparer.OrdinalIgnoreCase ).ToArray();
		if ( missing.Length > 0 ) throw new ArgumentException( $"Animation sequences not found: {string.Join( ", ", missing )}" );
	}

	public static void ValidatePose( BonePose pose, string name )
	{
		static bool Finite( Vector3 value ) => float.IsFinite( value.X ) && float.IsFinite( value.Y ) && float.IsFinite( value.Z );
		if ( !Finite( pose.Position ) || !Finite( pose.Scale ) || !float.IsFinite( pose.Rotation.LengthSquared() ) || pose.Rotation.LengthSquared() < 1e-12f )
			throw new InvalidDataException( $"Non-finite or invalid transform: {name}" );
		if ( pose.Scale.X <= 0 || MathF.Abs( pose.Scale.X - pose.Scale.Y ) > 0.0001f || MathF.Abs( pose.Scale.X - pose.Scale.Z ) > 0.0001f )
			throw new NotSupportedException( $"Nonuniform or nonpositive bone scale at '{name}': {pose.Scale}. DMX export requires uniform positive scale." );
	}
}

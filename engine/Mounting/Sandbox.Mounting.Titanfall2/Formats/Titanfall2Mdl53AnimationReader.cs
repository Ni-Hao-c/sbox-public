using System.Buffers.Binary;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;

namespace Titanfall2.Formats;

/// <summary>Decodes Titanfall 2 MDL53 RLE animation tracks into local bone poses.</summary>
public static class Titanfall2Mdl53AnimationReader
{
	const int StudioLooping = 0x0001;
	const int StudioDelta = 0x0004;
	const int StudioAllZeros = 0x0020;
	const int StudioScale = 0x20000;
	const int StudioFrameMovement = 0x40000;

	const byte AnimDelta = 0x01;
	const byte AnimRawPosition = 0x02;
	const byte AnimRawRotation = 0x04;
	const byte AnimRawScale = 0x08;
	const byte AnimNoRotation = 0x10;

	public readonly record struct BonePose( NumericsVector3 Position, NumericsQuaternion Rotation, NumericsVector3 Scale );

	public sealed class DecodedAnimation
	{
		public string Name { get; init; }
		public float FramesPerSecond { get; init; }
		public bool Looping { get; init; }
		public bool Delta { get; init; }
		public BonePose[][] Frames { get; init; } = Array.Empty<BonePose[]>();
	}

	public static DecodedAnimation Decode(
		ReadOnlySpan<byte> data,
		Titanfall2Mdl53Reader.ParsedModel model,
		int animationIndex,
		string name = null,
		int sequenceFlags = 0 )
	{
		if ( model is null ) throw new ArgumentNullException( nameof( model ) );
		if ( animationIndex < 0 || animationIndex >= model.Animations.Length )
			throw new ArgumentOutOfRangeException( nameof( animationIndex ) );

		var info = model.Animations[animationIndex];
		var description = info.Animation;
		var frameCount = Math.Max( description.FrameCount, 1 );
		var boneCount = model.Bones.Length;
		var delta = (description.Flags & StudioDelta) != 0;
		var defaults = CreateDefaultPose( model, delta );
		var frames = new BonePose[frameCount][];
		var sequenceHasScale = (sequenceFlags & StudioScale) != 0;

		for ( var frame = 0; frame < frameCount; ++frame )
		{
			var poses = new BonePose[boneCount];
			Array.Copy( defaults, poses, boneCount );

			if ( boneCount > 0 && (description.Flags & StudioAllZeros) == 0
				&& TryResolveAnimationBlock( data, info.Offset, description, frame, out var animationOffset, out var localFrame ) )
			{
				DecodeBoneRecords( data, model, animationOffset, localFrame, sequenceHasScale, poses );
			}

			if ( poses.Length > 0 )
				poses[0] = ApplyRootMovement( data, info.Offset, description, frame, poses[0] );
			frames[frame] = poses;
		}

		var animationName = string.IsNullOrWhiteSpace( name ) ? info.Name : name;
		if ( animationName?.StartsWith( '@' ) == true ) animationName = animationName[1..];
		if ( string.IsNullOrWhiteSpace( animationName ) ) animationName = $"animation_{animationIndex}";

		return new DecodedAnimation
		{
			Name = animationName,
			FramesPerSecond = description.FramesPerSecond > 0 && float.IsFinite( description.FramesPerSecond )
				? description.FramesPerSecond
				: 30.0f,
			Looping = ((sequenceFlags | description.Flags) & StudioLooping) != 0,
			Delta = delta,
			Frames = frames
		};
	}

	static BonePose[] CreateDefaultPose( Titanfall2Mdl53Reader.ParsedModel model, bool delta )
	{
		var poses = new BonePose[model.Bones.Length];
		for ( var boneIndex = 0; boneIndex < poses.Length; ++boneIndex )
		{
			if ( delta )
			{
				poses[boneIndex] = new BonePose( NumericsVector3.Zero, NumericsQuaternion.Identity, NumericsVector3.One );
				continue;
			}

			var bone = model.Bones[boneIndex].Bone;
			var position = bone.Position;
			var rotation = bone.RotationQuaternion;
			if ( boneIndex < model.LinearBones.Length )
			{
				position = model.LinearBones[boneIndex].Position;
				rotation = model.LinearBones[boneIndex].RotationQuaternion;
			}
			poses[boneIndex] = new BonePose( ToNumerics( position ), Normalize( rotation ), SanitizeScale( ToNumerics( bone.Scale ) ) );
		}
		return poses;
	}

	static bool TryResolveAnimationBlock(
		ReadOnlySpan<byte> data,
		int descriptionOffset,
		Mdl53AnimationDescription description,
		int frame,
		out int animationOffset,
		out int localFrame )
	{
		localFrame = frame;
		var relativeOffset = description.AnimationOffset;
		if ( description.SectionFrameCount > 0 )
		{
			int section;
			if ( description.FrameCount > description.SectionFrameCount && frame == description.FrameCount - 1 )
			{
				localFrame = 0;
				section = (description.FrameCount - 1) / description.SectionFrameCount + 1;
			}
			else
			{
				section = frame / description.SectionFrameCount;
				localFrame -= section * description.SectionFrameCount;
			}

			var sectionOffset = checked(descriptionOffset + description.SectionOffset + section * sizeof(int));
			EnsureRange( data, sectionOffset, sizeof(int) );
			relativeOffset = BinaryPrimitives.ReadInt32LittleEndian( data.Slice( sectionOffset, sizeof(int) ) );
		}

		if ( relativeOffset <= 0 )
		{
			animationOffset = 0;
			return false;
		}

		animationOffset = checked(descriptionOffset + relativeOffset);
		EnsureRange( data, animationOffset, 32 );
		return true;
	}

	static void DecodeBoneRecords(
		ReadOnlySpan<byte> data,
		Titanfall2Mdl53Reader.ParsedModel model,
		int firstRecordOffset,
		int frame,
		bool sequenceHasScale,
		BonePose[] poses )
	{
		var recordOffset = firstRecordOffset;
		for ( var recordIndex = 0; recordIndex < 4096; ++recordIndex )
		{
			EnsureRange( data, recordOffset, 32 );
			var positionScale = ReadSingle( data, recordOffset );
			var boneIndex = data[recordOffset + 4];
			var flags = data[recordOffset + 5];

			if ( boneIndex < poses.Length )
			{
				var basePose = poses[boneIndex];
				var position = DecodePosition( data, model, recordOffset, frame, boneIndex, flags, positionScale );
				var rotation = DecodeRotation( data, model, recordOffset, frame, boneIndex, flags );
				var hasScaleTrack = sequenceHasScale || (flags & AnimRawScale) != 0 || HasTrackOffsets( data, recordOffset + 22 );
				var scale = hasScaleTrack
					? DecodeScale( data, model, recordOffset, frame, boneIndex, flags )
					: basePose.Scale;
				poses[boneIndex] = new BonePose( position, rotation, scale );
			}

			var nextOffset = ReadInt32( data, recordOffset + 28 );
			if ( nextOffset == 0 ) return;
			if ( nextOffset < 0 ) throw new InvalidDataException( "MDL animation record points backwards." );
			recordOffset = checked(recordOffset + nextOffset);
		}

		throw new InvalidDataException( "MDL animation record chain exceeded the safety limit." );
	}

	static NumericsVector3 DecodePosition(
		ReadOnlySpan<byte> data,
		Titanfall2Mdl53Reader.ParsedModel model,
		int recordOffset,
		int frame,
		int boneIndex,
		byte flags,
		float positionScale )
	{
		if ( (flags & AnimRawPosition) != 0 ) return ReadVector48( data, recordOffset + 16 );

		var position = new NumericsVector3(
			ExtractAnimationValue( data, recordOffset + 16, 0, frame, positionScale ),
			ExtractAnimationValue( data, recordOffset + 16, 1, frame, positionScale ),
			ExtractAnimationValue( data, recordOffset + 16, 2, frame, positionScale ) );
		if ( (flags & AnimDelta) == 0 ) position += GetBasePosition( model, boneIndex );
		return position;
	}

	static NumericsQuaternion DecodeRotation(
		ReadOnlySpan<byte> data,
		Titanfall2Mdl53Reader.ParsedModel model,
		int recordOffset,
		int frame,
		int boneIndex,
		byte flags )
	{
		if ( (flags & AnimRawRotation) != 0 ) return ReadQuaternion64( data, recordOffset + 8 );
		if ( (flags & AnimNoRotation) != 0 )
			return (flags & AnimDelta) != 0 ? NumericsQuaternion.Identity : GetBaseQuaternion( model, boneIndex );

		var rotationScale = GetRotationScale( model, boneIndex );
		var angles = new NumericsVector3(
			ExtractAnimationValue( data, recordOffset + 8, 0, frame, rotationScale.X ),
			ExtractAnimationValue( data, recordOffset + 8, 1, frame, rotationScale.Y ),
			ExtractAnimationValue( data, recordOffset + 8, 2, frame, rotationScale.Z ) );
		if ( (flags & AnimDelta) == 0 ) angles += GetBaseEuler( model, boneIndex );

		var rotation = AngleQuaternion( angles );
		// R2's studio code aligns unified/linear-bone quaternions to the sign of
		// the bind-pose quaternion.  A quaternion and its negation represent the
		// same rotation, but keeping the sign continuous is required by the
		// animation interpolator; otherwise a frame can take the long path and
		// make a skinned hierarchy appear to explode.  Raw and no-rotation tracks
		// return early in the original implementation and must stay untouched.
		if ( (flags & AnimDelta) == 0 && NeedsQuaternionAlignment( model, boneIndex ) )
			rotation = AlignQuaternion( GetAlignmentQuaternion( model, boneIndex ), rotation );

		return rotation;
	}

	static NumericsVector3 DecodeScale(
		ReadOnlySpan<byte> data,
		Titanfall2Mdl53Reader.ParsedModel model,
		int recordOffset,
		int frame,
		int boneIndex,
		byte flags )
	{
		if ( (flags & AnimRawScale) != 0 ) return SanitizeScale( ReadVector48( data, recordOffset + 22 ) );
		var bone = model.Bones[boneIndex].Bone;
		var scale = new NumericsVector3(
			ExtractAnimationValue( data, recordOffset + 22, 0, frame, bone.ScaleScale.x ),
			ExtractAnimationValue( data, recordOffset + 22, 1, frame, bone.ScaleScale.y ),
			ExtractAnimationValue( data, recordOffset + 22, 2, frame, bone.ScaleScale.z ) );
		if ( (flags & AnimDelta) == 0 ) scale += ToNumerics( bone.Scale );
		return SanitizeScale( scale );
	}

	static BonePose ApplyRootMovement(
		ReadOnlySpan<byte> data,
		int descriptionOffset,
		Mdl53AnimationDescription description,
		int frame,
		BonePose pose )
	{
		var movement = NumericsVector3.Zero;
		var yawDegrees = 0.0f;
		if ( description.FrameMovementOffset > 0 && (description.Flags & StudioFrameMovement) != 0 )
		{
			var offset = checked(descriptionOffset + description.FrameMovementOffset);
			EnsureRange( data, offset, 24 );
			movement = new NumericsVector3(
				ExtractAnimationValue( data, offset + 16, 0, frame, ReadSingle( data, offset ) ),
				ExtractAnimationValue( data, offset + 16, 1, frame, ReadSingle( data, offset + 4 ) ),
				ExtractAnimationValue( data, offset + 16, 2, frame, ReadSingle( data, offset + 8 ) ) );
			yawDegrees = ExtractAnimationValue( data, offset + 16, 3, frame, ReadSingle( data, offset + 12 ), 4 );
		}
		else if ( description.MovementCount > 0 && description.MovementOffset > 0 )
		{
			DecodePiecewiseMovement( data, descriptionOffset, description, frame, out movement, out yawDegrees );
		}

		if ( movement == NumericsVector3.Zero && yawDegrees == 0.0f ) return pose;
		var yaw = NumericsQuaternion.CreateFromAxisAngle( NumericsVector3.UnitZ, yawDegrees * (MathF.PI / 180.0f) );
		return new BonePose( pose.Position + movement, Normalize( yaw * pose.Rotation ), pose.Scale );
	}

	static void DecodePiecewiseMovement(
		ReadOnlySpan<byte> data,
		int descriptionOffset,
		Mdl53AnimationDescription description,
		int frame,
		out NumericsVector3 position,
		out float yaw )
	{
		position = NumericsVector3.Zero;
		yaw = 0.0f;
		var previousFrame = 0.0f;
		for ( var movementIndex = 0; movementIndex < description.MovementCount; ++movementIndex )
		{
			var offset = checked(descriptionOffset + description.MovementOffset + movementIndex * System.Runtime.CompilerServices.Unsafe.SizeOf<Mdl53Movement>());
			EnsureRange( data, offset, System.Runtime.CompilerServices.Unsafe.SizeOf<Mdl53Movement>() );
			var movement = Titanfall2Mdl53Binary.ReadStruct<Mdl53Movement>( data, offset );
			if ( movement.EndFrame >= frame )
			{
				var denominator = movement.EndFrame - previousFrame;
				var fraction = denominator > 0.0f ? (frame - previousFrame) / denominator : 0.0f;
				var distance = movement.StartVelocity * fraction
					+ 0.5f * (movement.EndVelocity - movement.StartVelocity) * fraction * fraction;
				position += ToNumerics( movement.MovementVector ) * distance;
				yaw *= 1.0f - fraction;
				yaw += movement.EndYaw * fraction;
				return;
			}

			previousFrame = movement.EndFrame;
			position = ToNumerics( movement.Position );
			yaw = movement.EndYaw;
		}
	}

	static float ExtractAnimationValue(
		ReadOnlySpan<byte> data,
		int pointerBase,
		int axis,
		int frame,
		float scale,
		int axisCount = 3 )
	{
		if ( axis < 0 || axis >= axisCount ) return 0.0f;
		EnsureRange( data, pointerBase, axisCount * sizeof(short) );
		var relativeOffset = ReadInt16( data, pointerBase + axis * sizeof(short) );
		if ( relativeOffset <= 0 || scale == 0.0f ) return 0.0f;

		var valueOffset = checked(pointerBase + relativeOffset);
		var remainingFrame = Math.Max( frame, 0 );
		for ( var block = 0; block < 65536; ++block )
		{
			EnsureRange( data, valueOffset, 2 );
			var valid = data[valueOffset];
			var total = data[valueOffset + 1];
			if ( total == 0 || valid == 0 ) return 0.0f;
			if ( remainingFrame < total )
			{
				var valueIndex = remainingFrame < valid ? remainingFrame + 1 : valid;
				var sampleOffset = checked(valueOffset + valueIndex * sizeof(short));
				EnsureRange( data, sampleOffset, sizeof(short) );
				return ReadInt16( data, sampleOffset ) * scale;
			}

			remainingFrame -= total;
			valueOffset = checked(valueOffset + (valid + 1) * sizeof(short));
		}

		throw new InvalidDataException( "MDL animation value stream exceeded the safety limit." );
	}

	static bool HasTrackOffsets( ReadOnlySpan<byte> data, int pointerBase )
	{
		EnsureRange( data, pointerBase, 6 );
		return ReadInt16( data, pointerBase ) > 0
			|| ReadInt16( data, pointerBase + 2 ) > 0
			|| ReadInt16( data, pointerBase + 4 ) > 0;
	}

	static NumericsVector3 GetBasePosition( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> ToNumerics( boneIndex < model.LinearBones.Length ? model.LinearBones[boneIndex].Position : model.Bones[boneIndex].Bone.Position );

	static NumericsQuaternion GetBaseQuaternion( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> Normalize( boneIndex < model.LinearBones.Length
			? model.LinearBones[boneIndex].RotationQuaternion
			: model.Bones[boneIndex].Bone.RotationQuaternion );

	static NumericsVector3 GetBaseEuler( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> ToNumerics( boneIndex < model.LinearBones.Length ? model.LinearBones[boneIndex].RotationEuler : model.Bones[boneIndex].Bone.RotationEuler );

	static NumericsVector3 GetRotationScale( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> ToNumerics( boneIndex < model.LinearBones.Length ? model.LinearBones[boneIndex].RotationScale : model.Bones[boneIndex].Bone.RotationScale );

	static bool NeedsQuaternionAlignment( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> boneIndex >= 0 && boneIndex < model.LinearBones.Length
			? (model.LinearBones[boneIndex].Flags & 0x100000) != 0
			: boneIndex >= 0 && boneIndex < model.Bones.Length
				&& (model.Bones[boneIndex].Bone.Flags & 0x100000) != 0;

	static NumericsQuaternion GetAlignmentQuaternion( Titanfall2Mdl53Reader.ParsedModel model, int boneIndex )
		=> Normalize( boneIndex >= 0 && boneIndex < model.LinearBones.Length
			? model.LinearBones[boneIndex].AlignmentQuaternion
			: model.Bones[boneIndex].Bone.AlignmentQuaternion );

	static NumericsQuaternion AlignQuaternion( NumericsQuaternion alignment, NumericsQuaternion rotation )
	{
		// Equivalent to Source's QuaternionAlign: choose the hemisphere closest
		// to the alignment quaternion.  Do not multiply the quaternions.
		var dot = alignment.X * rotation.X + alignment.Y * rotation.Y
			+ alignment.Z * rotation.Z + alignment.W * rotation.W;
		return dot < 0.0f ? new NumericsQuaternion( -rotation.X, -rotation.Y, -rotation.Z, -rotation.W ) : rotation;
	}

	static NumericsVector3 ToNumerics( global::Vector3 value ) => new( value.x, value.y, value.z );

	static NumericsQuaternion AngleQuaternion( NumericsVector3 angles )
	{
		var sx = MathF.Sin( angles.X * 0.5f );
		var cx = MathF.Cos( angles.X * 0.5f );
		var sy = MathF.Sin( angles.Y * 0.5f );
		var cy = MathF.Cos( angles.Y * 0.5f );
		var sz = MathF.Sin( angles.Z * 0.5f );
		var cz = MathF.Cos( angles.Z * 0.5f );
		return Normalize( new NumericsQuaternion(
			sx * cy * cz - cx * sy * sz,
			cx * sy * cz + sx * cy * sz,
			cx * cy * sz - sx * sy * cz,
			cx * cy * cz + sx * sy * sz ) );
	}

	static NumericsQuaternion ReadQuaternion64( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(ulong) );
		var packed = BinaryPrimitives.ReadUInt64LittleEndian( data.Slice( offset, sizeof(ulong) ) );
		const ulong mask = (1UL << 21) - 1;
		const float inverse = 1.0f / 1048576.5f;
		var x = ((int)(packed & mask) - 1048576) * inverse;
		var y = ((int)((packed >> 21) & mask) - 1048576) * inverse;
		var z = ((int)((packed >> 42) & mask) - 1048576) * inverse;
		var w = MathF.Sqrt( MathF.Max( 0.0f, 1.0f - x * x - y * y - z * z ) );
		if ( (packed & (1UL << 63)) != 0 ) w = -w;
		return Normalize( new NumericsQuaternion( x, y, z, w ) );
	}

	static NumericsVector3 ReadVector48( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, 6 );
		return new NumericsVector3(
			(float)BitConverter.UInt16BitsToHalf( ReadUInt16( data, offset ) ),
			(float)BitConverter.UInt16BitsToHalf( ReadUInt16( data, offset + 2 ) ),
			(float)BitConverter.UInt16BitsToHalf( ReadUInt16( data, offset + 4 ) ) );
	}

	static NumericsVector3 SanitizeScale( NumericsVector3 scale )
	{
		if ( !float.IsFinite( scale.X ) || !float.IsFinite( scale.Y ) || !float.IsFinite( scale.Z ) )
			return NumericsVector3.One;
		return scale;
	}

	static NumericsQuaternion Normalize( NumericsQuaternion value )
	{
		var lengthSquared = value.LengthSquared();
		return lengthSquared > 1e-12f && float.IsFinite( lengthSquared )
			? NumericsQuaternion.Normalize( value )
			: NumericsQuaternion.Identity;
	}

	static short ReadInt16( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(short) );
		return BinaryPrimitives.ReadInt16LittleEndian( data.Slice( offset, sizeof(short) ) );
	}

	static ushort ReadUInt16( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(ushort) );
		return BinaryPrimitives.ReadUInt16LittleEndian( data.Slice( offset, sizeof(ushort) ) );
	}

	static int ReadInt32( ReadOnlySpan<byte> data, int offset )
	{
		EnsureRange( data, offset, sizeof(int) );
		return BinaryPrimitives.ReadInt32LittleEndian( data.Slice( offset, sizeof(int) ) );
	}

	static float ReadSingle( ReadOnlySpan<byte> data, int offset )
		=> BitConverter.Int32BitsToSingle( ReadInt32( data, offset ) );

	static void EnsureRange( ReadOnlySpan<byte> data, int offset, int length )
	{
		if ( offset < 0 || length < 0 || offset > data.Length - length )
			throw new InvalidDataException( $"MDL animation range is outside the file. Offset={offset}, Length={length}, File={data.Length}" );
	}
}

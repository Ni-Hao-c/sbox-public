/// <summary>Import provenance stored on the model, including exported/reloaded models.</summary>
[Sandbox.ModelEditor.GameData( "titanfall2_collision" )]
public sealed class Titanfall2ModelCollisionData
{
	public string SourcePath { get; set; }
	public string GeometrySource { get; set; }
	public int HullCount { get; set; }
	// Raw MDL contents. This is not a converted Source 2 or CM query mask.
	public uint ModelContents { get; set; }
}

/// <summary>
/// Original instance identity on a streamed collider. Diagnostic only: these
/// values do not change physics filtering or invent a contents-mask conversion.
/// </summary>
[Library, Hide]
public sealed class Titanfall2CollisionSource : Component
{
	/// <summary>Retail static-prop initialization (engine.dll 0x1801affc4..0x1801b0007).
	/// Returns original contents, not Source 2 collision flags or a gameplay query mask.</summary>
	public static uint StaticPropContents( uint modelContents, uint flagsRemove )
	{
		var contents = modelContents;
		if ( contents == 0 || (contents & 1) != 0 ) contents = (contents & ~1u) | 0x00EB0280u;
		if ( (contents & 2) != 0 ) contents = (contents & 0xFFF7FFFDu) | 0x00E30240u;
		if ( (contents & 8) != 0 ) contents = (contents & 0xFFB7FFF7u) | 0x00A30240u;
		return contents & ~flagsRemove;
	}

	[Property, Hide] public string MountIdent { get; set; }
	[Property, Hide] public string MapPath { get; set; }
	[Property, Hide] public string ModelPath { get; set; }
	[Property, Hide] public string GeometrySource { get; set; }
	[Property, Hide] public int SourceIndex { get; set; } = -1;
	[Property, Hide] public byte SolidType { get; set; }
	[Property, Hide] public uint ReservedCollisionData { get; set; }
	[Property, Hide] public uint CollisionFlagsRemove { get; set; }
	[Property, Hide] public uint EffectiveContents { get; set; }
	[Property, Hide] public bool HasEffectiveContents { get; set; }
	// Legacy v3-v5 scene payloads cannot supply original instance fields.
	[Property, Hide] public bool HasInstanceMetadata { get; set; }

	/// <summary>Describes the collider actually hit by an ordinary scene trace.</summary>
	public static object DescribeHit( SceneTraceResult hit )
	{
		var source = hit.Collider?.GameObject.Components.Get<Titanfall2CollisionSource>();
		var model = (hit.Collider as ModelCollider)?.Model;
		var data = model is { IsValid: true, IsError: false } ? model.GetData<Titanfall2ModelCollisionData>() : null;
		return new
		{
			hit.Hit,
			hit.StartedSolid,
			hit.EndPosition,
			hit.Normal,
			hit.Triangle,
			ObjectName = hit.GameObject?.Name,
			ColliderType = hit.Collider?.GetType().Name,
			ShapeType = hit.Shape?.GetType().Name,
			ModelResource = model?.ResourcePath,
			ModelBounds = model?.Bounds,
			WorldScale = hit.Collider?.GameObject.WorldScale,
			Source = source is null ? null : new
			{
				source.MountIdent,
				source.MapPath,
				source.ModelPath,
				source.SourceIndex,
				source.HasInstanceMetadata,
				source.SolidType,
				source.ReservedCollisionData,
				source.CollisionFlagsRemove,
				source.HasEffectiveContents,
				source.EffectiveContents,
				GeometrySource = data?.GeometrySource ?? source.GeometrySource ?? "unknown"
			},
			ModelData = data
		};
	}
}

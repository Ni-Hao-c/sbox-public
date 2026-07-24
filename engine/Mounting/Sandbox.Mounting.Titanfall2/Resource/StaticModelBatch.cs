/// <summary>
/// Renders one opaque Titanfall 2 static model at multiple permanent transforms.
/// The SceneCustomObject provides one native culling bound per BSP cell/model
/// group, while Graphics.DrawModelInstanced submits the whole visible group in
/// one instanced draw per model material.
/// </summary>
sealed class Titanfall2StaticModelBatch : SceneCustomObject
{
	readonly Model _model;
	readonly Transform[] _transforms;

	public int InstanceCount => _transforms.Length;

	public bool CastShadows
	{
		get => Flags.CastShadows;
		set => Flags.CastShadows = value;
	}

	public Titanfall2StaticModelBatch(
		SceneWorld world,
		Model model,
		IReadOnlyList<Transform> transforms,
		bool castShadows )
		: base( world )
	{
		_model = model;
		_transforms = transforms?.ToArray() ?? Array.Empty<Transform>();
		if ( !_model.IsValid() )
			throw new ArgumentException( "A valid model is required.", nameof( model ) );
		if ( _transforms.Length == 0 )
			throw new ArgumentException( "At least one instance transform is required.", nameof( transforms ) );

		var modelBounds = _model.Bounds;
		var worldBounds = modelBounds.Transform( _transforms[0] );
		for ( var index = 1; index < _transforms.Length; ++index )
			worldBounds = worldBounds.AddBBox( modelBounds.Transform( _transforms[index] ) );

		Bounds = worldBounds;
		Batchable = false;
		Flags.IsOpaque = true;
		Flags.IsTranslucent = false;
		Flags.IsStatic = true;
		Flags.CastShadows = castShadows;
		Flags.WantsPrePass = true;
		Flags.NeedsLightProbe = true;
		Flags.NeedsEnvironmentMap = true;
		Flags.IncludeInCubemap = true;
	}

	public override void RenderSceneObject()
	{
		if ( !_model.IsValid() || _transforms.Length == 0 ) return;
		Graphics.DrawModelInstanced( _model, _transforms );
	}
}

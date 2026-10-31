/// <summary>
/// Refractive water needs a framebuffer copy in the translucent pass. Native
/// opaque world aggregates cannot provide that binding. ModelRenderer retains
/// the normal transform, material, visibility and teardown lifecycle here.
/// </summary>
[Library, Hide]
public sealed class Titanfall2WaterRenderer : ModelRenderer
{
	protected override SceneObject CreateSceneObject( Model model )
	{
		var result = base.CreateSceneObject( model );
		result.Flags.IsOpaque = false;
		result.Flags.IsTranslucent = true;
		result.Flags.WantsFrameBufferCopy = true;
		return result;
	}
}

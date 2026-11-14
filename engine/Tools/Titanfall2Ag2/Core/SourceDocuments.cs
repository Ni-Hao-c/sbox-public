using System.Globalization;

namespace Titanfall2Ag2;

public static class SourceDocuments
{
	const string Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->";
	public static string Skeleton( string dmx ) => $$"""
		{{Header}}
		{
			_class = "CSkeletonDocument"
			m_sourceFilename = {{DmxWriter.Quote( dmx )}}
			m_flGlobalScale = 1.0
			m_rootBoneName = ""
		}
		""";

	public static string Clip( string dmx, string skeleton, float frameRate ) => $$"""
		{{Header}}
		{
			_class = "CClipDocument"
			m_sourceFilename = {{DmxWriter.Quote( dmx )}}
			m_animationSkeletonName = {{DmxWriter.Quote( skeleton )}}
			m_nTake = 0
			m_nStartFrame = -1
			m_nEndFrame = -1
			m_flImportScale = 1.0
			m_flImportFrameRate = {{frameRate.ToString( "G9", CultureInfo.InvariantCulture )}}
			m_additiveType = "None"
			m_bExtractMotion = false
		}
		""";

	public static string Model( string dmx, string skeleton ) => $$"""
		<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->
		{
			rootNode =
			{
				_class = "RootNode"
				children =
				[
					{
						_class = "RenderMeshList"
						children =
						[
							{
								_class = "RenderMeshFile"
								name = "model"
								filename = {{DmxWriter.Quote( dmx )}}
								import_scale = 1.0
								import_translation = [ 0.0, 0.0, 0.0 ]
								import_rotation = [ 0.0, 0.0, 0.0 ]
								import_filter = { exclude_by_default = false exception_list = [] }
							}
						]
					}
				]
				skeleton_name = {{DmxWriter.Quote( skeleton )}}
				anim_graph_name = ""
				model_archetype = ""
			}
		}
		""";

	// Portable editable slot placeholders. Original RPAK material/texture conversion is a separate pipeline.
	public static string Material => """
		Layer0
		{
			shader "shaders/complex.shader"
			TextureColor "[0.65 0.65 0.65 1]"
			TextureNormal "[0.5 0.5 1 1]"
			TextureRoughness "[0.7 0.7 0.7 1]"
			TextureAmbientOcclusion "[1 1 1 1]"
			TextureMetalness "[0 0 0 1]"
		}
		""";
}

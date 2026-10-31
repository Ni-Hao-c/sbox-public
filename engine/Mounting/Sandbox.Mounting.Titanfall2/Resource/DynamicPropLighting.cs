using Titanfall2;

/// <summary>
/// Retail lighting for map entities that are not static props (prop_dynamic): the light probe
/// nearest the entity's lighting origin, as the static-prop path does with its baked index.
/// Without it the mount shaders fall back to sun-only lighting (black indoors), and an unset
/// T2ProbeMode inherits whatever an instanced prop batch last left in the shared render
/// attributes, so the model flickers between another batch's probes and none.
/// </summary>
[Library]
public sealed class Titanfall2DynamicPropLighting : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer, Sandbox.Internal.IPreRenderSubscriber
{
	[Property, Hide] public bool HasProbe { get; set; }
	[Property, Hide] public Vector4 Red { get; set; }
	[Property, Hide] public Vector4 Green { get; set; }
	[Property, Hide] public Vector4 Blue { get; set; }
	[Property, Hide] public Vector4 SkyDirectionSunVisibility { get; set; }
	[Property, Hide] public uint LightWeights { get; set; }
	[Property, Hide] public uint LightIds01 { get; set; }
	[Property, Hide] public uint LightIds23 { get; set; }
	[Property, Hide] public Color SkyColor { get; set; }

	Titanfall2LocalLightController _lights;

	internal void Configure( Titanfall2LightProbe probe, Color skyColor )
	{
		var data = Titanfall2ProbeData.FromSource( probe );
		HasProbe = data.Metadata.x > 0;
		Red = data.Red;
		Green = data.Green;
		Blue = data.Blue;
		SkyDirectionSunVisibility = data.SkyDirectionSunVisibility;
		LightWeights = BitConverter.SingleToUInt32Bits( data.Metadata.y );
		LightIds01 = BitConverter.SingleToUInt32Bits( data.Metadata.z );
		LightIds23 = BitConverter.SingleToUInt32Bits( data.Metadata.w );
		SkyColor = skyColor;
	}

	protected override void OnPreRender()
	{
		var renderer = Components.Get<ModelRenderer>();
		var target = renderer.IsValid() ? renderer.SceneObject : null;
		if ( !target.IsValid() ) return;
		var data = new Titanfall2ProbeData
		{
			Red = Red,
			Green = Green,
			Blue = Blue,
			SkyDirectionSunVisibility = SkyDirectionSunVisibility,
			Metadata = new Vector4( HasProbe ? 1 : 0, BitConverter.UInt32BitsToSingle( LightWeights ),
				BitConverter.UInt32BitsToSingle( LightIds01 ), BitConverter.UInt32BitsToSingle( LightIds23 ) )
		};
		// Mode 0 is still written explicitly so a missing probe cannot inherit batch state.
		data.Apply( target, SkyColor );
		if ( !HasProbe ) target.Attributes.Set( "T2LocalLightCount", 0 );
		else
		{
			if ( !_lights.IsValid() ) _lights = Scene.GetAllComponents<Titanfall2LocalLightController>().FirstOrDefault();
			if ( _lights.IsValid() && _lights.Active ) _lights.ApplyTo( target.Attributes );
			else target.Attributes.Set( "T2LocalLightCount", 0 );
		}
	}
}

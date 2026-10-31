using System.Diagnostics;
using Sandbox.Mounting;

/// <summary>
/// Applies real RPAK materials to a mounted BSP over multiple frames. The world
/// model starts with neutral placeholder materials so scene loading is not held
/// hostage by synchronous STARPAK reads and GPU texture creation.
/// </summary>
/// <remarks>
/// Not currently installed: nothing adds this component to a scene, so
/// <c>Titanfall2StreamingSettings.WorldMaterialsPerFrame</c> and
/// <c>WorldMaterialFrameBudgetMilliseconds</c> are declared but have no effect. World
/// materials are created synchronously while the map is built. Install this component
/// (and read those two budgets) only as part of moving that creation out of the load
/// path; until then, do not treat either budget as active.
/// </remarks>
[Library]
public sealed class Titanfall2WorldMaterialStreamer : Component, Component.DontExecuteOnServer, Sandbox.Internal.IUpdateSubscriber
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "Titanfall2WorldMaterials" );

	[Property, Hide]
	public string MountIdent { get; set; }

	[Property, Hide]
	public string[] MaterialPaths { get; set; } = [];

	ModelRenderer _renderer;
	int _nextMaterial;
	int _failedMaterials;
	bool _completed;
	readonly Stopwatch _statusTimer = new();
	readonly Stopwatch _budgetTimer = new();
	public bool IsCompleted => _completed;

	public void Configure( string mountIdent, IReadOnlyList<string> materialPaths )
	{
		MountIdent = mountIdent;
		MaterialPaths = materialPaths?.ToArray() ?? [];
	}

	protected override void OnEnabled()
	{
		// The status log below only fires once this clock has been started.
		_statusTimer.Start();
	}

	protected override void OnUpdate()
	{
		if ( _completed ) return;
		_renderer ??= Components.Get<ModelRenderer>();
		if ( _renderer is null || !_renderer.Model.IsValid() ) return;

		var mount = Sandbox.Mounting.Directory.Get( MountIdent ) as Titanfall2Mount;
		if ( mount is null || !mount.IsMounted ) return;

		_budgetTimer.Restart();
		var processed = 0;
		while ( _nextMaterial < MaterialPaths.Length
			&& processed < Titanfall2StreamingSettings.WorldMaterialsPerFrame
			&& _budgetTimer.Elapsed.TotalMilliseconds < Titanfall2StreamingSettings.WorldMaterialFrameBudgetMilliseconds )
		{
			var materialIndex = _nextMaterial++;
			var materialName = MaterialPaths[materialIndex];
			if ( !mount.TryGetMaterialPath( materialName, out var registeredPath ) )
			{
				_failedMaterials++;
				processed++;
				continue;
			}

			var material = Material.Load( $"mount://{MountIdent}/{registeredPath}.vmat" );
			if ( material is null || !material.IsValid )
			{
				_failedMaterials++;
				Titanfall2Log.Warning( $"Unable to stream Titanfall 2 world material '{materialName}'." );
			}
			else if ( materialIndex < _renderer.Materials.Count )
			{
				_renderer.Materials.SetOverride( materialIndex, material );
			}
			else
			{
				_failedMaterials++;
				Titanfall2Log.Warning( $"Titanfall 2 material slot {materialIndex} is outside the world model's material range." );
			}
			processed++;
		}

		if ( _nextMaterial >= MaterialPaths.Length )
		{
			_completed = true;
			Log.Info( $"Titanfall 2 world material streaming complete: {MaterialPaths.Length - _failedMaterials}/{MaterialPaths.Length} applied." );
			return;
		}

		if ( _statusTimer.Elapsed.TotalSeconds >= 5.0 )
		{
			Log.Info( $"Titanfall 2 world material streaming: {_nextMaterial}/{MaterialPaths.Length} applied, {_failedMaterials} skipped." );
			_statusTimer.Restart();
		}
	}
}

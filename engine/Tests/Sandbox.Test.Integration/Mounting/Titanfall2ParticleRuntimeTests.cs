using System;
using System.IO;
using Titanfall2;

namespace MountingTests;

[TestClass]
public class Titanfall2ParticleRuntimeTests
{
	Scene _scene;

	[TestCleanup]
	public void Cleanup() => _scene?.Destroy();

	[TestMethod]
	public void SpriteCardSizeFadeAttenuatesRgbAndAlphaAndRecoversWhenCameraMoves()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		new Titanfall2Mount().EnsureRuntimeTypesRegistered();
		var camera = scene.CreateObject().AddComponent<CameraComponent>();
		scene.UpdateMainCamera();
		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 100, 0, 0 );
		var points = go.AddComponent<Titanfall2ParticleControlPoints>();
		var definition = new Titanfall2ParticleDefinition
		{
			Burst = 1,
			LifetimeMinimum = 10f,
			LifetimeMaximum = 10f,
			RadiusMinimum = 100,
			RadiusMaximum = 100
		};
		Titanfall2ParticleFactory.ConfigureSimulation( go, definition, points );
		var geometry = go.AddComponent<Titanfall2ParticleGeometry>();
		geometry.Configure( definition, points, 0f );
		geometry.ConfigureSizeFade( 0.3f, 1.3f );
		var effect = go.Components.Get<ParticleEffect>();
		effect.Step( 1f / 30f );
		Assert.AreEqual( 0.3f, effect.Particles.Single().Alpha, 0.001f );
		Assert.AreEqual( 0.3f, effect.Particles.Single().Color.r, 0.001f );
		camera.WorldPosition = new Vector3( -900, 0, 0 );
		effect.Step( 1f / 30f );
		Assert.AreEqual( 1f, effect.Particles.Single().Alpha, 0.001f );
		Assert.AreEqual( 1f, effect.Particles.Single().Color.r, 0.001f );
		camera.WorldPosition = new Vector3( 90, 0, 0 );
		effect.Step( 1f / 30f );
		Assert.AreEqual( 0f, effect.Particles.Single().Alpha, 0.001f );
	}

	[TestMethod]
	public void RetailBoomtownUpwardFogRetainsItsAuthoredBirthVolume()
	{
		var root = Environment.GetEnvironmentVariable( "TF2_SMOKE_AUDIT_ROOT" );
		if ( string.IsNullOrWhiteSpace( root ) ) Assert.Inconclusive( "Set TF2_SMOKE_AUDIT_ROOT to locally extracted PCFs." );
		var bytes = File.ReadAllBytes( Path.Combine( root, "particles", "boomtown_env.pcf" ) );
		Assert.IsTrue( Titanfall2PcfReader.TryReadDefinitions( bytes, "boomtown_env.pcf", out var definitions, out var error ), error );
		var definition = definitions.Single( d => d.Name == "cenotes_upward_fog" );
		Assert.AreEqual( Titanfall2ParticleShape.Sphere, definition.Shape );
		Assert.AreEqual( 0f, definition.ShapeMaximum );
		Assert.AreEqual( new Vector3( -250, -400, -100 ), definition.PositionMinimum );
		Assert.AreEqual( new Vector3( 250, 400, 300 ), definition.PositionMaximum );
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		new Titanfall2Mount().EnsureRuntimeTypesRegistered();
		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 8905.92f, -5503.05f, 9984.76f );
		go.WorldRotation = Rotation.FromYaw( -114.448f );
		var points = go.AddComponent<Titanfall2ParticleControlPoints>();
		Titanfall2ParticleFactory.ConfigureSimulation( go, definition, points );
		var effect = go.Components.Get<ParticleEffect>();
		for ( var i = 0; i < 60; i++ ) effect.Step( 1f / 30f );
		var births = effect.Particles.Select( p => go.WorldTransform.PointToLocal( p.StartPosition ) ).ToArray();
		Assert.IsTrue( births.Length >= 10 );
		Assert.IsTrue( births.All( p => p.x >= -250.01f && p.x <= 250.01f && p.y >= -400.01f && p.y <= 400.01f && p.z >= -100.01f && p.z <= 300.01f ) );
		Assert.IsTrue( births.Distinct().Count() > 1, "Retail fog must not spawn every particle at the offset centre." );
		Console.WriteLine( $"Retail Boomtown fog: {births.Length} births, X span {births.Max( p => p.x ) - births.Min( p => p.x ):0.00}, "
			+ $"Y span {births.Max( p => p.y ) - births.Min( p => p.y ):0.00}, Z span {births.Max( p => p.z ) - births.Min( p => p.z ):0.00}." );
	}

	[DataTestMethod]
	[DataRow( true )]
	[DataRow( false )]
	public void SphereBirthsRetainRandomOffsetSpreadInTheAuthoredSpace( bool local )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		new Titanfall2Mount().EnsureRuntimeTypesRegistered();
		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 1000, 2000, 3000 );
		go.WorldRotation = Rotation.FromYaw( 90 );
		var points = go.AddComponent<Titanfall2ParticleControlPoints>();
		var definition = new Titanfall2ParticleDefinition
		{
			Shape = Titanfall2ParticleShape.Sphere,
			ShapeMaximum = 0f,
			Burst = 128,
			MaxParticles = 128,
			LifetimeMinimum = 3f,
			LifetimeMaximum = 3f,
			LocalPosition = local,
			PositionMinimum = new Vector3( -250, -400, -100 ),
			PositionMaximum = new Vector3( 250, 400, 300 )
		};
		Assert.IsNotNull( Titanfall2ParticleFactory.ConfigureSimulation( go, definition, points ) );
		var effect = go.Components.Get<ParticleEffect>();
		effect.Step( 1f / 30f );
		Assert.AreEqual( 128, effect.ParticleCount );
		var offsets = effect.Particles.Select( particle => local
			? go.WorldTransform.PointToLocal( particle.StartPosition ) : particle.StartPosition - go.WorldPosition ).ToArray();
		Assert.IsTrue( offsets.All( p => p.x >= -250 && p.x <= 250 && p.y >= -400 && p.y <= 400 && p.z >= -100 && p.z <= 300 ) );
		Assert.IsTrue( offsets.Max( p => p.x ) - offsets.Min( p => p.x ) > 350, "The 500-unit X spread must not collapse to its centre." );
		Assert.IsTrue( offsets.Max( p => p.y ) - offsets.Min( p => p.y ) > 600, "The 800-unit Y spread must not collapse to its centre." );
		Assert.IsTrue( offsets.Max( p => p.z ) - offsets.Min( p => p.z ) > 300, "The 400-unit Z spread must not collapse to its centre." );
	}

	[TestMethod]
	public void RingShapeIsRetainedWhenApplyingARandomOffset()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		new Titanfall2Mount().EnsureRuntimeTypesRegistered();
		var go = scene.CreateObject();
		var points = go.AddComponent<Titanfall2ParticleControlPoints>();
		var definition = new Titanfall2ParticleDefinition
		{
			Shape = Titanfall2ParticleShape.Ring,
			ShapeMaximum = 50f,
			ShapeThickness = 0f,
			Burst = 128,
			MaxParticles = 128,
			LifetimeMinimum = 3f,
			LifetimeMaximum = 3f,
			PositionMinimum = new Vector3( 0, 0, 100 ),
			PositionMaximum = new Vector3( 0, 0, 300 )
		};
		Titanfall2ParticleFactory.ConfigureSimulation( go, definition, points );
		var effect = go.Components.Get<ParticleEffect>();
		effect.Step( 1f / 30f );
		Assert.AreEqual( 128, effect.ParticleCount );
		Assert.IsTrue( effect.Particles.All( p => MathF.Abs( p.StartPosition.WithZ( 0 ).Length - 50f ) < 0.01f ) );
		Assert.IsTrue( effect.Particles.Max( p => p.StartPosition.z ) - effect.Particles.Min( p => p.StartPosition.z ) > 150f );
	}

	[DataTestMethod]
	[DataRow( false )]
	[DataRow( true )]
	public void EntityWarmupIsBoundedAndRespectsPauseWithoutDoubleAdvancing( bool pause )
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		var go = scene.CreateObject();
		var effect = go.AddComponent<ParticleEffect>();
		effect.Lifetime = 10f;
		var emitter = go.AddComponent<ParticleSphereEmitter>();
		emitter.Radius = 0f;
		emitter.Burst = 1f;
		emitter.Rate = 0f;
		emitter.ResetEmitter();
		var warmup = new Titanfall2ParticleWarmup( [(effect, 0f)], 0.2f, pause );
		Assert.IsTrue( effect.Paused );
		Assert.IsFalse( warmup.Advance( 1, 1000f ), "One simulation step must not consume the whole pre-roll." );
		Assert.AreEqual( 1, effect.ParticleCount );
		var before = effect.Particles.Single().Age;
		effect.Step( 1f );
		Assert.AreEqual( before, effect.Particles.Single().Age, "Loading ticks must not advance an unfinished pre-roll." );
		for ( var i = 0; i < 20 && !warmup.Complete; ++i ) warmup.Advance( 1, 1000f );
		Assert.IsTrue( warmup.Complete );
		Assert.AreEqual( pause, effect.Paused );
		Assert.AreEqual( 0.2f, effect.Particles.Single().Age, 0.001f );
		effect.Step( 0.1f );
		Assert.AreEqual( pause ? 0.2f : 0.3f, effect.Particles.Single().Age, 0.001f );
	}

	[TestMethod]
	public void MapAndRuntimeControlPointsOverrideChildDefaults()
	{
		var scene = _scene = new Scene();
		using var scope = scene.Push();
		new Titanfall2Mount().EnsureRuntimeTypesRegistered();
		var root = scene.CreateObject();
		var points = root.AddComponent<Titanfall2ParticleControlPoints>();
		points.SetControlPoint( 1, new Vector3( 100, 200, 300 ) );
		var child = new GameObject( root, true, "child" );
		var childPoints = child.AddComponent<Titanfall2ParticleControlPoints>();
		childPoints.Configure( points, [new ParticleControlPointDefinition( 1, new Vector3( 10, 0, 0 ) ),
			new ParticleControlPointDefinition( 2, new Vector3( 20, 0, 0 ) )] );
		Assert.AreEqual( new Vector3( 100, 200, 300 ), childPoints.GetControlPoint( 1 ) );
		Assert.AreEqual( new Vector3( 20, 0, 0 ), childPoints.GetControlPoint( 2 ) );
		points.SetControlPoint( 1, new Vector3( 400, 500, 600 ) );
		Assert.AreEqual( new Vector3( 400, 500, 600 ), childPoints.GetControlPoint( 1 ) );
	}
}

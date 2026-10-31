using System;
using System.Collections.Generic;
using System.Reflection;
using Titanfall2.Formats;
using Mdl = Titanfall2.Formats.Titanfall2Mdl53Reader;

namespace MountingTests;

[TestClass]
[DoNotParallelize]
public class Titanfall2FeatureTest
{
	[TestMethod]
	public void ContentsQueriesDistinguishPilotTitanBulletAndNoGrapple()
	{
		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			Game.TypeLibrary.AddAssembly( typeof( Titanfall2CollisionSource ).Assembly, false );
			var model = Model.Builder.AddCollisionHull( new BBox( -Vector3.One, Vector3.One ).Corners.ToArray() ).Create();
			void Add( float x, Titanfall2Contents contents )
			{
				var go = scene.CreateObject(); go.WorldPosition = new Vector3( x, 0, 0 );
				var collider = go.AddComponent<ModelCollider>(); collider.Model = model; collider.Static = true;
				var source = go.AddComponent<Titanfall2CollisionSource>(); source.EffectiveContents = (uint)contents; source.HasEffectiveContents = true;
				typeof( Titanfall2CollisionQueries ).GetMethod( "Classify", BindingFlags.NonPublic | BindingFlags.Static ).Invoke( null, [go, collider, (uint)contents] );
			}
			Add( 10, Titanfall2Contents.BulletClip | Titanfall2Contents.NoGrapple );
			Add( 20, Titanfall2Contents.PlayerClip );
			Add( 30, Titanfall2Contents.TitanClip );
			for ( var i = 0; i < 2; ++i ) scene.GameTick( 0.02 );
			SceneTrace Ray() => scene.Trace.Ray( Vector3.Zero, new Vector3( 40, 0, 0 ) );
			var pilot = Titanfall2CollisionQueries.Run( Ray(), Titanfall2TraceMask.PlayerSolid, false );
			var titan = Titanfall2CollisionQueries.Run( Ray(), Titanfall2TraceMask.TitanSolid, false );
			var bullet = Titanfall2CollisionQueries.Run( Ray(), Titanfall2TraceMask.Shot, false );
			Assert.IsTrue( pilot.Hit ); Assert.AreEqual( 19f, pilot.EndPosition.x, 0.1f );
			Assert.IsTrue( titan.Hit ); Assert.AreEqual( 29f, titan.EndPosition.x, 0.1f );
			Assert.IsTrue( bullet.Hit ); Assert.AreEqual( 9f, bullet.EndPosition.x, 0.1f );
			Assert.IsFalse( Titanfall2CollisionQueries.CanGrapple( bullet ) );
			Assert.IsFalse( Titanfall2CollisionQueries.Run( Ray(), 0u, false ).Hit );
		}
		finally { scene.Destroy(); }
	}

	[TestMethod]
	public void MdlAttachmentAndFormalHitboxReachNativeModel()
	{
		var parsed = new Mdl.ParsedModel
		{
			Header = new Mdl53StudioHeader { SkinFamilyCount = 2 },
			Bones = [new Mdl.BoneInfo { Name = "root", SurfaceProperty = "flesh", Bone = new Mdl53Bone
				{ Parent = -1, Position = new System.Numerics.Vector3( 10, 0, 0 ), RotationQuaternion = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One } }],
			Attachments = [new Mdl.AttachmentInfo { Name = "muzzle", Attachment = new Mdl53Attachment
				{ LocalBone = 0, LocalTransform = new Titanfall2.Formats.Matrix3x4 { M00 = 1, M11 = 1, M22 = 1, M03 = 2, M13 = 3, M23 = 4 } } }],
			HitboxSets = [new Mdl.HitboxSetInfo { Name = "combat", Hitboxes = [new Mdl.HitboxInfo { Name = "head", KeyValue = "critical", Hitbox = new Mdl53Hitbox
				{ Bone = 0, Group = 1, CriticalOverride = 7, BoundingBoxMin = -System.Numerics.Vector3.One, BoundingBoxMax = System.Numerics.Vector3.One } }] }]
		};
		var type = typeof( Titanfall2Mount ).Assembly.GetType( "ModelLoader" );
		var loader = Activator.CreateInstance( type, [null, false, 0, false, new Titanfall2Mount(), "test_metadata", 1, 0, false] );
		var builder = Model.Builder.AddCollisionHull( new BBox( -Vector3.One, Vector3.One ).Corners.ToArray() );
		var mesh = new Mesh( Material.Load( "materials/default/default.vmat" ) );
		mesh.CreateVertexBuffer( 3, new[] { new Vertex( Vector3.Zero ), new Vertex( Vector3.Forward ), new Vertex( Vector3.Left ) } );
		mesh.CreateIndexBuffer( 3, new[] { 0, 1, 2 } );
		builder.AddMesh( mesh );
		var skeleton = type.GetMethod( "AddSkeleton", BindingFlags.NonPublic | BindingFlags.Static ).Invoke( null, [builder, parsed] );
		type.GetMethod( "AddModelMetadata", BindingFlags.NonPublic | BindingFlags.Instance ).Invoke( loader, [builder, parsed, skeleton] );
		var model = builder.Create();
		Console.WriteLine( $"bone={model.GetBoneTransform( 0 )} local={model.Attachments.Get( "muzzle" )?.LocalTransform} bone_name={model.Attachments.Get( "muzzle" )?.Bone?.Name} modern={model.Attachments.GetTransform( "muzzle" )}" );
		Assert.AreEqual( 1, model.AttachmentCount );
		Assert.AreEqual( new Vector3( 12, 3, 4 ), model.Attachments.GetTransform( "muzzle" ).Value.Position );
		var hitbox = model.HitboxSet.All.Single();
		Assert.AreEqual( "head", hitbox.Name ); Assert.AreEqual( "root", hitbox.Bone.Name );
		Assert.IsTrue( hitbox.Tags.Has( "titanfall2_hitgroup_1" ) );
		var metadata = model.GetData<Titanfall2ModelData>();
		Assert.AreEqual( 1, metadata.Skin ); Assert.AreEqual( 2, metadata.SkinCount );
		Assert.AreEqual( 7, metadata.Hitboxes.Single().CriticalOverride );
		Assert.IsTrue( hitbox.Tags.Has( "head" ) );
		Assert.IsTrue( hitbox.Tags.Has( "titanfall2_critical_override" ) );
		Assert.AreEqual( "flesh", metadata.Hitboxes.Single().Surface );
	}

	[TestMethod]
	public void DuplicateAndZeroMatrixAttachmentsKeepRetailLookup()
	{
		var parsed = new Mdl.ParsedModel
		{
			Bones = [new Mdl.BoneInfo { Name = "root", Bone = new Mdl53Bone
				{ Parent = -1, RotationQuaternion = System.Numerics.Quaternion.Identity, Scale = System.Numerics.Vector3.One } }],
			Attachments =
			[
				new Mdl.AttachmentInfo { Name = "muzzle_flash", Attachment = new Mdl53Attachment
					{ LocalBone = 0, LocalTransform = new Titanfall2.Formats.Matrix3x4 { M00 = 1, M11 = 1, M22 = 1, M03 = 5 } } },
				new Mdl.AttachmentInfo { Name = "muzzle_flash", Attachment = new Mdl53Attachment
					{ LocalBone = 0, LocalTransform = new Titanfall2.Formats.Matrix3x4 { M00 = 1, M11 = 1, M22 = 1, M03 = 9 } } },
				new Mdl.AttachmentInfo { Name = "SCR_TL_BL", Attachment = new Mdl53Attachment { LocalBone = 0 } }
			]
		};
		var (type, loader) = CreateLoader( skin: 0 );
		var builder = Model.Builder;
		builder.AddMesh( CreateTriangle() );
		var skeleton = type.GetMethod( "AddSkeleton", BindingFlags.NonPublic | BindingFlags.Static ).Invoke( null, [builder, parsed] );
		type.GetMethod( "AddModelMetadata", BindingFlags.NonPublic | BindingFlags.Instance ).Invoke( loader, [builder, parsed, skeleton] );
		var model = builder.Create();
		Assert.AreEqual( 2, model.AttachmentCount );
		Assert.AreEqual( 5f, model.Attachments.GetTransform( "muzzle_flash" ).Value.Position.x, 0.001f );
		Assert.IsTrue( model.Attachments.GetTransform( "SCR_TL_BL" ).HasValue );
	}

	[TestMethod]
	public void ReadableContentsTagsSeparateClipsFromSolidProps()
	{
		string[] Tags( uint contents ) => Titanfall2CollisionQueries.ReadableTags( contents ).Where( tag => tag.Applies ).Select( tag => tag.Tag ).ToArray();
		// Solid static prop expansion carries every clip bit plus BulletClip: not a clip.
		CollectionAssert.AreEqual( Array.Empty<string>(), Tags( Titanfall2CollisionSource.StaticPropContents( 1, 0 ) ) );
		// Retail movement clip brush (player/titan/monster/operator, no bullet clip).
		CollectionAssert.AreEquivalent( new[] { "playerclip", "titanclip" }, Tags( 0x00A30000u ) );
		CollectionAssert.AreEquivalent( new[] { "nowallrun" }, Tags( 0x00FB0280u ) );
		CollectionAssert.AreEquivalent( new[] { "nograpple" }, Tags( (uint)(Titanfall2Contents.Solid | Titanfall2Contents.NoGrapple) ) );
	}

	[TestMethod]
	public void SkinFamilyAndLodReplacementResolveMaterialNames()
	{
		var parsed = new Mdl.ParsedModel
		{
			Header = new Mdl53StudioHeader { SkinFamilyCount = 2, SkinReferenceCount = 2 },
			Textures = [new Mdl.TextureInfo { Index = 0, Name = "body" }, new Mdl.TextureInfo { Index = 1, Name = "glass" }, new Mdl.TextureInfo { Index = 2, Name = "body_red" }],
			SkinTable = [0, 1, 2, 1],
			EmbeddedVtx = new Titanfall2VtxReader.ParsedVtx
			{
				MaterialReplacementLists =
				[
					new Titanfall2VtxReader.MaterialReplacementListInfo { LodIndex = 0 },
					new Titanfall2VtxReader.MaterialReplacementListInfo { LodIndex = 1, Replacements =
						[new Titanfall2VtxReader.MaterialReplacementInfo { Header = new VtxMaterialReplacementHeader { MaterialId = 2 }, MaterialName = "body_red_lod1" }] }
				]
			}
		};
		Assert.AreEqual( "body", Titanfall2MeshExtractor.ResolveMaterialName( parsed, 0, 0, 0 ) );
		Assert.AreEqual( "body_red", Titanfall2MeshExtractor.ResolveMaterialName( parsed, 0, 0, 1 ) );
		Assert.AreEqual( "body", Titanfall2MeshExtractor.ResolveMaterialName( parsed, 0, 1, 0 ) );
		Assert.AreEqual( "body_red_lod1", Titanfall2MeshExtractor.ResolveMaterialName( parsed, 0, 1, 1 ) );
		Assert.AreEqual( "glass", Titanfall2MeshExtractor.ResolveMaterialName( parsed, 1, 1, 1 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => Titanfall2MeshExtractor.ResolveMaterialName( parsed, 0, 0, 2 ) );
	}

	[TestMethod]
	public void NativeBodyGroupsAndSkinMaterialGroupsSwitchOnOneModel()
	{
		var parsed = new Mdl.ParsedModel
		{
			Header = new Mdl53StudioHeader { SkinFamilyCount = 2, SkinReferenceCount = 1 },
			Textures = [new Mdl.TextureInfo { Name = "a" }, new Mdl.TextureInfo { Name = "b" }],
			SkinTable = [0, 1],
			BodyParts =
			[
				new Mdl.BodyPartInfo { Name = "body", BodyPart = new Mdl53BodyPart { Base = 1, ModelCount = 1 }, Models = [new Mdl.ModelInfo { Name = "body" }] },
				new Mdl.BodyPartInfo { Name = "helmet", BodyPart = new Mdl53BodyPart { Base = 1, ModelCount = 3 },
					Models = [new Mdl.ModelInfo { Name = "on" }, new Mdl.ModelInfo { Name = "visor" }, new Mdl.ModelInfo { Name = "blank" }] }
			]
		};
		var (type, loader) = CreateLoader( skin: 0 );
		var names = (string[])type.GetMethod( "NativeBodyGroupNames", BindingFlags.NonPublic | BindingFlags.Static ).Invoke( null, [parsed] );
		CollectionAssert.AreEqual( new[] { "body", "helmet" }, names );
		var builder = Model.Builder;
		type.GetMethod( "RegisterBodyGroups", BindingFlags.NonPublic | BindingFlags.Instance ).Invoke( loader, [builder, parsed, names] );
		var material = Material.Load( "materials/default/default.vmat" );
		builder.AddMesh( CreateTriangle( material ), 0, "body", 0 );
		builder.AddMesh( CreateTriangle( material ), 0, "helmet", 0 );
		builder.AddMesh( CreateTriangle( material ), 0, "helmet", 1 );
		var slots = new List<(Material Material, int SkinReference)> { (material, 0) };
		type.GetMethod( "AddSkinMaterialGroups", BindingFlags.NonPublic | BindingFlags.Instance ).Invoke( loader, [builder, parsed, slots, false] );
		var model = builder.Create();

		var helmet = model.Parts.Get( "helmet" );
		Assert.IsNotNull( helmet );
		Assert.AreEqual( 3, helmet.Choices.Count );
		Assert.AreEqual( 2, model.MaterialGroupCount );
		Assert.AreEqual( "Skin 0", model.GetMaterialGroupName( 0 ) );
		Assert.AreEqual( "Skin 1", model.GetMaterialGroupName( 1 ) );

		var scene = new Scene();
		try
		{
			using var scope = scene.Push();
			var renderer = scene.CreateObject().AddComponent<ModelRenderer>();
			renderer.Model = model;
			Assert.AreEqual( 0, renderer.GetBodyGroup( "helmet" ) );
			renderer.SetBodyGroup( "helmet", 2 );
			Assert.AreEqual( 2, renderer.GetBodyGroup( "helmet" ) );
			renderer.MaterialGroup = "Skin 1";
			Assert.AreEqual( "Skin 1", renderer.MaterialGroup );
		}
		finally { scene.Destroy(); }
	}

	[TestMethod]
	public void PackedBodyChangeKeepsOtherGroups()
	{
		var data = new Titanfall2ModelData
		{
			BodyGroups = [new( "head", 1, ["a", "b", "c"] ), new( "gear", 3, ["none", "pack"] )]
		};
		var body = data.SetBodyGroup( 0, "gear", 1 );
		Assert.AreEqual( 3, body );
		body = data.SetBodyGroup( body, "head", 2 );
		Assert.AreEqual( 5, body );
		Assert.AreEqual( 2, data.SetBodyGroup( body, "gear", 0 ) );
		Assert.ThrowsException<ArgumentOutOfRangeException>( () => data.SetBodyGroup( body, "gear", 2 ) );
	}

	static (Type Type, object Loader) CreateLoader( int skin )
	{
		var type = typeof( Titanfall2Mount ).Assembly.GetType( "ModelLoader" );
		return (type, Activator.CreateInstance( type, [null, false, 0, false, new Titanfall2Mount(), "test_metadata", skin, 0, false] ));
	}

	static Mesh CreateTriangle( Material material = null )
	{
		var mesh = new Mesh( material ?? Material.Load( "materials/default/default.vmat" ) );
		mesh.CreateVertexBuffer( 3, new[] { new Vertex( Vector3.Zero ), new Vertex( Vector3.Forward ), new Vertex( Vector3.Left ) } );
		mesh.CreateIndexBuffer( 3, new[] { 0, 1, 2 } );
		return mesh;
	}
}

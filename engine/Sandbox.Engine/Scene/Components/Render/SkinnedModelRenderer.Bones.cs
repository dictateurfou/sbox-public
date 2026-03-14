namespace Sandbox;

public sealed partial class SkinnedModelRenderer
{
	readonly Dictionary<BoneCollection.Bone, GameObject> boneToGameObject = new();

	/// <summary>
	/// Get the <see cref="GameObject"/> proxy for a specific bone by index.
	/// Returns <see langword="null"/> when <see cref="CreateBoneObjects"/> is not enabled, or
	/// when the index is out of range. When <see cref="BoneMergeTarget"/> is set, the proxy
	/// transform is driven by the target's animation rather than this renderer's own animation.
	/// </summary>
	public GameObject GetBoneObject( int index )
	{
		if ( Model is null )
			return null;

		if ( index < 0 || index >= Model.Bones.AllBones.Count )
			return null;

		var bone = Model.Bones.AllBones[index];

		if ( boneToGameObject.TryGetValue( bone, out var gameObject ) )
		{
			return gameObject;
		}

		return null;
	}

	/// <summary>
	/// Find a bone's proxy <see cref="GameObject"/> by bone name.
	/// Returns <see langword="null"/> when <see cref="CreateBoneObjects"/> is not enabled or
	/// the name is not found. When <see cref="BoneMergeTarget"/> is set, the proxy transform is
	/// driven by the target's animation.
	/// </summary>
	public GameObject GetBoneObject( string boneName )
	{
		if ( Model is null ) return null;
		return GetBoneObject( Model.Bones.GetBone( boneName ) );
	}

	/// <inheritdoc cref="GetBoneObject(int)"/>
	public override GameObject GetBoneObject( BoneCollection.Bone bone )
	{
		if ( bone is null )
			return null;

		if ( boneToGameObject.TryGetValue( bone, out var gameObject ) )
		{
			return gameObject;
		}

		return null;
	}

	/// <summary>
	/// (Re-)creates the bone proxy <see cref="GameObject"/> hierarchy for this renderer.
	/// Existing proxies are cleared first. When <see cref="CreateBoneObjects"/> is
	/// <see langword="false"/> or the model is <see langword="null"/> the method returns without
	/// creating anything. After construction the proxy transforms are populated during the next
	/// <c>UpdateGameObjectsFromBones</c> call, which occurs either during the normal animation
	/// tick or immediately via <c>SetBoneMerge</c> when a <see cref="BoneMergeTarget"/> is
	/// assigned.
	/// </summary>
	void BuildBoneHierarchy()
	{
		ClearBoneProxies();

		if ( !CreateBoneObjects )
			return;

		if ( Model is null )
			return;

		var boneObjects = Model.CreateBoneObjects( GameObject );

		boneToGameObject.Clear();

		foreach ( var entry in boneObjects )
		{
			boneToGameObject[entry.Key] = entry.Value;
		}
	}

	/// <summary>
	/// Removes the <see cref="GameObjectFlags.Bone"/> flag from every proxy
	/// <see cref="GameObject"/> and clears the internal bone-to-proxy mapping. Called when the
	/// renderer is disabled or the model is changed.
	/// </summary>
	void ClearBoneProxies()
	{
		if ( boneToGameObject.Count == 0 )
			return;

		foreach ( var o in boneToGameObject )
		{
			if ( !o.Value.IsValid() )
				continue;

			o.Value.Flags &= ~GameObjectFlags.Bone;
		}

		boneToGameObject.Clear();
	}

	/// <summary>
	/// Try to get the final worldspace bone transform.
	/// </summary>
	public bool TryGetBoneTransform( string boneName, out Transform tx )
	{
		tx = global::Transform.Zero;
		if ( !SceneModel.IsValid() ) return false;
		if ( Model is null ) return false;

		return TryGetBoneTransform( Model.Bones.GetBone( boneName ), out tx );
	}

	/// <summary>
	/// Try to get the final worldspace bone transform.
	/// </summary>
	public bool TryGetBoneTransform( in BoneCollection.Bone bone, out Transform tx )
	{
		tx = global::Transform.Zero;

		if ( bone is null ) return false;
		if ( !SceneModel.IsValid() ) return false;

		tx = SceneModel.GetBoneWorldTransform( bone.Index );

		// would be great to have it return something more indicitive
		if ( tx == global::Transform.Zero ) return false;

		return true;
	}

	public bool TryGetBoneTransformLocal( string boneName, out Transform tx )
	{
		tx = global::Transform.Zero;
		if ( !SceneModel.IsValid() ) return false;
		if ( Model is null ) return false;

		return TryGetBoneTransformLocal( Model.Bones.GetBone( boneName ), out tx );
	}

	public bool TryGetBoneTransformLocal( in BoneCollection.Bone bone, out Transform tx )
	{
		tx = global::Transform.Zero;

		if ( bone is null ) return false;
		if ( !SceneModel.IsValid() ) return false;

		tx = SceneModel.GetBoneLocalTransform( bone.Index );

		// would be great to have it return something more indicitive
		if ( tx == global::Transform.Zero ) return false;

		return true;
	}

	/// <summary>
	/// Try to get the worldspace bone transform after animation but before physics and procedural bones.
	/// </summary>
	public bool TryGetBoneTransformAnimation( in BoneCollection.Bone bone, out Transform tx )
	{
		tx = global::Transform.Zero;

		if ( bone is null ) return false;
		if ( !SceneModel.IsValid() ) return false;

		tx = SceneModel.GetWorldSpaceAnimationTransform( bone.Index );

		return true;
	}

	public void SetBoneTransform( in BoneCollection.Bone bone, Transform transform )
	{
		if ( !SceneModel.IsValid() ) return;
		ArgumentNullException.ThrowIfNull( bone, nameof( bone ) );

		SceneModel.SetBoneOverride( bone.Index, transform );
	}

	public void ClearPhysicsBones()
	{
		if ( !SceneModel.IsValid() ) return;
		SceneModel.ClearBoneOverrides();
	}

	/// <summary>
	/// Allocate an array of bone transforms in either world space or parent space.
	/// </summary>
	public Transform[] GetBoneTransforms( bool world )
	{
		Assert.NotNull( Model, "Model should not be null when calling GetBoneTransforms" );

		Transform[] transforms = new Transform[Model.BoneCount];

		for ( int i = 0; i < Model.BoneCount; i++ )
		{
			if ( world )
			{
				transforms[i] = SceneModel?.GetBoneWorldTransform( i ) ?? Model.GetBoneTransform( i );
			}
			else
			{
				transforms[i] = SceneModel?.GetBoneLocalTransform( i ) ?? Model.GetBoneTransform( i );
			}
		}

		return transforms;
	}

	public record struct BoneVelocity( Vector3 Linear, Vector3 Angular );

	/// <summary>
	/// Allocate an array of bone velocities in world space
	/// </summary>
	public BoneVelocity[] GetBoneVelocities()
	{
		Assert.NotNull( Model, "Model should not be null when calling GetBoneVelocities" );

		BoneVelocity[] transforms = new BoneVelocity[Model.BoneCount];

		if ( !SceneModel.IsValid() )
			return transforms;

		for ( int i = 0; i < Model.BoneCount; i++ )
		{
			SceneModel.GetBoneVelocity( i, out var linear, out var angular );
			transforms[i] = new BoneVelocity( linear, angular );
		}

		return transforms;
	}

	/// <summary>
	/// Retrieve the bone's velocities based on previous and current position
	/// </summary>
	public BoneVelocity GetBoneVelocity( int boneIndex )
	{
		Assert.NotNull( Model, "Model should not be null when calling GetBoneVelocity" );

		if ( !SceneModel.IsValid() || boneIndex < 0 || boneIndex >= Model.BoneCount )
			return default;

		SceneModel.GetBoneVelocity( boneIndex, out var linear, out var angular );
		return new BoneVelocity( linear, angular );
	}
}

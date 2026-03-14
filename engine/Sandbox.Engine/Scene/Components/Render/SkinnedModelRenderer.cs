
namespace Sandbox;

/// <summary>
/// Renders a skinned model in the world. A skinned model is any model with bones/animations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bone Proxies</b><br/>
/// When <see cref="CreateBoneObjects"/> is enabled, a child <see cref="GameObject"/> is created for
/// every bone in the skeleton, forming a hierarchy that mirrors the model's bone tree. These
/// GameObjects are flagged with <see cref="GameObjectFlags.Bone"/> and their transforms are kept in
/// sync with the running animation each frame via <c>UpdateGameObjectsFromBones</c>. You can attach
/// child objects (e.g. weapons, accessories) to a bone proxy so they automatically follow the
/// animation. A bone can also be flagged as <see cref="GameObjectFlags.ProceduralBone"/> to let you
/// drive its transform manually instead of having the animation overwrite it.
/// </para>
/// <para>
/// <b>Bone Merging (<see cref="BoneMergeTarget"/>)</b><br/>
/// Setting <see cref="BoneMergeTarget"/> on this renderer causes its skeleton to be driven entirely
/// by the target renderer's animation. Every frame the target calls
/// <c>SceneModel.MergeBones</c> on this renderer's scene model, copying over all bone transforms
/// from the target, so the two models animate in perfect synchrony. This is the mechanism used by
/// the clothing / avatar system where multiple clothing pieces need to deform along with the base
/// character body.
/// </para>
/// <para>
/// <b>Bone Proxies + Bone Merging</b><br/>
/// The two features compose naturally. When a renderer has both <see cref="CreateBoneObjects"/>
/// enabled <i>and</i> a <see cref="BoneMergeTarget"/> set, its bone proxy GameObjects will be
/// updated to reflect the <em>target's</em> bone transforms (not its own independent animation).
/// The root bone proxies receive an additional offset (<c>mergeOffset</c>) so they stay positioned
/// relative to the root of the merge chain (<see cref="RootBoneMergeTarget"/>) rather than being
/// placed at the world origin. Non-root bones are left in parent-space and therefore automatically
/// stay under the correct parent bone proxy without any extra offset.
/// </para>
/// </remarks>
[Title( "Model Renderer (skinned)" )]
[Category( "Rendering" )]
[Icon( "sports_martial_arts" )]
[Alias( "AnimatedModelComponent" )]
public sealed partial class SkinnedModelRenderer : ModelRenderer, Component.ExecuteInEditor
{
	bool _createBones = false;

	/// <summary>
	/// When enabled, creates a child <see cref="GameObject"/> for every bone in the model's
	/// skeleton. Each proxy is kept in sync with the running animation (or the
	/// <see cref="BoneMergeTarget"/>'s animation) every frame. Attach child objects to a bone
	/// proxy to make them follow the animation automatically. Mark a proxy with
	/// <see cref="GameObjectFlags.ProceduralBone"/> to drive its transform manually.
	/// </summary>
	[Property, Group( "Bones", StartFolded = true )]
	public bool CreateBoneObjects
	{
		get => _createBones;
		set
		{
			if ( _createBones == value ) return;
			_createBones = value;

			UpdateObject();
		}
	}

	SkinnedModelRenderer _boneMergeTarget;

	/// <summary>
	/// When set, this renderer's skeleton is driven by the target renderer's animation instead
	/// of its own. Every frame the target copies its bone transforms into this renderer so the
	/// two models deform identically. If <see cref="CreateBoneObjects"/> is also enabled, this
	/// renderer's bone proxy <see cref="GameObject"/>s will follow the target's bones rather than
	/// an independent animation. The merge is applied immediately on assignment to avoid a
	/// one-frame flicker at the default bind pose.
	/// </summary>
	/// <remarks>
	/// A renderer cannot be its own target. Changes propagate through chains: if A → B and
	/// B → C, then C drives both B and A via <see cref="MergeDescendants"/> called from the
	/// root of the chain. The ultimate root is returned by <see cref="RootBoneMergeTarget"/>.
	/// </remarks>
	[Property, Group( "Bones" )]
	public SkinnedModelRenderer BoneMergeTarget
	{
		get => _boneMergeTarget;
		set
		{
			if ( value == this ) return;
			if ( _boneMergeTarget == value ) return;

			_boneMergeTarget?.SetBoneMerge( this, false );

			_boneMergeTarget = value;

			_boneMergeTarget?.SetBoneMerge( this, true );
		}
	}

	bool _useAnimGraph = true;

	/// <summary>
	/// Usually used for turning off animation on ragdolls.
	/// </summary>
	[Property, Group( "Animation" ), Title( "Use Animation Graph" )]
	public bool UseAnimGraph
	{
		get => _useAnimGraph;
		set
		{
			if ( _useAnimGraph == value ) return;

			_useAnimGraph = value;

			if ( SceneModel.IsValid() )
			{
				SceneModel.UseAnimGraph = value;
			}
		}
	}

	AnimationGraph _animationGraph;

	/// <summary>
	/// Override animgraph, otherwise uses animgraph of the model.
	/// </summary>
	[Property, Group( "Animation" ), ShowIf( nameof( UseAnimGraph ), true )]
	public AnimationGraph AnimationGraph
	{
		get => _animationGraph;
		set
		{
			if ( _animationGraph == value ) return;

			_animationGraph = value;

			if ( SceneModel.IsValid() )
			{
				SceneModel.AnimationGraph = value;
			}
		}
	}

	/// <summary>
	/// Allows playback of sequences directly, rather than using an animation graph.
	/// Requires <see cref="UseAnimGraph"/> disabled if the scene model has one.
	/// </summary>
	[Property, Group( "Animation" ), ShowIf( nameof( ShouldShowSequenceEditor ), true ), InlineEditor( Label = false )]
	public SequenceAccessor Sequence
	{
		get
		{
			_sequence ??= new SequenceAccessor( this );
			return _sequence;
		}
	}

	float _playbackRate = 1.0f;

	/// <summary>
	/// Control playback rate of animgraph or current sequence.
	/// </summary>
	[Property, Range( 0.0f, 4.0f ), Group( "Animation" )]
	public float PlaybackRate
	{
		get => _playbackRate;
		set
		{
			if ( _playbackRate == value ) return;

			_playbackRate = value;

			if ( SceneModel.IsValid() )
			{
				SceneModel.PlaybackRate = _playbackRate;
			}
		}
	}

	public SceneModel SceneModel => _sceneObject as SceneModel;

	public Transform RootMotion => SceneModel.IsValid() ? SceneModel.RootMotion : default;

	readonly HashSet<SkinnedModelRenderer> mergeChildren = new();

	/// <summary>
	/// Does our model have collision and joints.
	/// </summary>
	bool HasBonePhysics()
	{
		return Model.IsValid() && Model.Physics is { Parts.Count: > 0, Joints.Count: > 0 };
	}

	/// <summary>
	/// Registers or unregisters <paramref name="newChild"/> as a bone-merge child of this
	/// renderer. Called by the child when its <see cref="BoneMergeTarget"/> property is set.
	/// </summary>
	/// <remarks>
	/// When <paramref name="enabled"/> is <see langword="true"/> the child is added to
	/// <c>mergeChildren</c> and, if both scene models already exist, a full synchronisation is
	/// performed immediately:
	/// <list type="number">
	///   <item>The child's scene-model world transform is snapped to ours.</item>
	///   <item><c>SceneModel.MergeBones</c> copies every bone transform from our scene model
	///   into the child's, so the child is already in the correct pose before the first rendered
	///   frame.</item>
	///   <item><c>UpdateGameObjectsFromBones</c> propagates those bone transforms into the
	///   child's bone proxy <see cref="GameObject"/>s (when
	///   <see cref="CreateBoneObjects"/> is enabled on the child).</item>
	///   <item>Bone physics is (re-)created on the child if the child's model has physics
	///   bodies and joints.</item>
	/// </list>
	/// When <paramref name="enabled"/> is <see langword="false"/> the child is removed and its
	/// physics destroyed; it will revert to animating independently on the next frame.
	/// </remarks>
	private void SetBoneMerge( SkinnedModelRenderer newChild, bool enabled )
	{
		ArgumentNullException.ThrowIfNull( newChild );

		if ( enabled )
		{
			mergeChildren.Add( newChild );

			// Merge immediately if we can. This prevents a problem where components
			// are added after the animation has been worked out, so you get a one frame
			// flicker of the default pose.
			if ( SceneModel is not null && newChild.SceneModel is not null )
			{
				newChild.SceneModel.Transform = SceneModel.Transform;
				newChild.SceneModel.MergeBones( SceneModel );

				// Updated bones, transform is no longer dirty.
				newChild._transformDirty = false;

				// Create bone physics on child if they exist.
				newChild.Physics?.Destroy();
				newChild.Physics = newChild.HasBonePhysics() ? new BonePhysics( newChild, this ) : null;

				if ( !newChild.UpdateGameObjectsFromBones() )
					return;

				if ( ThreadSafe.IsMainThread )
				{
					newChild.Transform.TransformChanged();
				}
			}
		}
		else
		{
			mergeChildren.Remove( newChild );

			newChild.Physics?.Destroy();
			newChild.Physics = null;
		}
	}

	protected override void OnEnabled()
	{
		Assert.True( _sceneObject == null, "_sceneObject should be null - disable wasn't called" );
		Assert.NotNull( Scene, "Scene should not be null" );

		var model = Model ?? Model.Load( "models/dev/box.vmdl" );

		var so = new SceneModel( Scene.SceneWorld, model, WorldTransform );
		_sceneObject = so;

		if ( AnimationGraph is not null )
		{
			so.AnimationGraph = AnimationGraph;
		}

		if ( so.UseAnimGraph != UseAnimGraph )
		{
			so.UseAnimGraph = UseAnimGraph;
		}

		so.PlaybackRate = PlaybackRate;

		OnSceneObjectCreated( _sceneObject );

		Transform.OnTransformChanged += OnTransformChanged;
	}

	internal override void OnSceneObjectCreated( SceneObject o )
	{
		base.OnSceneObjectCreated( o );

		ApplyStoredAnimParameters();

		Morphs.Apply();
		Sequence.Apply();
	}

	protected override void UpdateObject()
	{
		BuildBoneHierarchy();

		base.UpdateObject();

		if ( !SceneModel.IsValid() )
			return;

		SceneModel.OnFootstepEvent = InternalOnFootstep;
		SceneModel.OnSoundEvent = InternalOnSoundEvent;
		SceneModel.OnGenericEvent = InternalOnGenericEvent;
		SceneModel.OnAnimTagEvent = InternalOnAnimTagEvent;

		//
		// If we have a bone merge target then just set up the bone merge
		// which will read the bones and set the game object positions.
		//
		// If we're not bone merge, then do a first frame update to set
		// the bone positions before anything else happens.
		//
		if ( _boneMergeTarget is not null )
		{
			_boneMergeTarget.SetBoneMerge( this, true );
		}
		else
		{
			if ( Scene.IsEditor && !CanUpdateInEditor() )
			{
				SceneModel.UpdateToBindPose( ReadBonesFromGameObjects );
			}
			else
			{
				UpdateTransform( WorldTransform );
			}

			// Updated bones, transform is no longer dirty.
			_transformDirty = false;

			UpdateGameObjectsFromBones();
		}
	}

	internal override void OnDisabledInternal()
	{
		try
		{
			ClearBoneProxies();
		}
		finally
		{
			Transform.OnTransformChanged -= OnTransformChanged;

			base.OnDisabledInternal();
		}

		Physics?.Destroy();
	}

	/// <summary>
	/// Called by the scene system after animation has been evaluated for this frame. Dispatches
	/// pending animation events and, when this renderer is the <em>root</em> of a bone-merge
	/// chain (i.e. its own <see cref="BoneMergeTarget"/> is <see langword="null"/>), propagates
	/// the merged bone transforms down to all registered children via
	/// <see cref="MergeDescendants"/>. Renderers that are already bone-merged skip this call
	/// because their root target handles the update for the whole chain.
	/// </summary>
	public void PostAnimationUpdate()
	{
		ThreadSafe.AssertIsMainThread();

		if ( !SceneModel.IsValid() )
			return;

		SceneModel.RunPendingEvents();
		SceneModel.DispatchTagEvents();

		// Skip if we're bone merged, the target will handle the merge.
		if ( _boneMergeTarget.IsValid() )
			return;

		// Bone merge all children in hierarchy in order.
		MergeDescendants();
	}

	/// <summary>
	/// If true then animations will play while in an editor scene.
	/// </summary>
	public bool PlayAnimationsInEditorScene { get; set; }

	internal bool CanUpdateInEditor()
	{
		if ( PlayAnimationsInEditorScene ) return true;

		// Do we have any modified animgraph parameters?
		if ( parameters.Count > 0 )
			return true;

		// If we're not using animgraph, do we have a sequence selected?
		if ( !UseAnimGraph && !string.IsNullOrWhiteSpace( Sequence.Name ) )
			return true;

		// Have we procedurally moved any bones?
		return SceneModel.IsValid() && SceneModel.HasBoneOverrides();
	}

	/// <summary>
	/// Advances this renderer's animation by <c>Time.Delta</c> and writes the resulting bone
	/// transforms back into the underlying scene model. When this renderer has a
	/// <see cref="BoneMergeTarget"/>, updating the bone proxy GameObjects is skipped here
	/// because the target (the root of the merge chain) will push the merged transforms to all
	/// children during <see cref="PostAnimationUpdate"/>.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> if any bone proxy <see cref="GameObject"/> transform changed
	/// and <see cref="BoneMergeTarget"/> is <see langword="null"/>; otherwise
	/// <see langword="false"/>.
	/// </returns>
	internal bool AnimationUpdate()
	{
		if ( !SceneModel.IsValid() )
			return false;

		SceneModel.Transform = WorldTransform;

		lock ( this )
		{
			// Update physics bones if they exist.
			Physics?.Update();

			if ( Scene.IsEditor && !CanUpdateInEditor() )
			{
				SceneModel.UpdateToBindPose( ReadBonesFromGameObjects );
			}
			else
			{
				SceneModel.Update( Time.Delta, ReadBonesFromGameObjects );
			}
		}

		// Updated bones, transform is no longer dirty.
		_transformDirty = false;

		// Skip if we're bone merged, the target will handle the merge.
		return !_boneMergeTarget.IsValid() && UpdateGameObjectsFromBones();
	}

	bool _transformDirty;

	void OnTransformChanged()
	{
		// Check transform because we could get a false positive.
		_transformDirty = SceneModel.IsValid() && SceneModel.Transform != WorldTransform;
	}

	internal void FinishUpdate()
	{
		// Debug draw physics world if it exists.
		Physics?.DebugDraw();

		if ( !_transformDirty )
			return;

		// Skip if we're bone merged, the target will handle the merge.
		if ( _boneMergeTarget.IsValid() )
			return;

		// Transform changed, make sure bones are updated.
		UpdateTransform( WorldTransform );

		// Updated bones, transform is no longer dirty.
		_transformDirty = false;

		// Update all bone merge children to new transform.
		MergeDescendants();
	}

	void UpdateTransform( Transform transform )
	{
		if ( !SceneModel.IsValid() )
			return;

		SceneModel.Transform = transform;
		ReadBonesFromGameObjects();
		SceneModel.FinishBoneUpdate();
	}

	/// <summary>
	/// For Procedural Bones, copy the current value to the animation bone
	/// </summary>
	void ReadBonesFromGameObjects()
	{
		foreach ( var entry in boneToGameObject )
		{
			if ( !entry.Value.Flags.Contains( GameObjectFlags.ProceduralBone ) )
				continue;

			// Ignore absolute bones, they're probably physics bones
			if ( entry.Value.Flags.Contains( GameObjectFlags.Absolute ) )
				continue;

			var localTransform = entry.Value.LocalTransform;
			if ( localTransform.IsValid )
			{
				SceneModel.SetParentSpaceBone( entry.Key.Index, localTransform );
			}
		}
	}

	/// <summary>
	/// Walks the <see cref="BoneMergeTarget"/> chain upward and returns the renderer at the top
	/// — i.e. the one whose animation drives the entire chain. When this renderer has no target
	/// it returns itself.
	/// </summary>
	private SkinnedModelRenderer RootBoneMergeTarget => BoneMergeTarget.IsValid() ? BoneMergeTarget.RootBoneMergeTarget : this;

	/// <summary>
	/// Copies the current bone and attachment transforms from the underlying scene model into
	/// each bone proxy and attachment proxy <see cref="GameObject"/>. Should be called after
	/// any operation that updates the scene model's bones (animation tick, bone-merge, or
	/// transform change). Returns <see langword="true"/> if at least one proxy transform
	/// changed.
	/// </summary>
	/// <remarks>
	/// <b>Merge offset for root bones</b><br/>
	/// When this renderer is bone-merged, the scene model's bone transforms are in the
	/// coordinate space of the <see cref="RootBoneMergeTarget"/>. Root bones (those without a
	/// parent) are therefore offset by the local transform from this renderer's
	/// <see cref="GameObject"/> to the root-target's <see cref="GameObject"/> so they end up
	/// positioned correctly relative to their proxy parent. Non-root bones are already in
	/// parent-bone space and need no extra offset.
	/// <br/><br/>
	/// <b>Skipped proxies</b><br/>
	/// Bones flagged <see cref="GameObjectFlags.ProceduralBone"/> are skipped because their
	/// transform is written by user code and must not be overwritten by animation. Bones flagged
	/// <see cref="GameObjectFlags.Absolute"/> (physics-driven bones) are also skipped.
	/// </remarks>
	bool UpdateGameObjectsFromBones()
	{
		bool transformsChanged = false;

		var mergeTarget = RootBoneMergeTarget;

		// The offset between our transform and root target.
		Transform? mergeOffset = mergeTarget.IsValid() ? WorldTransform.ToLocal( mergeTarget.WorldTransform ) : default;

		foreach ( var entry in boneToGameObject )
		{
			// Ignore procedural bones, local transform is set manually.
			if ( entry.Value.Flags.Contains( GameObjectFlags.ProceduralBone ) )
				continue;

			// Ignore absolute bones, they're probably physics bones.
			if ( entry.Value.Flags.Contains( GameObjectFlags.Absolute ) )
				continue;

			var transform = SceneModel.GetParentSpaceBone( entry.Key.Index );
			if ( !transform.IsValid )
				continue;

			// Offset root bones to move us to the root target.
			if ( mergeOffset.HasValue && entry.Key.Parent is null )
			{
				transform = mergeOffset.Value.ToWorld( transform );
			}

			transformsChanged |= entry.Value.Transform.SetLocalTransformFast( transform );
		}

		foreach ( var entry in attachmentToGameObject )
		{
			var transform = SceneModel.GetAttachment( entry.Key.Name, false );
			if ( !transform.HasValue )
				continue;

			// Offset root attachments to move us to the root target.
			if ( mergeOffset.HasValue && entry.Key.Bone is null )
			{
				transform = mergeOffset.Value.ToWorld( transform.Value );
			}

			transformsChanged |= entry.Value.Transform.SetLocalTransformFast( transform.Value );
		}

		return transformsChanged;
	}

	/// <summary>
	/// Enumerates all renderers in the bone-merge tree rooted at this renderer, in depth-first
	/// order. Each renderer returned has its <see cref="BoneMergeTarget"/> pointing (directly or
	/// transitively) back to this renderer.
	/// </summary>
	private IEnumerable<SkinnedModelRenderer> GetMergeDescendants()
	{
		foreach ( var child in mergeChildren )
		{
			if ( !child.IsValid() )
				continue;

			yield return child;

			foreach ( var descendant in child.GetMergeDescendants() )
			{
				yield return descendant;
			}
		}
	}

	/// <summary>
	/// Iterates every renderer in the merge tree rooted at this renderer and synchronises each
	/// one with its <see cref="BoneMergeTarget"/>:
	/// <list type="number">
	///   <item>Snaps the descendant's scene-model world transform to the target's.</item>
	///   <item>Calls <c>SceneModel.MergeBones</c> to copy all bone transforms from the target
	///   into the descendant's scene model.</item>
	///   <item>Calls <c>UpdateGameObjectsFromBones</c> to push those bone transforms into the
	///   descendant's bone proxy <see cref="GameObject"/>s (if
	///   <see cref="CreateBoneObjects"/> is enabled on the descendant).</item>
	/// </list>
	/// This method is called once per frame from <see cref="PostAnimationUpdate"/> (or
	/// <see cref="FinishUpdate"/> when only a transform change occurred) on the root renderer of
	/// the merge chain.
	/// </summary>
	private void MergeDescendants()
	{
		if ( mergeChildren.Count == 0 )
			return;

		var descendants = GetMergeDescendants();
		foreach ( var descendant in descendants )
		{
			if ( !descendant.IsValid() )
				continue;

			var so = descendant.SceneModel;
			if ( !so.IsValid() )
				continue;

			var target = descendant.BoneMergeTarget;
			if ( !target.IsValid() )
				continue;

			var parent = target.SceneModel;
			if ( !parent.IsValid() )
				continue;

			so.Transform = parent.Transform;
			so.MergeBones( parent );

			// Updated bones, transform is no longer dirty.
			descendant._transformDirty = false;

			if ( !descendant.UpdateGameObjectsFromBones() )
				continue;

			if ( ThreadSafe.IsMainThread )
			{
				descendant.Transform.TransformChanged();
			}
		}
	}

	public Transform? GetAttachment( string name, bool worldSpace = true )
	{
		return SceneModel?.GetAttachment( name, worldSpace );
	}
}

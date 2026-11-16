using Godot;
using System.Collections.Generic;

/// <summary>
/// Déplace les articulations du squelette selon la morphologie choisie (grand, petit, féminin…), après l'animation.
/// Les blend shapes ne déplacent que le maillage : sans cela, les mains et les pieds ne suivraient pas.
/// </summary>
public partial class BoneOffsets : SkeletonModifier3D
{
	private Vector3[] _offset;
	private Vector3[] _lastWritten;
	private Vector3[] _animBase;
	private bool[] _written;
	private bool _dirty = true;

	/// <summary>Décalage de chaque os, en mètres dans le repère du modèle (nom d'os Godot).</summary>
	public void SetOffsets(Dictionary<string, Vector3> byBone)
	{
		_pending = byBone;
		_dirty = true;
	}

	private Dictionary<string, Vector3> _pending = new();

	private void Prepare(Skeleton3D sk)
	{
		int n = sk.GetBoneCount();
		_offset = new Vector3[n];
		_lastWritten = new Vector3[n];
		_animBase = new Vector3[n];
		_written = new bool[n];
		foreach (var kv in _pending)
		{
			int i = sk.FindBone(kv.Key);
			if (i >= 0) _offset[i] = kv.Value;
		}
		_dirty = false;
	}

	public override void _ProcessModification()
	{
		var sk = GetSkeleton();
		if (sk == null) return;
		if (_dirty || _offset == null || _offset.Length != sk.GetBoneCount()) Prepare(sk);

		for (int i = 0; i < _offset.Length; i++)
		{
			int parent = sk.GetBoneParent(i);
			var parentOffset = parent >= 0 ? _offset[parent] : Vector3.Zero;
			var d = _offset[i] - parentOffset;
			var cur = sk.GetBonePosePosition(i);
			// l'animation a-t-elle réécrit cette position depuis notre dernier passage ? sinon on repart de la valeur d'avant
			if (!_written[i] || !cur.IsEqualApprox(_lastWritten[i])) _animBase[i] = cur;
			var local = d == Vector3.Zero ? Vector3.Zero : (parent >= 0 ? sk.GetBoneGlobalRest(parent).Basis.Inverse() * d : d);
			var next = _animBase[i] + local;
			sk.SetBonePosePosition(i, next);
			_lastWritten[i] = next;
			_written[i] = true;
		}
	}
}

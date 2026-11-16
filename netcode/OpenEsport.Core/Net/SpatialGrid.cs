using System;
using System.Collections.Generic;

namespace OpenEsport.Core
{
	/// <summary>
	/// Découpage de la carte en cases (comme la Replication Graph de Fortnite) : retrouve les entités proches d'un point
	/// sans parcourir tout le monde. Se reconstruit en O(n) par tri par comptage.
	/// </summary>
	public sealed class SpatialGrid
	{
		private readonly float _cell, _originX, _originZ;
		private readonly int _cx, _cz;
		private int[] _cellStart;
		private int[] _items = new int[256];
		private int[] _itemCell = new int[256];
		private int[] _itemId = new int[256];
		private int _count;

		public float CellSize => _cell;

		public SpatialGrid(float minX, float minZ, float maxX, float maxZ, float cellSize)
		{
			_cell = cellSize;
			_originX = minX;
			_originZ = minZ;
			_cx = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize) + 1);
			_cz = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cellSize) + 1);
			_cellStart = new int[_cx * _cz + 1];
		}

		public void Clear() { _count = 0; }

		public void Insert(int id, float x, float z)
		{
			if (_count == _itemId.Length)
			{
				Array.Resize(ref _itemId, _count * 2);
				Array.Resize(ref _itemCell, _count * 2);
				Array.Resize(ref _items, _count * 2);
			}
			_itemId[_count] = id;
			_itemCell[_count] = CellOf(x, z);
			_count++;
		}

		private int CellOf(float x, float z)
		{
			int ix = Math.Clamp((int)((x - _originX) / _cell), 0, _cx - 1);
			int iz = Math.Clamp((int)((z - _originZ) / _cell), 0, _cz - 1);
			return iz * _cx + ix;
		}

		/// <summary>À appeler après les insertions et avant les requêtes.</summary>
		public void Build()
		{
			Array.Clear(_cellStart, 0, _cellStart.Length);
			for (int i = 0; i < _count; i++)
				_cellStart[_itemCell[i] + 1]++;
			for (int c = 0; c < _cx * _cz; c++)
				_cellStart[c + 1] += _cellStart[c];
			// remplissage en réutilisant un curseur par case (_cellStart sert de base, on avance dans _fill)
			if (_fill == null || _fill.Length != _cellStart.Length)
				_fill = new int[_cellStart.Length];
			Array.Copy(_cellStart, _fill, _cellStart.Length);
			for (int i = 0; i < _count; i++)
				_items[_fill[_itemCell[i]]++] = _itemId[i];
		}

		private int[] _fill;

		/// <summary>Ajoute à <paramref name="result"/> les identifiants situés dans les cases qui recouvrent le disque de rayon donné.</summary>
		public void Query(float x, float z, float radius, List<int> result)
		{
			int x0 = Math.Clamp((int)((x - radius - _originX) / _cell), 0, _cx - 1);
			int x1 = Math.Clamp((int)((x + radius - _originX) / _cell), 0, _cx - 1);
			int z0 = Math.Clamp((int)((z - radius - _originZ) / _cell), 0, _cz - 1);
			int z1 = Math.Clamp((int)((z + radius - _originZ) / _cell), 0, _cz - 1);
			for (int iz = z0; iz <= z1; iz++)
				for (int ix = x0; ix <= x1; ix++)
				{
					int c = iz * _cx + ix;
					for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
						result.Add(_items[k]);
				}
		}
	}
}

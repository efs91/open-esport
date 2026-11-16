using System;

namespace OpenEsport.Server
{
	/// <summary>Durées des derniers ticks, pour juger si le serveur tient la cadence (on regarde le pire cas, pas la moyenne).</summary>
	public sealed class TickMetrics
	{
		private readonly double[] _ring = new double[1200];
		private int _next, _count;
		private long _overruns;
		private readonly double _budgetMs;

		public TickMetrics(double budgetMs) { _budgetMs = budgetMs; }

		public long Overruns => _overruns;

		public void Record(double ms)
		{
			_ring[_next] = ms;
			_next = (_next + 1) % _ring.Length;
			if (_count < _ring.Length) _count++;
			if (ms > _budgetMs) _overruns++;
		}

		public (double mean, double p50, double p99, double max) Summary()
		{
			if (_count == 0) return (0, 0, 0, 0);
			var copy = new double[_count];
			Array.Copy(_ring, copy, _count);
			Array.Sort(copy);
			double sum = 0;
			foreach (var v in copy) sum += v;
			return (sum / _count, copy[_count / 2], copy[Math.Min(_count - 1, (int)(_count * 0.99))], copy[_count - 1]);
		}
	}
}

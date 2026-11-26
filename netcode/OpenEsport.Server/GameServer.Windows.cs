using OpenEsport.Core;

namespace OpenEsport.Server
{
	public sealed partial class GameServer
	{
		private ServerEvent WindowEvent(int index)
		{
			var state = _world.Windows[index];
			return new ServerEvent { Type = EventType.WindowChanged, A = (ushort)index, B = state.Health, C = (byte)(state.Reinforced ? 1 : 0) };
		}

		private void SetWindow(int index, WindowState state)
		{
			_world.SetWindow(index, state);
			var change = WindowEvent(index);
			foreach (var player in _humans) SendEvent(player, change);
			SendToSpectators(change);
		}

		private void HitWindow(int index, float damage) => SetWindow(index, _world.Windows[index].Hit(damage));
	}
}

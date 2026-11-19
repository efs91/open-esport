using System;

namespace OpenEsport.Core
{
	public readonly struct WindowState
	{
		public const ushort TargetBase = 32768;
		public const int MaxPanes = 16000;
		public const float Reach = 3f;
		public readonly ushort Health;
		public readonly bool Reinforced;
		public int Capacity => Reinforced ? 500 : 50;
		public static WindowState New => new WindowState(50, false);
		public bool IsNew => !Reinforced && Health == 50;

		public WindowState(int health, bool reinforced)
		{
			Reinforced = reinforced && health > 0;
			Health = (ushort)Math.Clamp(health, 0, Reinforced ? 500 : 50);
		}

		public WindowState Hit(float damage) =>
			new WindowState(Health - Math.Max(1, (int)MathF.Round(damage)), Reinforced);

		public WindowState RestoreOrReinforce() =>
			Health < Capacity ? new WindowState(Capacity, Reinforced) : new WindowState(500, true);

		// Separate repair and reinforce requests so a second player's stale repair cannot upgrade the pane.
		public ushort ActionTarget(int pane) => Health == Capacity && Reinforced ? PlayerInput.NoTarget :
			(ushort)(TargetBase + pane + (Health == Capacity ? MaxPanes : 0));
	}
}

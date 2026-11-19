using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	[Collection("Network")]
	public class WindowTests
	{
		[Fact]
		public void PaneThicknessAndCollisionFollowItsDurability()
		{
			var data = WorldData.CreateFlat(50, 0);
			data.Panes.Add(new Aabb(new Vec3(-2, 1, -2.04f), new Vec3(2, 2, -1.96f)));
			var world = new StaticWorld(data);
			var eye = new Vec3(0, 1.5f, 0);
			var direction = new Vec3(0, 0, -1);
			var reinforced = WindowState.New.RestoreOrReinforce();
			world.SetWindow(0, reinforced);
			Assert.True(world.Raycast(eye, direction, 3, out float distance));
			Assert.Equal(1.92f, distance, 3);
			var damaged = reinforced.Hit(499);
			Assert.Equal(1, damaged.Health);
			Assert.Equal(500, damaged.RestoreOrReinforce().Health);
			var broken = damaged.Hit(1);
			world.SetWindow(0, broken);
			Assert.False(world.Raycast(eye, direction, 3, out _));
			Assert.True(world.Raycast(eye, direction, 3, out _, out int pane, includeBroken: true));
			Assert.Equal(0, pane);
			world.SetWindow(0, broken.RestoreOrReinforce());
			Assert.Equal(50, world.Windows[0].Health);
			Assert.True(world.Raycast(eye, direction, 3, out distance));
			Assert.Equal(1.96f, distance, 3);
		}

		[Fact]
		public void ServerStopsShotsRestoresBrokenPaneAndSyncsReinforcementToLateJoiner()
		{
			var data = WorldData.CreateFlat(100, 0);
			data.Panes.Add(new Aabb(new Vec3(-2, 0.8f, -2.05f), new Vec3(2, 2.6f, -1.95f)));
			using var rig = new Rig(o => o.Match.TeamSize = 1, data);
			var shooter = rig.Connect("Alice");
			var observer = rig.Connect("Bob");
			Assert.True(rig.WaitPlaying(shooter));
			Assert.True(rig.WaitPlaying(observer));
			rig.Teleport(shooter, Vec3.Zero);
			rig.Teleport(observer, new Vec3(0, 0, -5));
			rig.Give(shooter, ItemIds.Ak);
			rig.Run(0.3);

			void Press(Buttons button)
			{
				shooter.Buttons = button;
				rig.Run(0.05);
				shooter.Buttons = Buttons.None;
				rig.Run(0.24);
			}
			ServerEvent Last(Rig.Bot bot) => bot.Events.Last(e => e.Type == EventType.WindowChanged);
			for (int i = 0; i < 10; i++)
			{
				Press(Buttons.Fire);
				if (Last(observer).B == 0) break;
			}
			Assert.Equal(0, Last(observer).B);
			Assert.Equal(0, shooter.HitConfirms);
			Press(Buttons.Fire);
			Assert.True(shooter.HitConfirms > 0);

			shooter.C.InteractTarget = WindowState.TargetBase;
			shooter.Yaw = 3.14f;
			Press(Buttons.Interact);
			Assert.Equal(0, Last(observer).B);
			shooter.Yaw = 0;
			Press(Buttons.Interact);
			Assert.Equal(50, Last(observer).B);
			Press(Buttons.Interact); // stale repair cannot reinforce
			Assert.Equal(50, Last(observer).B);
			shooter.C.InteractTarget = WindowState.New.ActionTarget(0);
			Press(Buttons.Interact);
			Assert.Equal(500, Last(observer).B);
			Assert.Equal(1, Last(observer).C);

			var late = rig.Connect("Clara");
			Assert.True(rig.WaitPlaying(late));
			Assert.True(rig.WaitUntil(() => late.Events.Any(e => e.Type == EventType.WindowChanged), 2));
			Assert.Equal(500, Last(late).B);
			Assert.Equal(1, Last(late).C);
		}
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	/// <summary>Les réglages sont globaux : ces tests s'exécutent avec les autres tests réseau (collection) et remettent les valeurs par défaut.</summary>
	[Collection("Network")]
	public class BalanceTests
	{
		[Fact]
		public void A_Settings_File_Changes_Speeds_And_Weapons_And_Reset_Restores_Defaults()
		{
			try
			{
				var warnings = new List<string>();
				GameBalance.ApplyFileText("// commentaire\n{ \"movement.baseSpeed\": 6.5, \"weapon.ak.damage\": 31, \"inconnu.truc\": 1, }", warnings);
				Assert.Equal(6.5f, Sim.BaseSpeed);
				Assert.Equal(31f, WeaponDefs.Get(ItemIds.Ak).Damage);
				Assert.Contains(warnings, w => w.Contains("inconnu.truc"));
				Assert.Equal(2, GameBalance.ModifiedCount);
			}
			finally { GameBalance.ResetToDefaults(); }
			Assert.Equal(4f, Sim.BaseSpeed);
			Assert.Equal(19f, WeaponDefs.Get(ItemIds.Ak).Damage);
		}

		[Fact]
		public void The_Default_Settings_File_Is_Valid_And_Changes_Nothing()
		{
			GameBalance.ResetToDefaults();
			string text = GameBalance.ToFileText();
			GameBalance.ApplyFileText(text, null);
			Assert.Equal(0, GameBalance.ModifiedCount);
		}

		[Fact]
		public void Server_Values_Travel_To_Clients_In_The_Same_Order()
		{
			try
			{
				GameBalance.ApplyFileText("{ \"movement.sprintMultiplier\": 2.0, \"weapon.smg45.roundsPerMinute\": 1200 }", null);
				var wire = GameBalance.ToWire();
				GameBalance.ResetToDefaults();
				Assert.Equal(1.7f, Sim.SprintMultiplier);
				GameBalance.ApplyWire(wire);
				Assert.Equal(2.0f, Sim.SprintMultiplier);
				Assert.Equal(1200f, WeaponDefs.Get(ItemIds.Smg45).RoundsPerMinute);
			}
			finally { GameBalance.ResetToDefaults(); }
		}

		[Fact]
		public void A_Client_Applies_The_Settings_The_Server_Sends_On_Join()
		{
			try
			{
				GameBalance.ApplyFileText("{ \"movement.baseSpeed\": 5.5 }", null);
				using var rig = new Rig(o => o.Match.TeamSize = 1);
				var wire = GameBalance.ToWire();
				// le client part avec les valeurs par défaut, comme un vrai joueur qui se connecte à un serveur réglé autrement
				GameBalance.ResetToDefaults();
				Assert.Equal(4f, Sim.BaseSpeed);
				GameBalance.ApplyWire(wire); // (le serveur de test partage le processus : on rejoue ce que le client reçoit)
				var a = rig.Connect("Alice");
				Assert.True(rig.WaitPlaying(a));
				Assert.Equal(5.5f, Sim.BaseSpeed);
			}
			finally { GameBalance.ResetToDefaults(); }
		}
	}
}

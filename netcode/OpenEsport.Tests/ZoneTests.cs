using System;
using System.Collections.Generic;
using System.Linq;
using OpenEsport.Core;
using Xunit;

namespace OpenEsport.Tests
{
	public class ZoneTests
	{
		private const float Dt = 1f / 60f;

		private static ZoneRunner Make(ZoneConfig cfg, int seed = 1)
		{
			// carte de 1520 m de côté, centrée sur l'origine
			var z = new ZoneRunner(cfg, -760, 760, -760, 760, seed);
			z.Start();
			return z;
		}

		private static void Run(ZoneRunner z, double seconds)
		{
			int n = (int)Math.Round(seconds * 60.0);
			for (int i = 0; i < n; i++) z.Update(Dt);
		}

		[Fact]
		public void Apex_Starts_With_The_Whole_Map_Then_Draws_Zone_2_Then_Shrinks_Zone_1_Into_It()
		{
			var z = Make(ZonePresets.Apex());
			float full = z.ReferenceRadius;
			Assert.Equal(ZonePhase.Idle, z.Phase);
			z.CurrentCircle(out _, out _, out float r0);
			Assert.Equal(full, r0, 1);                                    // 100 % de la carte (coins compris)
			Assert.Equal(1074.8f, full, 0);                               // demi-diagonale de 1520 m

			Run(z, 119);
			Assert.Equal(ZonePhase.Idle, z.Phase);                        // 2 min : seule la zone 1
			Assert.False(z.NextVisible);
			Run(z, 2);                                                    // t = 121 s : la zone 2 est dessinée
			Assert.Equal(ZonePhase.Warning, z.Phase);
			Assert.True(z.NextVisible);
			z.NextCircle(out float nx, out float nz, out float nr);
			Assert.Equal(full * 0.5f, nr, 1);                             // 50 %
			Assert.True(Math.Sqrt(nx * nx + nz * nz) + nr <= full + 0.01, "la zone 2 est entièrement dans la zone 1");
			z.CurrentCircle(out _, out _, out float rWarn);
			Assert.Equal(full, rWarn, 1);                                 // rien ne bouge pendant l'annonce
			Run(z, 88);                                                   // t = 209 s : 1 min 28 après l'annonce, toujours rien
			Assert.Equal(ZonePhase.Warning, z.Phase);
			Run(z, 3);                                                    // t = 212 s : 1 min 30 après l'annonce : la réduction commence
			Assert.Equal(ZonePhase.Shrinking, z.Phase);
			Run(z, 130);                                                  // à mi-réduction (260 s)
			z.CurrentCircle(out _, out _, out float rMid);
			Assert.InRange(rMid, full * 0.70f, full * 0.80f);
			Run(z, 135);                                                  // fin de la réduction : la zone 2 est la zone actuelle
			Assert.Equal(2, z.Stage);
			z.CurrentCircle(out float cx, out float cz, out float cr);
			Assert.Equal(full * 0.5f, cr, 1);
			Assert.Equal(nx, cx, 1); Assert.Equal(nz, cz, 1);
		}

		[Fact]
		public void Apex_Has_Five_Zones_And_The_Fifth_Shrinks_Until_There_Is_No_Safe_Place()
		{
			var z = Make(ZonePresets.Apex());
			var radii = new List<float>();
			var damages = new List<float>();
			int lastStage = 0;
			double t = 0;
			while (z.Phase != ZonePhase.Collapsed && t < 3600)
			{
				if (z.Stage != lastStage) { lastStage = z.Stage; z.CurrentCircle(out _, out _, out float r); radii.Add(r / z.ReferenceRadius * 100f); damages.Add(z.Damage); }
				Run(z, 1); t += 1;
			}
			Assert.Equal(ZonePhase.Collapsed, z.Phase);
			Assert.Equal(new[] { 100f, 50f, 28f, 14f, 6f }, radii.Select(r => MathF.Round(r)).ToArray());      // 5 zones
			Assert.Equal(new[] { 2f, 4f, 10f, 15f, 20f }, damages.ToArray());                                // dégâts par tick de chaque étape
			Assert.InRange(t, 1100, 1160);                                                                    // ≈ 19 min comme Apex (~20 min)
			Assert.Equal(25f, z.Damage);                                                                       // plus aucune place : dégâts maximaux
			Assert.False(z.Inside(0, 0));
			Assert.False(z.Inside(z.ReferenceRadius * 0.01f, 0));
		}

		[Fact]
		public void Apex_Zones_Are_Always_Inside_The_Previous_One_Whatever_The_Seed()
		{
			for (int seed = 1; seed <= 40; seed++)
			{
				var z = Make(ZonePresets.Apex(), seed);
				z.CurrentCircle(out float px, out float pz, out float pr);
				int stage = 1;
				for (double t = 0; t < 1300 && z.Phase != ZonePhase.Collapsed; t += 0.5)
				{
					if (z.NextVisible)
					{
						z.NextCircle(out float nx, out float nz, out float nr);
						if (z.Phase == ZonePhase.Warning && stage == z.Stage)
						{
							float d = MathF.Sqrt((nx - px) * (nx - px) + (nz - pz) * (nz - pz));
							Assert.True(d + nr <= pr + 0.05f, $"graine {seed}, zone {z.Stage} : la zone suivante sort de la zone actuelle");
							stage++;
							px = nx; pz = nz; pr = nr;
						}
					}
					Run(z, 0.5);
				}
			}
		}

		[Fact]
		public void The_Same_Seed_Gives_The_Same_Zones_And_Another_Seed_Gives_Others()
		{
			float[] Centers(int seed)
			{
				var z = Make(ZonePresets.Apex(), seed);
				var list = new List<float>();
				for (int i = 0; i < 5; i++) { Run(z, 125); z.NextCircle(out float x, out float zz, out _); list.Add(x); list.Add(zz); Run(z, 60); }
				return list.ToArray();
			}
			Assert.Equal(Centers(7), Centers(7));
			Assert.NotEqual(Centers(7), Centers(8));
		}

		[Fact]
		public void Fortnite_Zones_Move_Overlap_Come_Back_And_Go_On_Almost_Forever()
		{
			var cfg = ZonePresets.Fortnite();
			var z = Make(cfg, 3);
			var centers = new List<(float x, float z, float r)>();
			bool outsideOfPrevious = false, revisited = false;
			int stages = 0, last = 0;
			z.CurrentCircle(out float px, out float pz, out float pr);
			for (double t = 0; t < 24 * 3600 && stages < 300; t += 1)
			{
				if (z.Stage != last)
				{
					last = z.Stage; stages++;
					z.CurrentCircle(out float cx, out float cz, out float cr);
					if (centers.Count > 1)
						foreach (var h in centers.Take(centers.Count - 1)) if (Math.Sqrt((h.x - cx) * (h.x - cx) + (h.z - cz) * (h.z - cz)) < 0.3 * cr) revisited = true;
					centers.Add((cx, cz, cr));
				}
				if (z.Phase == ZonePhase.Warning)
				{
					z.NextCircle(out float nx, out float nz, out float nr);
					z.CurrentCircle(out float cx, out float cz, out float cr);
					if (MathF.Sqrt((nx - cx) * (nx - cx) + (nz - cz) * (nz - cz)) + nr > cr + 0.5f) outsideOfPrevious = true;   // ne tient pas dans la précédente : elle se déplace
				}
				Run(z, 1);
				Assert.NotEqual(ZonePhase.Collapsed, z.Phase);
			}
			Assert.True(stages >= 300, $"la zone doit continuer longtemps : {stages} zones");
			Assert.True(outsideOfPrevious, "la zone doit se déplacer et dépasser la précédente");
			Assert.True(revisited, "la zone doit revenir sur un ancien emplacement");
			Assert.True(centers.All(c => c.r >= cfg.CycleMinRadius / 100f * z.ReferenceRadius * 0.99f || c.r > 0), "elle ne se réduit jamais à rien");
		}

		[Fact]
		public void The_Config_Roundtrips_Through_Json_And_Is_Validated()
		{
			var apex = ZonePresets.Apex();
			var back = ZoneConfig.FromJson(apex.ToJson());
			Assert.Equal(apex.Stages.Count, back.Stages.Count);
			Assert.Equal(120f, back.Stages[0].Idle); Assert.Equal(90f, back.Stages[0].Warning); Assert.Equal(50f, back.Stages[0].Radius);
			Assert.Equal("inside", back.Containment);
			Assert.Equal("apex", ZoneConfig.Load("APEX").Name);
			Assert.Equal("fortnite", ZoneConfig.Load("fortnite").Name);
			Assert.Throws<ArgumentException>(() => ZoneConfig.Load("n'existe-pas"));
			Assert.Throws<InvalidOperationException>(() => new ZoneConfig().Validated());                                   // aucune étape
			Assert.Throws<InvalidOperationException>(() => new ZoneConfig { Containment = "ailleurs", Stages = { new ZoneStageConfig { Shrink = 1 } } }.Validated());
			// un réglage minimal écrit à la main
			var mine = ZoneConfig.FromJson(@"{ ""stages"": [ { ""idle"": 10, ""warning"": 5, ""shrink"": 20, ""radius"": 30, ""damage"": 3 } ] }");
			Assert.Equal(30f, mine.Stages[0].Radius);
		}

		[Fact]
		public void The_Network_State_Lets_The_Client_Compute_The_Exact_Circle()
		{
			var z = Make(ZonePresets.Apex(), 5);
			Run(z, 120 + 90 + 100);                                           // en pleine réduction
			uint tick = 123456;
			var st = z.ToState(tick);
			var w = new BitWriter(128); st.Write(w);
			var r = new BitReader(w.Buffer, 0, w.ByteLength);
			Assert.Equal((byte)MsgType.Zone, r.ReadByte());
			var back = ZoneState.Read(r);
			Assert.Equal(ZonePhase.Shrinking, back.Phase);
			back.CircleAt(tick, out float x, out float zz, out float rad);
			z.CurrentCircle(out float ex, out float ez, out float er);
			Assert.Equal(ex, x, 1); Assert.Equal(ez, zz, 1); Assert.Equal(er, rad, 1);
			// 10 s plus tard côté serveur = 600 ticks plus tard côté client
			Run(z, 10);
			back.CircleAt(tick + 600, out x, out zz, out rad);
			z.CurrentCircle(out ex, out ez, out er);
			Assert.Equal(er, rad, 1);
			Assert.InRange(back.SecondsLeft(tick), 150f, 161f);                // 260 - 100 = 160 s
		}
	}
}

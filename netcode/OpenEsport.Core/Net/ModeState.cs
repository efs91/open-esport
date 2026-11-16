using System;

namespace OpenEsport.Core
{
	public struct ControlPointState
	{
		/// <summary>Propriétaire : 0 = bleus, 1 = rouges, 255 = personne.</summary>
		public byte Owner;
		/// <summary>Avancement de la capture : −100 (tout bleu) à +100 (tout rouge).</summary>
		public sbyte Progress;
		/// <summary>Les deux équipes sont dessus : la capture est bloquée.</summary>
		public bool Contested;
	}

	public struct FlagState
	{
		/// <summary>0 = à sa base, 1 = porté, 2 = tombé au sol.</summary>
		public byte State;
		public ushort Carrier;
		public Vec3 Pos;
	}

	/// <summary>Étape d'une manche de Sabotage.</summary>
	public enum SabotageStage : byte { Buy = 0, Round = 1, Planted = 2, RoundEnd = 3 }

	/// <summary>État de la partie propre au mode de jeu (scores, minuteur, points, drapeaux, bombe, argent), envoyé à chaque joueur plusieurs fois par seconde.</summary>
	public struct ModeState
	{
		public GameMode Mode;
		/// <summary>Sabotage : étape de la manche (<see cref="SabotageStage"/>) ; mi-temps = 4 avec Round pair…</summary>
		public byte Stage;
		/// <summary>Mon camp : équipe 0 (bleue) ou 1 (rouge). En Sabotage, 0 = défenseurs, 1 = attaquants.</summary>
		public byte MySide;
		public ushort Score0, Score1;
		public ushort Target, TimeLeft;
		public byte Round, RoundWinner, WinnerTeam;
		/// <summary>Deathmatch : mes éliminations et le meilleur score.</summary>
		public ushort MyScore, TopScore;
		public ushort Money;
		public byte ClassId;
		public bool HasKit, CanBuy, Invulnerable;
		public ControlPointState[] Points;
		public FlagState Flag0, Flag1;
		public byte BombState;        // 0 = portée, 1 = au sol, 2 = posée, 3 = désamorcée, 4 = explosée
		public ushort BombCarrier;
		public Vec3 BombPos;
		public float BombTimer;
		public byte BombSite;
		public byte Action, ActionProgress;   // 1 = pose en cours, 2 = désamorçage ; avancement 0-100

		public static ModeState Empty => new ModeState { Points = new ControlPointState[5], WinnerTeam = 255, RoundWinner = 255 };

		public void Write(BitWriter w)
		{
			w.WriteByte((byte)MsgType.ModeState);
			w.WriteByte((byte)Mode); w.WriteByte(Stage); w.WriteByte(MySide);
			w.WriteUInt16(Score0); w.WriteUInt16(Score1); w.WriteUInt16(Target); w.WriteUInt16(TimeLeft);
			w.WriteByte(Round); w.WriteByte(RoundWinner); w.WriteByte(WinnerTeam);
			w.WriteUInt16(MyScore); w.WriteUInt16(TopScore); w.WriteUInt16(Money); w.WriteByte(ClassId);
			w.WriteBool(HasKit); w.WriteBool(CanBuy); w.WriteBool(Invulnerable);
			for (int i = 0; i < 5; i++)
			{
				var c = Points != null && i < Points.Length ? Points[i] : new ControlPointState { Owner = 255 };
				w.WriteByte(c.Owner); w.WriteSByte(c.Progress); w.WriteBool(c.Contested);
			}
			WriteFlag(w, Flag0); WriteFlag(w, Flag1);
			w.WriteByte(BombState); w.WriteUInt16(BombCarrier); w.WriteVec3(BombPos); w.WriteFloat(BombTimer); w.WriteByte(BombSite);
			w.WriteByte(Action); w.WriteByte(ActionProgress);
		}

		private static void WriteFlag(BitWriter w, FlagState f) { w.WriteByte(f.State); w.WriteUInt16(f.Carrier); w.WriteVec3(f.Pos); }
		private static FlagState ReadFlag(BitReader r) => new FlagState { State = r.ReadByte(), Carrier = r.ReadUInt16(), Pos = r.ReadVec3() };

		/// <summary>Lit un message (l'octet de type est déjà lu).</summary>
		public static ModeState Read(BitReader r)
		{
			var s = Empty;
			s.Mode = (GameMode)r.ReadByte(); s.Stage = r.ReadByte(); s.MySide = r.ReadByte();
			s.Score0 = r.ReadUInt16(); s.Score1 = r.ReadUInt16(); s.Target = r.ReadUInt16(); s.TimeLeft = r.ReadUInt16();
			s.Round = r.ReadByte(); s.RoundWinner = r.ReadByte(); s.WinnerTeam = r.ReadByte();
			s.MyScore = r.ReadUInt16(); s.TopScore = r.ReadUInt16(); s.Money = r.ReadUInt16(); s.ClassId = r.ReadByte();
			s.HasKit = r.ReadBool(); s.CanBuy = r.ReadBool(); s.Invulnerable = r.ReadBool();
			for (int i = 0; i < 5; i++) s.Points[i] = new ControlPointState { Owner = r.ReadByte(), Progress = r.ReadSByte(), Contested = r.ReadBool() };
			s.Flag0 = ReadFlag(r); s.Flag1 = ReadFlag(r);
			s.BombState = r.ReadByte(); s.BombCarrier = r.ReadUInt16(); s.BombPos = r.ReadVec3(); s.BombTimer = r.ReadFloat(); s.BombSite = r.ReadByte();
			s.Action = r.ReadByte(); s.ActionProgress = r.ReadByte();
			return s;
		}
	}
}

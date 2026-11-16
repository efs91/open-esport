using Godot;

namespace Openesport.Online
{
	/// <summary>Sons du jeu en ligne : pas, sauts, atterrissages et coups de feu des autres joueurs, tous positionnés dans l'espace (3D).</summary>
	public static class GameAudio
	{
		private const string StepDir = "res://assets/sounds/foosteps/conctrete/";
		private static AudioStream[] _jog, _jump, _land;
		private static AudioStreamWav _shot, _shotLight;

		private static AudioStream[] Load(string prefix, int count)
		{
			var list = new System.Collections.Generic.List<AudioStream>();
			for (int i = 1; i <= count; i++)
			{
				var s = GD.Load<AudioStream>($"{StepDir}{prefix}{i}.WAV");
				if (s != null) list.Add(s);
			}
			return list.ToArray();
		}

		private static void Init()
		{
			if (_jog != null) return;
			using var _trace = Openesport.Diagnostics.LagTracer.Scope("GameAudio.Init (chargement des sons)");
			_jog = Load("Footstep_Concrete_Boots_Jog_", 8);
			_jump = Load("Footstep_Concrete_Boots_Jump_", 4);
			_land = Load("Footstep_Concrete_Boots_Land_", 4);
			_shot = Synthesize(0.42f, 70f, 16f, 1f);
			_shotLight = Synthesize(0.28f, 130f, 24f, 0.8f);
		}

		/// <summary>Coup de feu synthétisé : claquement (bruit) + grondement grave qui s'éteint.</summary>
		private static AudioStreamWav Synthesize(float seconds, float thumpHz, float decay, float gain)
		{
			const int rate = 32000;
			int n = (int)(seconds * rate);
			var data = new byte[n * 2];
			var rng = new System.Random(5);
			float lp = 0f;
			for (int i = 0; i < n; i++)
			{
				float t = i / (float)rate;
				float noise = (float)(rng.NextDouble() * 2 - 1);
				lp += (noise - lp) * 0.35f;                       // bruit filtré : le « souffle »
				float crack = (noise - lp) * Mathf.Exp(-t * 90f); // front aigu très bref
				float body = lp * Mathf.Exp(-t * decay);
				float thump = Mathf.Sin(Mathf.Tau * thumpHz * t * (1f - 0.4f * t)) * Mathf.Exp(-t * decay * 0.8f);
				float v = Mathf.Clamp((crack * 0.9f + body * 1.1f + thump * 1.2f) * 0.6f * gain, -1f, 1f);
				short s16 = (short)(v * 32000);
				data[i * 2] = (byte)(s16 & 0xFF);
				data[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
			}
			return new AudioStreamWav { Data = data, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false };
		}

		private static readonly System.Random Rng = new System.Random();
		private static AudioStream Pick(AudioStream[] list) => list.Length == 0 ? null : list[Rng.Next(list.Length)];

		/// <summary>Joue un son 3D qui se supprime tout seul. maxDistance : au-delà, on n'entend plus rien.</summary>
		public static void PlayAt(Node parent, AudioStream stream, Vector3 position, float volumeDb, float maxDistance, float unitSize, float pitch = 1f)
		{
			if (stream == null || parent == null || !GodotObject.IsInstanceValid(parent)) return;
			var p = new AudioStreamPlayer3D
			{
				Stream = stream,
				VolumeDb = volumeDb,
				MaxDistance = maxDistance,
				UnitSize = unitSize,
				AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
				PitchScale = pitch,
				Bus = "Master"
			};
			parent.AddChild(p);
			p.GlobalPosition = position;
			p.Finished += p.QueueFree;
			p.Play();
		}

		public static void Step(Node parent, Vector3 pos, bool sprint)
		{
			Init();
			PlayAt(parent, Pick(_jog), pos, sprint ? 2f : -1f, 55f, 6f, 0.95f + (float)Rng.NextDouble() * 0.12f);
		}

		public static void Jump(Node parent, Vector3 pos) { Init(); PlayAt(parent, Pick(_jump), pos, 0f, 55f, 6f); }
		public static void Land(Node parent, Vector3 pos) { Init(); PlayAt(parent, Pick(_land), pos, 2f, 60f, 7f); }

		/// <summary>Coup de feu : audible de loin (250 m), très fort de près.</summary>
		public static void Shot(Node parent, Vector3 pos, bool light, bool local)
		{
			Init();
			PlayAt(parent, light ? _shotLight : _shot, pos, local ? 0f : 8f, 260f, 18f, 0.92f + (float)Rng.NextDouble() * 0.16f);
		}
	}
}

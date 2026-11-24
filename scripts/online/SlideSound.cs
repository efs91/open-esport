using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Bruit de glissade : le début du frottement, puis le milieu en boucle tant que dure la glissade, et la fin quand elle s'arrête.
	/// Sons tirés de assets/sounds/glissade/slide.wav : slide_loop.wav (attaque puis boucle de 0,8 s à partir de l'échantillon
	/// <see cref="LoopBegin"/>) et slide_end.wav (fin). Positionné sur un joueur distant, sans position pour le joueur local.
	/// </summary>
	public partial class SlideSound : Node3D
	{
		private const string LoopPath = "res://assets/sounds/glissade/slide_loop.wav";
		private const string EndPath = "res://assets/sounds/glissade/slide_end.wav";
		/// <summary>Fin de l'attaque, début de la boucle (échantillons à 48 kHz) ; la boucle va jusqu'à la fin du fichier.</summary>
		private const int LoopBegin = 4800;

		private static AudioStream _loop, _end;

		/// <summary>Faux : son entendu « dans la tête » (joueur local) ; vrai : son 3D à la position du nœud.</summary>
		public bool Positional = true;
		public float VolumeDb = 0f;

		private AudioStreamPlayer _flat;
		private AudioStreamPlayer3D _spatial;
		private bool _on;

		private static void Init()
		{
			if (_loop != null) return;
			if (GD.Load<AudioStream>(LoopPath) is AudioStreamWav wav)
			{
				var w = (AudioStreamWav)wav.Duplicate();
				w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
				w.LoopBegin = LoopBegin;
				w.LoopEnd = (int)System.Math.Round(w.GetLength() * w.MixRate);      // jusqu'à la fin du fichier
				_loop = w;
			}
			_end = GD.Load<AudioStream>(EndPath);
		}

		public override void _Ready()
		{
			Init();
			if (Positional)
			{
				_spatial = new AudioStreamPlayer3D { Stream = _loop, VolumeDb = VolumeDb, MaxDistance = 45f, UnitSize = 6f, Bus = "Master" };
				AddChild(_spatial);
			}
			else
			{
				_flat = new AudioStreamPlayer { Stream = _loop, VolumeDb = VolumeDb, Bus = "Master" };
				AddChild(_flat);
			}
		}

		/// <summary>À chaque image : glisse-t-on ? Démarre la boucle, ou l'arrête et joue la fin.</summary>
		public void Set(bool sliding)
		{
			if (sliding == _on || _loop == null) return;
			_on = sliding;
			if (sliding)
			{
				if (_spatial != null) _spatial.Play(); else _flat?.Play();
				return;
			}
			_spatial?.Stop(); _flat?.Stop();
			if (_end == null) return;
			if (Positional) GameAudio.PlayAt(GetParent(), _end, GlobalPosition, VolumeDb, 45f, 6f);
			else
			{
				var p = new AudioStreamPlayer { Stream = _end, VolumeDb = VolumeDb, Bus = "Master" };
				AddChild(p);
				p.Finished += p.QueueFree;
				p.Play();
			}
		}
	}
}

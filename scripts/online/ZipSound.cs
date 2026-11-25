using Godot;

namespace Openesport.Online
{
	/// <summary>
	/// Bruits de tyrolienne : l'accroche, puis (une fois l'accroche finie) le roulement en boucle tant qu'on est sur le câble, et le décrochage.
	/// Positionné sur un joueur distant (audible à 100 m), sans position pour le joueur local.
	/// </summary>
	public partial class ZipSound : Node3D
	{
		private const string AttachPath = "res://assets/sounds/tirolienne/attach2.mp3";
		private const string LoopPath = "res://assets/sounds/tirolienne/en cours de tiro.mp3";
		private const string DetachPath = "res://assets/sounds/tirolienne/detach.mp3";
		/// <summary>L'enregistrement commence très bas : on entre dans le roulement à cet instant (s).</summary>
		private const float LoopFrom = 6f;
		private const float MaxDistance = 100f, UnitSize = 10f;
		/// <summary>Le roulement est enregistré environ 20 dB plus bas que l'accroche et le décrochage.</summary>
		private const float LoopDb = 6f, ClickDb = -5f, FadeSeconds = 0.15f;

		private static AudioStream _attach, _loop, _detach;

		/// <summary>Faux : son entendu « dans la tête » (joueur local) ; vrai : son 3D à la position du nœud.</summary>
		public bool Positional = true;

		private AudioStreamPlayer _flat;
		private AudioStreamPlayer3D _spatial;
		private Tween _fade;
		private bool _on;
		/// <summary>Temps restant (s) avant le début du roulement ; négatif : rien à lancer.</summary>
		private double _loopIn = -1;

		private static void Init()
		{
			if (_loop != null) return;
			_attach = GD.Load<AudioStream>(AttachPath);
			_loop = GameAudio.Looping(LoopPath);
			_detach = GD.Load<AudioStream>(DetachPath);
		}

		public override void _Ready()
		{
			Init();
			if (Positional)
			{
				_spatial = new AudioStreamPlayer3D { Stream = _loop, VolumeDb = LoopDb, MaxDistance = MaxDistance, UnitSize = UnitSize, Bus = "Master" };
				AddChild(_spatial);
			}
			else
			{
				_flat = new AudioStreamPlayer { Stream = _loop, VolumeDb = LoopDb, Bus = "Master" };
				AddChild(_flat);
			}
		}

		/// <summary>À chaque image : est-on sur un câble ? <paramref name="clicks"/> faux : ni accroche ni décrochage (joueur qu'on vient de voir apparaître, mort).</summary>
		public void Set(bool zipping, bool clicks = true)
		{
			if (zipping == _on) return;
			_on = zipping;
			_fade?.Kill();
			if (zipping)
			{
				_spatial?.Stop(); _flat?.Stop();
				if (clicks) Click(_attach);
				_loopIn = clicks && _attach != null ? _attach.GetLength() : 0;
				return;
			}
			_loopIn = -1;
			if (clicks) Click(_detach);
			Node player = _spatial != null ? _spatial : _flat;
			if (player == null) return;
			_fade = CreateTween();
			_fade.TweenProperty(player, "volume_db", -60f, FadeSeconds);
			_fade.TweenCallback(Callable.From(() => { _spatial?.Stop(); _flat?.Stop(); }));
		}

		public override void _Process(double delta)
		{
			if (_loopIn < 0) return;
			_loopIn -= delta;
			if (_loopIn > 0) return;
			_loopIn = -1;
			if (_spatial != null) { _spatial.VolumeDb = LoopDb; _spatial.Play(LoopFrom); }
			else if (_flat != null) { _flat.VolumeDb = LoopDb; _flat.Play(LoopFrom); }
		}

		private void Click(AudioStream stream)
		{
			if (stream == null) return;
			if (Positional) { GameAudio.PlayAt(GetParent(), stream, GlobalPosition, ClickDb + 5f, MaxDistance, UnitSize); return; }
			var p = new AudioStreamPlayer { Stream = stream, VolumeDb = ClickDb, Bus = "Master" };
			AddChild(p);
			p.Finished += p.QueueFree;
			p.Play();
		}
	}
}

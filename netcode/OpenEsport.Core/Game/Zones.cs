using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEsport.Core
{
	/// <summary>
	/// Une étape de la zone : la zone actuelle reste seule <see cref="Idle"/> secondes, puis la zone suivante est dessinée sur la carte (rien ne bouge)
	/// pendant <see cref="Warning"/> secondes, enfin la zone actuelle se réduit jusqu'à la suivante en <see cref="Shrink"/> secondes.
	/// </summary>
	public sealed class ZoneStageConfig
	{
		/// <summary>Seconds avant que la zone suivante soit annoncée (la zone actuelle est seule sur la carte).</summary>
		public float Idle { get; set; }
		/// <summary>Secondes entre l'annonce de la zone suivante et le début de la réduction.</summary>
		public float Warning { get; set; }
		/// <summary>Durée de la réduction (secondes) : la zone actuelle devient la zone suivante, centre et rayon en même temps.</summary>
		public float Shrink { get; set; }
		/// <summary>Rayon de la zone suivante, en % du rayon de référence. 0 = la zone se réduit jusqu'à disparaître.</summary>
		public float Radius { get; set; }
		/// <summary>Dégâts par tick pour qui est hors de la zone pendant cette étape.</summary>
		public float Damage { get; set; }
	}

	/// <summary>
	/// Réglage complet des zones d'une partie. Tout est configurable : le nombre d'étapes, les durées, les tailles, la façon dont la zone suivante se
	/// place (toujours dans la zone actuelle comme Apex Legends, ou qui se déplace, chevauche et revient sur d'anciens emplacements comme Fortnite),
	/// et une phase cyclique qui prolonge la partie presque indéfiniment. Se charge et s'enregistre en JSON (<see cref="FromJson"/>, <see cref="ToJson"/>).
	/// </summary>
	public sealed class ZoneConfig
	{
		public string Name { get; set; } = "apex";

		/// <summary>Rayon de référence (100 %) : « diagonal » = la moitié de la diagonale de la carte (la première zone couvre toute la carte, coins compris),
		/// « half-width » = la moitié du côté (la première zone est le cercle inscrit).</summary>
		public string Reference { get; set; } = "diagonal";
		/// <summary>Rayon de la première zone, en % du rayon de référence.</summary>
		public float FirstRadius { get; set; } = 100f;
		/// <summary>Intervalle (s) entre deux coups de dégâts hors de la zone.</summary>
		public float TickSeconds { get; set; } = 1.5f;
		/// <summary>Dégâts par tick une fois la zone entièrement refermée (plus aucune place nulle part).</summary>
		public float FinalDamage { get; set; } = 25f;
		/// <summary>Vrai : les dégâts de zone passent à travers le bouclier (comme Apex).</summary>
		public bool IgnoreShield { get; set; } = true;

		/// <summary>Placement de la zone suivante : « inside » = entièrement dans la zone actuelle (Apex) ; « overlap » = elle se déplace et chevauche la zone actuelle
		/// (Fortnite) ; « free » = n'importe où sur la carte, y compris loin ou sur une zone déjà passée.</summary>
		public string Containment { get; set; } = "inside";
		/// <summary>Modes « overlap » : décalage maximal du centre, en fraction du rayon actuel.</summary>
		public float MaxShift { get; set; } = 0.8f;
		/// <summary>Chance (0 à 1) que la zone suivante soit recentrée sur un ancien emplacement : la zone revient sur ses pas.</summary>
		public float RevisitChance { get; set; }

		/// <summary>Les étapes, dans l'ordre. L'étape i fait passer de la zone i à la zone i+1.</summary>
		public List<ZoneStageConfig> Stages { get; set; } = new List<ZoneStageConfig>();

		/// <summary>Phase cyclique : une fois les étapes épuisées (et si la dernière zone n'est pas réduite à rien), la zone continue de bouger indéfiniment.</summary>
		public bool Cycle { get; set; }
		public float CycleWarning { get; set; } = 20f;
		public float CycleShrink { get; set; } = 40f;
		public float CycleDamage { get; set; } = 10f;
		/// <summary>À chaque cycle le rayon est multiplié par ce facteur ; sous <see cref="CycleMinRadius"/> il revient à <see cref="CycleResetRadius"/> (en % du rayon de référence).</summary>
		public float CycleRatio { get; set; } = 0.85f;
		public float CycleMinRadius { get; set; } = 8f;
		public float CycleResetRadius { get; set; } = 35f;

		public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never
		};

		public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
		public static ZoneConfig FromJson(string json) => JsonSerializer.Deserialize<ZoneConfig>(json, JsonOptions) ?? throw new InvalidOperationException("JSON de zones vide");

		/// <summary>Un préréglage par son nom (« apex », « fortnite »), ou un fichier JSON.</summary>
		public static ZoneConfig Load(string nameOrPath)
		{
			if (string.IsNullOrWhiteSpace(nameOrPath)) return ZonePresets.Apex();
			switch (nameOrPath.Trim().ToLowerInvariant())
			{
				case "apex": return ZonePresets.Apex();
				case "fortnite": return ZonePresets.Fortnite();
			}
			if (System.IO.File.Exists(nameOrPath)) return FromJson(System.IO.File.ReadAllText(nameOrPath));
			throw new ArgumentException($"Préréglage de zones inconnu : « {nameOrPath} » (apex, fortnite ou un fichier .json)");
		}

		/// <summary>Vérifie la cohérence du réglage et lève une exception explicite sinon.</summary>
		public ZoneConfig Validated()
		{
			if (Stages.Count == 0 && !Cycle) throw new InvalidOperationException("Au moins une étape (ou le mode cyclique) est nécessaire");
			if (FirstRadius <= 0f) throw new InvalidOperationException("FirstRadius doit être positif");
			if (TickSeconds <= 0.05f) TickSeconds = 0.05f;
			foreach (var s in Stages)
			{
				if (s.Idle < 0f || s.Warning < 0f || s.Shrink <= 0f) throw new InvalidOperationException("Durées d'étape invalides (Shrink doit être > 0)");
				if (s.Radius < 0f) throw new InvalidOperationException("Rayon d'étape négatif");
			}
			if (Containment != "inside" && Containment != "overlap" && Containment != "free") throw new InvalidOperationException("Containment : inside, overlap ou free");
			if (Reference != "diagonal" && Reference != "half-width") throw new InvalidOperationException("Reference : diagonal ou half-width");
			return this;
		}
	}

	/// <summary>
	/// Préréglages. « apex » : 5 zones comme Apex Legends (la zone 1 couvre la carte, chacune est à peu près moitié moins grande, la zone 5 se réduit jusqu'à
	/// ne plus laisser aucune place), avec les temps d'attente, durées de réduction et dégâts par tick des cercles d'Apex (saison 18 et suivantes).
	/// « fortnite » : une zone qui se déplace, chevauche la précédente, revient parfois sur ses pas et continue presque indéfiniment.
	/// </summary>
	public static class ZonePresets
	{
		public static ZoneConfig Apex() => new ZoneConfig
		{
			Name = "apex", Reference = "diagonal", FirstRadius = 100f, TickSeconds = 1.5f, FinalDamage = 25f, IgnoreShield = true, Containment = "inside",
			Stages =
			{
				// zone 1 (100 %) pendant 2 min, puis la zone 2 (50 %) est dessinée et, 1 min 30 plus tard, la zone 1 se réduit jusqu'à elle
				new ZoneStageConfig { Idle = 120f, Warning = 90f,  Shrink = 260f, Radius = 50f, Damage = 2f },
				new ZoneStageConfig { Idle = 0f,   Warning = 120f, Shrink = 70f,  Radius = 28f, Damage = 4f },
				new ZoneStageConfig { Idle = 0f,   Warning = 90f,  Shrink = 45f,  Radius = 14f, Damage = 10f },
				new ZoneStageConfig { Idle = 0f,   Warning = 90f,  Shrink = 50f,  Radius = 6f,  Damage = 15f },
				// zone 5 : elle se réduit jusqu'à disparaître (plus aucune place hors zone)
				new ZoneStageConfig { Idle = 0f,   Warning = 75f,  Shrink = 120f, Radius = 0f,  Damage = 20f },
			}
		};

		public static ZoneConfig Fortnite() => new ZoneConfig
		{
			Name = "fortnite", Reference = "diagonal", FirstRadius = 100f, TickSeconds = 1f, FinalDamage = 10f, IgnoreShield = false, Containment = "overlap",
			MaxShift = 0.9f, RevisitChance = 0.25f,
			Stages =
			{
				new ZoneStageConfig { Idle = 60f, Warning = 60f, Shrink = 90f, Radius = 62f, Damage = 1f },
				new ZoneStageConfig { Idle = 0f,  Warning = 45f, Shrink = 75f, Radius = 42f, Damage = 1f },
				new ZoneStageConfig { Idle = 0f,  Warning = 40f, Shrink = 60f, Radius = 30f, Damage = 2f },
				new ZoneStageConfig { Idle = 0f,  Warning = 35f, Shrink = 55f, Radius = 22f, Damage = 5f },
			},
			// ensuite la zone respire et se promène sans fin : elle rétrécit de 15 % à chaque cycle, puis repart d'un rayon plus large
			Cycle = true, CycleWarning = 25f, CycleShrink = 45f, CycleDamage = 8f, CycleRatio = 0.85f, CycleMinRadius = 9f, CycleResetRadius = 34f
		};
	}

	public enum ZonePhase : byte
	{
		/// <summary>Pas encore commencée.</summary>
		None = 0,
		/// <summary>La zone actuelle est seule sur la carte.</summary>
		Idle = 1,
		/// <summary>La zone suivante est dessinée ; rien ne bouge.</summary>
		Warning = 2,
		/// <summary>La zone actuelle se réduit vers la zone suivante.</summary>
		Shrinking = 3,
		/// <summary>La zone est entièrement refermée : plus aucune place hors dégâts.</summary>
		Collapsed = 4,
	}

	/// <summary>
	/// État de la zone tel qu'il circule sur le réseau : suffisant pour que le client calcule le cercle exact à chaque image (la réduction est déterministe :
	/// interpolation entre le cercle de départ et le cercle suivant entre deux ticks du serveur).
	/// </summary>
	public struct ZoneState
	{
		public ZonePhase Phase;
		/// <summary>Numéro de la zone actuelle (1 = la première).</summary>
		public byte Stage;
		/// <summary>Tick du serveur où la phase actuelle a commencé, et sa durée en ticks.</summary>
		public uint StartTick, DurationTicks;
		public float CurX, CurZ, CurRadius;
		public float NextX, NextZ, NextRadius;
		/// <summary>Dégâts par tick hors de la zone, et intervalle des ticks (s).</summary>
		public float Damage, TickSeconds;
		public byte TickRate;
		/// <summary>Durées de l'annonce et de la réduction de l'étape en cours (s) : le client en déduit « rétrécissement dans… » et « fin du rétrécissement dans… ».</summary>
		public float WarningSeconds, ShrinkSeconds;

		public bool NextVisible => Phase == ZonePhase.Warning || Phase == ZonePhase.Shrinking;

		/// <summary>Le cercle sûr à l'instant <paramref name="serverTick"/> (fractionnaire) : fixe, ou en cours de réduction.</summary>
		public void CircleAt(float serverTick, out float x, out float z, out float r)
		{
			if (Phase == ZonePhase.Shrinking)
			{
				float t = DurationTicks == 0 ? 1f : Math.Clamp((serverTick - StartTick) / DurationTicks, 0f, 1f);
				x = CurX + (NextX - CurX) * t; z = CurZ + (NextZ - CurZ) * t; r = CurRadius + (NextRadius - CurRadius) * t;
				return;
			}
			if (Phase == ZonePhase.Collapsed) { x = NextX; z = NextZ; r = 0f; return; }
			x = CurX; z = CurZ; r = CurRadius;
		}

		/// <summary>Secondes restantes dans la phase actuelle.</summary>
		public float SecondsLeft(float serverTick)
		{
			if (TickRate == 0 || Phase == ZonePhase.Collapsed || Phase == ZonePhase.None) return 0f;
			return Math.Max(0f, (StartTick + DurationTicks - serverTick) / TickRate);
		}

		public void Write(BitWriter w)
		{
			w.WriteByte((byte)MsgType.Zone);
			w.WriteByte((byte)Phase); w.WriteByte(Stage); w.WriteUInt32(StartTick); w.WriteUInt32(DurationTicks);
			w.WriteFloat(CurX); w.WriteFloat(CurZ); w.WriteFloat(CurRadius);
			w.WriteFloat(NextX); w.WriteFloat(NextZ); w.WriteFloat(NextRadius);
			w.WriteFloat(Damage); w.WriteFloat(TickSeconds); w.WriteByte(TickRate); w.WriteFloat(WarningSeconds); w.WriteFloat(ShrinkSeconds);
		}

		/// <summary>Lit un message (l'octet de type est déjà lu).</summary>
		public static ZoneState Read(BitReader r)
		{
			var s = new ZoneState { Phase = (ZonePhase)r.ReadByte(), Stage = r.ReadByte(), StartTick = r.ReadUInt32(), DurationTicks = r.ReadUInt32() };
			s.CurX = r.ReadFloat(); s.CurZ = r.ReadFloat(); s.CurRadius = r.ReadFloat();
			s.NextX = r.ReadFloat(); s.NextZ = r.ReadFloat(); s.NextRadius = r.ReadFloat();
			s.Damage = r.ReadFloat(); s.TickSeconds = r.ReadFloat(); s.TickRate = r.ReadByte(); s.WarningSeconds = r.ReadFloat(); s.ShrinkSeconds = r.ReadFloat();
			return s;
		}
	}

	/// <summary>
	/// Déroule les zones d'une partie : tire les cercles successifs d'après le réglage et une graine (deux serveurs avec la même graine donnent les mêmes zones),
	/// et fait avancer les phases (seul, annonce, réduction, fermeture). Indépendant du moteur : le serveur l'utilise, les tests aussi.
	/// </summary>
	public sealed class ZoneRunner
	{
		private readonly ZoneConfig _cfg;
		private readonly Random _rng;
		private readonly float _minX, _maxX, _minZ, _maxZ, _refRadius;
		private readonly int _tickRate;

		private int _stageIndex;           // étape en cours (0 = zone 1 -> zone 2)
		private ZoneStageConfig _stage;
		private float _timer;              // temps écoulé dans la phase
		private float _cx, _cz, _r;        // cercle de départ de l'étape
		private float _nx, _nz, _nr;       // cercle suivant
		private float _lastCyclePercent;
		private readonly List<(float x, float z)> _history = new List<(float, float)>();

		public ZonePhase Phase { get; private set; } = ZonePhase.None;
		/// <summary>Numéro de la zone actuelle (1 = la première).</summary>
		public int Stage => _stageIndex + 1;
		public ZoneConfig Config => _cfg;
		public float ReferenceRadius => _refRadius;
		/// <summary>Vrai quand la phase ou le cercle suivant vient de changer (à diffuser aux joueurs).</summary>
		public bool Changed { get; private set; }

		public ZoneRunner(ZoneConfig config, float minX, float maxX, float minZ, float maxZ, int seed, int tickRate = 60)
		{
			_cfg = config.Validated();
			_minX = minX; _maxX = maxX; _minZ = minZ; _maxZ = maxZ;
			_tickRate = tickRate;
			_rng = new Random(seed);
			float w = maxX - minX, d = maxZ - minZ;
			_refRadius = _cfg.Reference == "diagonal" ? 0.5f * MathF.Sqrt(w * w + d * d) : 0.5f * MathF.Min(w, d);
			_cx = (minX + maxX) * 0.5f; _cz = (minZ + maxZ) * 0.5f;
			_r = _refRadius * _cfg.FirstRadius / 100f;
			_history.Add((_cx, _cz));
			_lastCyclePercent = _cfg.FirstRadius;
		}

		/// <summary>Démarre la première étape (à appeler au départ de la partie).</summary>
		public void Start()
		{
			_stageIndex = 0;
			BeginStage();
		}

		private ZoneStageConfig StageConfig(int index)
		{
			if (index < _cfg.Stages.Count) return _cfg.Stages[index];
			// au-delà des étapes réglées : phase cyclique (la zone respire et se promène)
			float basePercent = _cfg.Stages.Count > 0 ? _cfg.Stages[_cfg.Stages.Count - 1].Radius : _cfg.FirstRadius;
			if (index == _cfg.Stages.Count) _lastCyclePercent = basePercent;
			float next = _lastCyclePercent * _cfg.CycleRatio;
			if (next < _cfg.CycleMinRadius) next = _cfg.CycleResetRadius;
			_lastCyclePercent = next;
			return new ZoneStageConfig { Idle = 0f, Warning = _cfg.CycleWarning, Shrink = _cfg.CycleShrink, Radius = next, Damage = _cfg.CycleDamage };
		}

		private void BeginStage()
		{
			_stage = StageConfig(_stageIndex);
			PickNext();
			_timer = 0f;
			Phase = _stage.Idle > 0f ? ZonePhase.Idle : ZonePhase.Warning;
			if (Phase == ZonePhase.Warning && _stage.Warning <= 0f) Phase = ZonePhase.Shrinking;
			Changed = true;
		}

		/// <summary>Tire le cercle suivant selon le mode de placement.</summary>
		private void PickNext()
		{
			_nr = _refRadius * _stage.Radius / 100f;
			if (_nr <= 0f)
			{
				// disparition : la zone se referme sur son centre
				_nx = _cx; _nz = _cz; _nr = 0f;
				return;
			}
			float x, z;
			if (_cfg.RevisitChance > 0f && _history.Count > 1 && _rng.NextDouble() < _cfg.RevisitChance)
			{
				// retour sur un ancien emplacement (pas le dernier), un peu décalé
				var h = _history[_rng.Next(_history.Count - 1)];
				double a = _rng.NextDouble() * Math.PI * 2.0;
				float j = _nr * 0.25f * (float)_rng.NextDouble();
				x = h.x + (float)Math.Cos(a) * j; z = h.z + (float)Math.Sin(a) * j;
			}
			else
			{
				switch (_cfg.Containment)
				{
					case "free":
						x = _minX + (float)_rng.NextDouble() * (_maxX - _minX);
						z = _minZ + (float)_rng.NextDouble() * (_maxZ - _minZ);
						break;
					case "overlap":
					{
						double a = _rng.NextDouble() * Math.PI * 2.0;
						float d = _cfg.MaxShift * _r * (0.4f + 0.6f * (float)_rng.NextDouble());
						x = _cx + (float)Math.Cos(a) * d; z = _cz + (float)Math.Sin(a) * d;
						break;
					}
					default:   // inside : le cercle suivant tient entièrement dans le cercle actuel
					{
						double a = _rng.NextDouble() * Math.PI * 2.0;
						float room = MathF.Max(0f, _r - _nr);
						float d = room * MathF.Sqrt((float)_rng.NextDouble());
						x = _cx + (float)Math.Cos(a) * d; z = _cz + (float)Math.Sin(a) * d;
						break;
					}
				}
			}
			// le centre reste sur la carte
			_nx = Math.Clamp(x, _minX, _maxX); _nz = Math.Clamp(z, _minZ, _maxZ);
			// en mode « inside », le clamp ne doit pas faire sortir le cercle de l'ancien
			if (_cfg.Containment == "inside" && _nr > 0f)
			{
				float dx = _nx - _cx, dz = _nz - _cz, dist = MathF.Sqrt(dx * dx + dz * dz), room = MathF.Max(0f, _r - _nr);
				if (dist > room && dist > 1e-4f) { _nx = _cx + dx / dist * room; _nz = _cz + dz / dist * room; }
			}
		}

		/// <summary>Fait avancer le temps de <paramref name="dt"/> secondes (un tick du serveur).</summary>
		public void Update(float dt)
		{
			Changed = false;
			if (Phase == ZonePhase.None || Phase == ZonePhase.Collapsed) return;
			_timer += dt;
			// seul -> annonce -> réduction (une phase de durée nulle est sautée)
			while (true)
			{
				if (Phase == ZonePhase.Idle && _timer >= _stage.Idle) { _timer -= _stage.Idle; Phase = ZonePhase.Warning; Changed = true; continue; }
				if (Phase == ZonePhase.Warning && _timer >= _stage.Warning) { _timer -= _stage.Warning; Phase = ZonePhase.Shrinking; Changed = true; continue; }
				break;
			}
			if (Phase == ZonePhase.Shrinking && _timer >= _stage.Shrink)
			{
				// la réduction est finie : la zone suivante devient la zone actuelle
				_cx = _nx; _cz = _nz; _r = _nr;
				_history.Add((_cx, _cz));
				if (_r <= 0f) { Phase = ZonePhase.Collapsed; Changed = true; _timer = 0f; return; }
				_stageIndex++;
				if (_stageIndex >= _cfg.Stages.Count && !_cfg.Cycle) { Phase = ZonePhase.Collapsed; Changed = true; _timer = 0f; return; }
				float carry = _timer - _stage.Shrink;
				BeginStage();
				_timer = carry;
				while (true)
				{
					if (Phase == ZonePhase.Idle && _timer >= _stage.Idle) { _timer -= _stage.Idle; Phase = ZonePhase.Warning; continue; }
					if (Phase == ZonePhase.Warning && _timer >= _stage.Warning) { _timer -= _stage.Warning; Phase = ZonePhase.Shrinking; continue; }
					break;
				}
			}
		}

		/// <summary>Le cercle sûr actuel (en réduction : interpolé) : centre et rayon.</summary>
		public void CurrentCircle(out float x, out float z, out float r)
		{
			if (Phase == ZonePhase.Shrinking)
			{
				float t = Math.Clamp(_timer / _stage.Shrink, 0f, 1f);
				x = _cx + (_nx - _cx) * t; z = _cz + (_nz - _cz) * t; r = _r + (_nr - _r) * t;
				return;
			}
			if (Phase == ZonePhase.Collapsed) { x = _cx; z = _cz; r = 0f; return; }
			x = _cx; z = _cz; r = _r;
		}

		public bool NextVisible => Phase == ZonePhase.Warning || Phase == ZonePhase.Shrinking;
		public void NextCircle(out float x, out float z, out float r) { x = _nx; z = _nz; r = _nr; }

		/// <summary>Vrai si le point est dans la zone sûre.</summary>
		public bool Inside(float px, float pz)
		{
			if (Phase == ZonePhase.None) return true;
			CurrentCircle(out float x, out float z, out float r);
			float dx = px - x, dz = pz - z;
			return dx * dx + dz * dz <= r * r;
		}

		/// <summary>Dégâts par tick hors de la zone dans l'état actuel.</summary>
		public float Damage => Phase == ZonePhase.Collapsed ? _cfg.FinalDamage : (_stage?.Damage ?? 0f);

		/// <summary>Secondes restantes dans la phase actuelle (0 si fermée).</summary>
		public float SecondsLeft
		{
			get
			{
				switch (Phase)
				{
					case ZonePhase.Idle: return MathF.Max(0f, _stage.Idle - _timer);
					case ZonePhase.Warning: return MathF.Max(0f, _stage.Warning - _timer);
					case ZonePhase.Shrinking: return MathF.Max(0f, _stage.Shrink - _timer);
					default: return 0f;
				}
			}
		}

		/// <summary>L'état à diffuser aux joueurs, daté du tick <paramref name="currentTick"/> du serveur.</summary>
		public ZoneState ToState(uint currentTick)
		{
			float dur = Phase == ZonePhase.Idle ? _stage.Idle : Phase == ZonePhase.Warning ? _stage.Warning : Phase == ZonePhase.Shrinking ? _stage.Shrink : 0f;
			uint startTick = currentTick - (uint)Math.Round(_timer * _tickRate);
			return new ZoneState
			{
				Phase = Phase, Stage = (byte)Math.Min(255, Stage), StartTick = startTick, DurationTicks = (uint)Math.Round(dur * _tickRate),
				CurX = _cx, CurZ = _cz, CurRadius = _r, NextX = _nx, NextZ = _nz, NextRadius = _nr,
				Damage = Damage, TickSeconds = _cfg.TickSeconds, TickRate = (byte)_tickRate,
				WarningSeconds = _stage?.Warning ?? 0f, ShrinkSeconds = _stage?.Shrink ?? 0f
			};
		}
	}
}

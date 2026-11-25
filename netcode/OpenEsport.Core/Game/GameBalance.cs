using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenEsport.Core
{
	/// <summary>
	/// Tous les réglages de jeu au même endroit : déplacement, armes, balles, butin, niveau des bots.
	/// Chaque réglage a un nom (« movement.baseSpeed »), une valeur par défaut, une unité et une description.
	/// Le serveur les lit dans un fichier (<c>balance.json</c>, voir <see cref="ToFileText"/>) et les envoie aux clients à la connexion :
	/// tout le monde simule avec les mêmes valeurs, sinon la prédiction du déplacement se désynchroniserait.
	/// Pour ajouter un réglage : une ligne dans <see cref="Define"/>, puis lire sa valeur avec <see cref="Get"/> ou un champ mis en cache.
	/// </summary>
	public static class GameBalance
	{
		public sealed class Entry
		{
			public string Key, Unit, Description;
			public double Default, Value;
			public bool Integer;
		}

		private static readonly List<Entry> _entries = new List<Entry>();
		private static readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>();

		/// <summary>Appelé quand des valeurs changent (lecture d'un fichier, réception de celles du serveur).</summary>
		public static event Action Changed;

		public static IReadOnlyList<Entry> Entries => _entries;

		// ---------------------------------------------------------------- valeurs mises en cache (lues à chaque tick de simulation)

		public static float UnarmedMultiplier = 1.2f;
		public static float BodyBalanceStrength { get; private set; }
		public static float BaseSpeed, SprintMultiplier, CrouchMultiplier, Acceleration, Deceleration, Gravity, JumpSpeed;
		public static float SlideStartMultiplier, SlideMaxMultiplier, SlideCooldown, SlideFlatDeceleration, SlideDownAcceleration, SlideUpDeceleration;
		public static float BulletSpeed, BulletGravity, HeadMultiplier, TorsoMultiplier, LimbMultiplier;
		public static float ChestSecondWeaponChance, ChestAccessoryChance, ChestSupplyChance, ChestGrenadeChance, GroundGrenadeShare, ItemPointChance, ChestPointChance, QualityMajority, ExtraSupplyChance, ZoneRampPerTick, ZoneRampMax, ExtraAmmoChance, AmmoAmountMultiplier;
		/// <summary>Poids de tirage des fournitures : bouclier, puis les 6 soins (HealSmall à BothBig).</summary>
		public static readonly float[] SupplyWeights = new float[7];
		/// <summary>Points de bouclier par niveau (blanc, bleu, violet, rouge).</summary>
		public static readonly float[] ShieldMax = new float[4];
		/// <summary>Soins (identifiants HealSmall à BothBig) : vie rendue, bouclier rendu (999 = tout remplir) et durée d'utilisation.</summary>
		public static readonly float[] ConsumableHeal = new float[6], ConsumableShield = new float[6], ConsumableSeconds = new float[6];
		public static readonly string[] ConsumableKeys = { "healSmall", "healBig", "shieldSmall", "shieldBig", "bothSmall", "bothBig" };
		/// <summary>Par niveau (classique, rare, épique, légendaire) : poids de tirage, et multiplicateurs appliqués aux armes.</summary>
		public static readonly float[] TierChance = new float[4], TierDamage = new float[4], TierMagazine = new float[4], TierReload = new float[4], TierSpread = new float[4];

		static GameBalance()
		{
			// ---- déplacement (m/s, m/s²). Valeurs proches d'Apex Legends : marche 3,8 m/s, course 6,5 à 7,4 m/s.
			Define("movement.baseSpeed", 4.0, "m/s", "Vitesse de déplacement normale (sans sprint).");
			Define("movement.sprintMultiplier", 1.7, "x", "Multiplicateur de vitesse en sprint (4 x 1,7 = 6,8 m/s).");
			Define("movement.crouchMultiplier", 0.7, "x", "Multiplicateur de vitesse accroupi.");
			Define("movement.unarmedMultiplier", 1.2, "x", "Multiplicateur de vitesse quand on n'a aucune arme en main (à mains nues on court plus vite).");
			Define("movement.bodyBalance", 1, "0–1", "Intensité des effets du gabarit et de la vitesse sur le déplacement et les dégâts des balles (0 = cosmétique).");
			Define("movement.acceleration", 8.0, "1/s", "Rapidité pour atteindre la vitesse voulue (plus grand = plus nerveux).");
			Define("movement.deceleration", 10.0, "1/s", "Rapidité pour s'arrêter ou changer de direction.");
			Define("movement.gravity", 29.4, "m/s²", "Gravité des joueurs.");
			Define("movement.jumpSpeed", 10.0, "m/s", "Vitesse verticale au saut (hauteur = vitesse² / (2 x gravité)).");
			Define("slide.startMultiplier", 1.45, "x", "Coup de vitesse au début d'une glissade, par rapport à la vitesse de course (même sur terrain plat).");
			Define("slide.maxMultiplier", 2.0, "x", "Vitesse maximale d'une glissade, en multiple de la vitesse de sprint (en descente).");
			Define("slide.cooldown", 1.0, "s", "Délai avant de pouvoir glisser de nouveau.");
			Define("slide.flatDeceleration", 6.0, "m/s²", "Perte de vitesse de la glissade sur terrain plat (plus petit = glisse plus longtemps).");
			Define("slide.downAcceleration", 4.0, "m/s²", "Gain de vitesse de la glissade en descente (à pente maximale).");
			Define("slide.upDeceleration", 16.0, "m/s²", "Perte de vitesse de la glissade en montée (à pente maximale).");

			// ---- balles (tir réel : temps de vol et chute)
			Define("bullet.speed", 250.0, "m/s", "Vitesse des balles.");
			Define("bullet.gravity", 9.8, "m/s²", "Chute des balles.");
			Define("hit.headMultiplier", 2.0, "x", "Multiplicateur de dégâts à la tête.");
			Define("hit.torsoMultiplier", 1.0, "x", "Multiplicateur de dégâts au torse.");
			Define("hit.limbMultiplier", 0.9, "x", "Multiplicateur de dégâts aux membres.");

			// ---- armes : valeurs d'origine reprises de WeaponDefs
			foreach (var w in WeaponDefs.All)
			{
				string n = WeaponDefs.NameOf(w);
				Define($"weapon.{n}.damage", w.Damage, "PV", $"Dégâts par balle ({n}).");
				Define($"weapon.{n}.roundsPerMinute", w.RoundsPerMinute, "coups/min", $"Cadence de tir ({n}).");
				if (w.MagazineSize > 0) Define($"weapon.{n}.magazine", w.MagazineSize, "balles", $"Taille du chargeur ({n}).", integer: true);
				Define($"weapon.{n}.range", w.Range, "m", $"Portée ({n}).");
				if (w.SpreadDegrees > 0) Define($"weapon.{n}.spread", w.SpreadDegrees, "°", $"Dispersion ({n}).");
				if (w.AimSpreadDegrees > 0) Define($"weapon.{n}.aimSpread", w.AimSpreadDegrees, "°", $"Dispersion en visée ({n}).");
				if (w.FalloffEnd > 0)
				{
					Define($"weapon.{n}.falloffStart", w.FalloffStart, "m", $"Distance à partir de laquelle les dégâts baissent ({n}).");
					Define($"weapon.{n}.falloffEnd", w.FalloffEnd, "m", $"Distance à laquelle l'arme ne fait plus de dégâts ({n}) ; 0 = pas de perte.");
				}
				if (w.Automatic && !w.Melee) Define($"weapon.{n}.tapSeconds", w.TapSeconds, "s", $"Durée d'appui avant la deuxième balle ({n}) : un clic plus bref n'en tire qu'une ; 0 = rafale tout de suite.");
				if (w.Pellets > 1) Define($"weapon.{n}.pellets", w.Pellets, "plombs", $"Nombre de plombs par tir ({n}).", integer: true);
				if (w.ReloadSeconds > 0) Define($"weapon.{n}.reloadSeconds", w.ReloadSeconds, "s", $"Durée du rechargement ({n}).");
				if (w.BulletSpeed > 0) Define($"weapon.{n}.bulletSpeed", w.BulletSpeed, "m/s", $"Vitesse de la balle ({n}) ; sans cette ligne, la vitesse commune bullet.speed.");
			}

			// ---- grenades
			foreach (var g in GrenadeDefs.All)
			{
				string n = GrenadeDefs.NameOf(g);
				Define($"grenade.{n}.fuse", g.Fuse, "s", $"Délai avant l'effet de la grenade ({n}).");
				Define($"grenade.{n}.radius", g.Radius, "m", g.Effect == GrenadeEffect.Flash ? $"Portée de l'éclair ({n})." : $"Rayon de l'effet ({n}).");
				if (g.Effect == GrenadeEffect.Blast) Define($"grenade.{n}.damage", g.Damage, "PV", $"Dégâts au centre de l'explosion ({n}) ; 20 % au bord.");
				if (g.Effect == GrenadeEffect.Fire) { Define($"grenade.{n}.burnSeconds", g.BurnSeconds, "s", $"Durée de la zone de feu ({n})."); Define($"grenade.{n}.burnDps", g.BurnDps, "PV/s", $"Dégâts par seconde dans la zone de feu ({n})."); }
				if (g.Effect == GrenadeEffect.Flash) Define($"grenade.{n}.flashSeconds", g.FlashSeconds, "s", $"Durée maximale de l'aveuglement ({n}) : de face et de près.");
			}

			// ---- niveaux d'équipement : blanc / bleu / violet / rouge
			double[] chance = { 55, 28, 13, 4 }, damage = { 1.0, 1.08, 1.16, 1.25 }, magazine = { 1.0, 1.1, 1.2, 1.35 }, reload = { 1.0, 0.93, 0.86, 0.78 }, spread = { 1.0, 0.9, 0.8, 0.7 };
			for (int t = 0; t < Tiers.Count; t++)
			{
				string k = Tiers.Keys[t], n = Tiers.FrenchNames[t].ToLowerInvariant();
				Define($"tier.chance.{k}", chance[t], "poids", $"Chance qu'une arme tombe au niveau {n} (poids relatif : 55 / 28 / 13 / 4 = environ 55 %, 28 %, 13 %, 4 %).");
				Define($"tier.damage.{k}", damage[t], "x", $"Multiplicateur de dégâts des armes de niveau {n}.");
				Define($"tier.magazine.{k}", magazine[t], "x", $"Multiplicateur de la taille du chargeur, niveau {n}.");
				Define($"tier.reload.{k}", reload[t], "x", $"Multiplicateur de la durée de rechargement, niveau {n} (plus petit = plus rapide).");
				Define($"tier.spread.{k}", spread[t], "x", $"Multiplicateur de la dispersion des tirs, niveau {n} (plus petit = plus précis).");
			}

			// ---- bouclier et soins
			double[] shield = { 50, 75, 100, 125 };
			for (int t = 0; t < Tiers.Count; t++)
				Define($"shield.max.{Tiers.Keys[t]}", shield[t], "PS", $"Points de bouclier d'un bouclier de niveau {Tiers.FrenchNames[t].ToLowerInvariant()}.");
			double[] healAmount = { 25, 999, 0, 0, 50, 999 }, shieldAmount = { 0, 0, 25, 999, 50, 999 }, seconds = { 3, 6, 3, 6, 5, 10 };
			string[] fr = { "Petit soin", "Grand soin", "Petit bouclier", "Grand bouclier", "Petit bouclier + vie", "Grand bouclier + vie" };
			for (int i = 0; i < 6; i++)
			{
				string k = ConsumableKeys[i];
				Define($"consumable.{k}.heal", healAmount[i], "PV", $"{fr[i]} : vie rendue (999 = tout remplir).");
				Define($"consumable.{k}.shield", shieldAmount[i], "PS", $"{fr[i]} : bouclier rendu (999 = tout remplir).");
				Define($"consumable.{k}.seconds", seconds[i], "s", $"{fr[i]} : durée d'utilisation.");
			}

			// ---- butin
			Define("zone.rampPerTick", 0.15, "x", "Hors de la zone : à chaque coup consécutif, les dégâts de la zone augmentent de cette part (0,15 = +15 % du coup de base par coup, tant qu'on reste dehors).");
			Define("zone.rampMax", 3.0, "x", "Hors de la zone : multiplicateur maximal des dégâts de zone (3 = trois fois le coup de base).");
			Define("loot.extraAmmoChance", 0.85, "0-1", "Probabilité qu'un point d'objet ait en plus une boîte de munitions posée à côté (en plus des munitions de l'arme).");
			Define("loot.ammoAmountMultiplier", 1.5, "x", "Multiplicateur de la quantité de munitions des boîtes posées au sol et dans les coffres.");
			Define("loot.pointChance", 0.95, "0-1", "Points de butin « au hasard » (objets au sol) : probabilité commune qu'un point soit garni à chaque partie. Chaque point peut avoir sa propre valeur dans l'éditeur.");
			Define("loot.extraSupplyChance", 0.5, "0-1", "Probabilité qu'un point d'objet ait en plus un bouclier ou un soin posé à côté (en plus des munitions).");
			Define("loot.chestPointChance", 1.0, "0-1", "Emplacements de coffre « au hasard » : probabilité commune qu'un coffre soit présent à chaque partie. Chaque coffre peut avoir sa propre valeur dans l'éditeur.");
			Define("loot.qualityMajority", 0.70, "0-1", "Points de butin réglés « en majorité » d'un niveau (rare, épique…) : probabilité que l'arme ou le bouclier soit de ce niveau (sinon tirage normal).");
			Define("loot.chestSecondWeaponChance", 0.20, "0-1", "Probabilité qu'un coffre contienne deux armes (sinon une seule).");
			Define("loot.chestSupplyChance", 0.67, "0-1", "Probabilité qu'un coffre contienne un bouclier ou un soin (en plus des armes, de l'accessoire et des munitions).");
			Define("loot.chestGrenadeChance", 0.35, "0-1", "Probabilité qu'un coffre contienne une grenade (en plus des armes, de l'accessoire, des soins et des munitions).");
			Define("loot.groundGrenadeShare", 0.12, "0-1", "Part des objets au sol tirés au hasard qui sont des grenades.");
			double[] supply = { 22, 30, 8, 24, 6, 12, 3 };
			string[] supplyNames = { "shield", "healSmall", "healBig", "shieldSmall", "shieldBig", "bothSmall", "bothBig" };
			for (int i = 0; i < 7; i++)
				Define($"loot.weight.{supplyNames[i]}", supply[i], "poids", $"Poids de tirage dans le butin et les coffres : {(i == 0 ? "bouclier (son niveau est tiré avec tier.chance.*)" : "soin")} ({supplyNames[i]}). Plus petit = plus rare.");
			Define("loot.chestAccessoryChance", 0.70, "0-1", "Probabilité qu'un coffre contienne un accessoire (viseur ou sac à dos).");

			// ---- bots : probabilité de viser juste à un instant donné, selon le niveau
			Define("bot.accuracy.easy", 0.05, "0-1", "Bots « easy » : proportion du temps où la visée est juste.");
			Define("bot.accuracy.normal", 0.30, "0-1", "Bots « normal ».");
			Define("bot.accuracy.hard", 0.60, "0-1", "Bots « hard ».");
			Define("bot.accuracy.expert", 0.97, "0-1", "Bots « expert ».");

			// ---- autres modes de jeu (lus par le serveur avec GameBalance.F)
			Define("mode.dmKills", 20, "kills", "Deathmatch : éliminations pour gagner.", true);
			Define("mode.dmTime", 600, "s", "Deathmatch : durée maximale (le meilleur score gagne à la fin).", true);
			Define("mode.tdmKills", 50, "kills", "Team Deathmatch : éliminations d'équipe pour gagner.", true);
			Define("mode.tdmTime", 720, "s", "Team Deathmatch : durée maximale.", true);
			Define("mode.ctfCaptures", 3, "prises", "Capture du drapeau : prises pour gagner.", true);
			Define("mode.ctfTime", 720, "s", "Capture du drapeau : durée maximale.", true);
			Define("mode.flagReturnSeconds", 20, "s", "Capture du drapeau : un drapeau lâché retourne seul à sa base après ce délai.");
			Define("mode.flagCarrierSpeed", 0.9, "x", "Capture du drapeau : multiplicateur de vitesse du porteur.");
			Define("mode.controlScore", 100, "points", "Contrôle : points pour gagner.", true);
			Define("mode.controlTime", 900, "s", "Contrôle : durée maximale.", true);
			Define("mode.controlCaptureSeconds", 5, "s", "Contrôle : temps pour capturer un point seul (plus vite à plusieurs).");
			Define("mode.controlTickSeconds", 1, "s", "Contrôle : intervalle entre deux décomptes de points.");
			Define("mode.respawnProtection", 2, "s", "Modes à réapparition : invulnérabilité après la réapparition.");
			Define("mode.lobbyMinPlayers", 1, "joueurs", "Humains nécessaires pour lancer une partie des modes hors battle royale (les bots complètent).", true);
			Define("sabotage.roundsToWin", 7, "manches", "Sabotage : manches pour gagner (mi-temps après la moitié des manches possibles).", true);
			Define("sabotage.buySeconds", 15, "s", "Sabotage : durée de la phase d'achat.");
			Define("sabotage.roundSeconds", 115, "s", "Sabotage : durée d'une manche avant la pose de la bombe.");
			Define("sabotage.bombSeconds", 40, "s", "Sabotage : délai avant l'explosion de la bombe posée.");
			Define("sabotage.plantSeconds", 3, "s", "Sabotage : durée de la pose de la bombe.");
			Define("sabotage.defuseSeconds", 5, "s", "Sabotage : durée du désamorçage.");
			Define("sabotage.defuseKitSeconds", 3, "s", "Sabotage : durée du désamorçage avec un kit.");
			Define("sabotage.endSeconds", 5, "s", "Sabotage : pause entre deux manches.");
			Define("sabotage.startMoney", 800, "$", "Sabotage : argent de départ.", true);
			Define("sabotage.winMoney", 3250, "$", "Sabotage : gain d'une manche gagnée.", true);
			Define("sabotage.lossMoney", 1900, "$", "Sabotage : gain d'une manche perdue.", true);
			Define("sabotage.lossBonus", 500, "$", "Sabotage : bonus par défaite consécutive.", true);
			Define("sabotage.lossBonusMax", 3400, "$", "Sabotage : gain maximal d'une défaite.", true);
			Define("sabotage.killMoney", 300, "$", "Sabotage : gain par élimination.", true);
			Define("sabotage.plantMoney", 800, "$", "Sabotage : gain de l'équipe qui a posé la bombe (même si elle perd la manche).", true);
			Define("sabotage.maxMoney", 16000, "$", "Sabotage : argent maximal.", true);

			Refresh();
		}

		private static void Define(string key, double value, string unit, string description, bool integer = false)
		{
			value = Math.Round(value, 4); // le fichier écrit 4 décimales : les valeurs d'origine (float) doivent s'y retrouver exactement
			var e = new Entry { Key = key, Default = value, Value = value, Unit = unit, Description = description, Integer = integer };
			_entries.Add(e);
			_byKey[key] = e;
		}

		public static double Get(string key) => _byKey[key].Value;
		public static float F(string key) => (float)_byKey[key].Value;
		public static bool Has(string key) => _byKey.ContainsKey(key);

		/// <summary>Relit les valeurs dans les champs mis en cache et les armes.</summary>
		private static void Refresh()
		{
			BodyBalanceStrength = Math.Clamp(F("movement.bodyBalance"), 0, 1);
			UnarmedMultiplier = F("movement.unarmedMultiplier"); BaseSpeed = F("movement.baseSpeed"); SprintMultiplier = F("movement.sprintMultiplier"); CrouchMultiplier = F("movement.crouchMultiplier");
			Acceleration = F("movement.acceleration"); Deceleration = F("movement.deceleration"); Gravity = F("movement.gravity"); JumpSpeed = F("movement.jumpSpeed");
			SlideStartMultiplier = F("slide.startMultiplier"); SlideMaxMultiplier = F("slide.maxMultiplier"); SlideCooldown = F("slide.cooldown");
			SlideFlatDeceleration = F("slide.flatDeceleration"); SlideDownAcceleration = F("slide.downAcceleration"); SlideUpDeceleration = F("slide.upDeceleration");
			BulletSpeed = F("bullet.speed"); BulletGravity = F("bullet.gravity");
			HeadMultiplier = F("hit.headMultiplier"); TorsoMultiplier = F("hit.torsoMultiplier"); LimbMultiplier = F("hit.limbMultiplier");
			ChestSecondWeaponChance = F("loot.chestSecondWeaponChance"); ChestAccessoryChance = F("loot.chestAccessoryChance"); ChestSupplyChance = F("loot.chestSupplyChance"); ChestGrenadeChance = F("loot.chestGrenadeChance"); GroundGrenadeShare = F("loot.groundGrenadeShare");
			ItemPointChance = F("loot.pointChance"); ChestPointChance = F("loot.chestPointChance"); QualityMajority = F("loot.qualityMajority");
			ZoneRampPerTick = F("zone.rampPerTick"); ZoneRampMax = F("zone.rampMax"); ExtraAmmoChance = F("loot.extraAmmoChance"); ExtraSupplyChance = F("loot.extraSupplyChance"); AmmoAmountMultiplier = F("loot.ammoAmountMultiplier");
			string[] sn = { "shield", "healSmall", "healBig", "shieldSmall", "shieldBig", "bothSmall", "bothBig" };
			for (int i = 0; i < 7; i++) SupplyWeights[i] = F($"loot.weight.{sn[i]}");
			for (int t = 0; t < Tiers.Count; t++)
			{
				string k = Tiers.Keys[t];
				TierChance[t] = F($"tier.chance.{k}"); TierDamage[t] = F($"tier.damage.{k}"); TierMagazine[t] = F($"tier.magazine.{k}");
				TierReload[t] = F($"tier.reload.{k}"); TierSpread[t] = F($"tier.spread.{k}");
			}
			for (int t = 0; t < Tiers.Count; t++) ShieldMax[t] = F($"shield.max.{Tiers.Keys[t]}");
			for (int i = 0; i < 6; i++)
			{
				ConsumableHeal[i] = F($"consumable.{ConsumableKeys[i]}.heal"); ConsumableShield[i] = F($"consumable.{ConsumableKeys[i]}.shield"); ConsumableSeconds[i] = F($"consumable.{ConsumableKeys[i]}.seconds");
			}
			WeaponDefs.ApplyBalance();
			GrenadeDefs.ApplyBalance();
		}

		/// <summary>Probabilité qu'un bot de ce niveau (0 à 1) vise juste à un instant donné, interpolée entre les niveaux.</summary>
		public static float BotAccuracy(float skill)
		{
			(float skill, float chance)[] curve =
			{
				(0f, 0f), (0.2f, F("bot.accuracy.easy")), (0.5f, F("bot.accuracy.normal")), (0.75f, F("bot.accuracy.hard")), (1f, F("bot.accuracy.expert"))
			};
			skill = Math.Clamp(skill, 0f, 1f);
			for (int i = 1; i < curve.Length; i++)
				if (skill <= curve[i].skill)
				{
					float t = (skill - curve[i - 1].skill) / (curve[i].skill - curve[i - 1].skill);
					return curve[i - 1].chance + (curve[i].chance - curve[i - 1].chance) * t;
				}
			return curve[curve.Length - 1].chance;
		}

		public static void ResetToDefaults()
		{
			foreach (var e in _entries) e.Value = e.Default;
			Refresh();
			Changed?.Invoke();
		}

		/// <summary>Nombre de réglages qui diffèrent de leur valeur par défaut.</summary>
		public static int ModifiedCount
		{
			get { int n = 0; foreach (var e in _entries) if (e.Value != e.Default) n++; return n; }
		}

		// ---------------------------------------------------------------- fichier

		/// <summary>Texte du fichier de réglages : un objet JSON avec des commentaires (// ...) qui expliquent chaque valeur.</summary>
		public static string ToFileText()
		{
			var sb = new StringBuilder();
			sb.AppendLine("// Réglages du jeu. Modifie les valeurs puis relance le serveur : il les envoie à tous les joueurs à la connexion.");
			sb.AppendLine("// Supprime une ligne (ou tout le fichier) pour retrouver la valeur par défaut. Les « // » sont des commentaires.");
			sb.AppendLine("{");
			string group = null;
			for (int i = 0; i < _entries.Count; i++)
			{
				var e = _entries[i];
				string g = e.Key.Substring(0, e.Key.IndexOf('.'));
				if (g != group)
				{
					if (group != null) sb.AppendLine();
					sb.AppendLine($"  // ---- {GroupTitle(g)}");
					group = g;
				}
				sb.AppendLine($"  // {e.Description} [{e.Unit}] (défaut : {Format(e.Default)})");
				sb.AppendLine($"  \"{e.Key}\": {Format(e.Value)}{(i < _entries.Count - 1 ? "," : "")}");
			}
			sb.AppendLine("}");
			return sb.ToString();
		}

		private static string GroupTitle(string g) => g switch
		{
			"movement" => "Déplacement",
			"slide" => "Glissade",
			"bullet" => "Balles",
			"hit" => "Zones touchées",
			"weapon" => "Armes",
			"tier" => "Niveaux d'équipement (blanc / bleu / violet / rouge)",
			"shield" => "Bouclier",
			"consumable" => "Soins",
			"loot" => "Butin",
			"bot" => "Bots",
			_ => g
		};

		private static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

		/// <summary>Applique un fichier de réglages. Les clés inconnues et les valeurs invalides sont signalées dans <paramref name="warnings"/>.</summary>
		public static void ApplyFileText(string text, List<string> warnings)
		{
			var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
			using var doc = JsonDocument.Parse(text, options);
			foreach (var p in doc.RootElement.EnumerateObject())
			{
				if (!_byKey.TryGetValue(p.Name, out var e)) { warnings?.Add($"réglage inconnu : {p.Name}"); continue; }
				if (p.Value.ValueKind != JsonValueKind.Number) { warnings?.Add($"{p.Name} : un nombre est attendu"); continue; }
				double v = p.Value.GetDouble();
				if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) { warnings?.Add($"{p.Name} : valeur invalide ({v})"); continue; }
				e.Value = e.Integer ? Math.Round(v) : v;
			}
			Refresh();
			Changed?.Invoke();
		}

		// ---------------------------------------------------------------- réseau

		/// <summary>Valeurs courantes, dans l'ordre de la table (la même chez le serveur et chez le client : version du protocole).</summary>
		public static float[] ToWire()
		{
			var a = new float[_entries.Count];
			for (int i = 0; i < a.Length; i++) a[i] = (float)_entries[i].Value;
			return a;
		}

		public static void ApplyWire(float[] values)
		{
			if (values == null || values.Length != _entries.Count) return; // autre version : on garde les valeurs actuelles
			for (int i = 0; i < values.Length; i++)
				if (!float.IsNaN(values[i]) && !float.IsInfinity(values[i])) _entries[i].Value = values[i];
			Refresh();
			Changed?.Invoke();
		}
	}
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Openesport.Utils
{
	/// <summary>Comportement du bouton de visée.</summary>
	public enum AimMode
	{
		/// <summary>Un clic vise, un autre clic retire la visée.</summary>
		Switch = 0,
		/// <summary>On ne vise que tant que le bouton est maintenu.</summary>
		Hold = 1
	}

	/// <summary>
	/// Réglages du joueur, sauvegardés dans user://settings.json : mode de visée et touches personnalisées.
	/// Une touche personnalisée remplace celle par défaut de l'action (voir InputManager).
	/// </summary>
	public static class GameSettings
	{
		private const string Path = "user://settings.json";

		private sealed class Data
		{
			public int Aim { get; set; } = (int)AimMode.Switch;
			/// <summary>action → « key:CODE », « phys:CODE » ou « mouse:INDEX ».</summary>
			public Dictionary<string, string> Keys { get; set; } = new();
		}

		private static Data _data;

		private static Data D => _data ??= Load();

		public static AimMode Aim
		{
			get => (AimMode)D.Aim;
			set { D.Aim = (int)value; Save(); }
		}

		public static IReadOnlyDictionary<string, string> KeyOverrides => D.Keys;

		public static void SetBinding(string action, InputEvent ev)
		{
			D.Keys[action] = Encode(ev);
			Save();
		}

		public static void ResetBindings()
		{
			D.Keys.Clear();
			Save();
		}

		/// <summary>Touche personnalisée d'une action, ou null si elle garde sa touche par défaut.</summary>
		public static InputEvent GetBinding(string action) => D.Keys.TryGetValue(action, out var s) ? Decode(s) : null;

		public static string Encode(InputEvent ev) => ev switch
		{
			InputEventKey k when k.Keycode != Key.None => $"key:{(long)k.Keycode}",
			InputEventKey k => $"phys:{(long)k.PhysicalKeycode}",
			InputEventMouseButton m => $"mouse:{(int)m.ButtonIndex}",
			_ => ""
		};

		public static InputEvent Decode(string s)
		{
			int colon = s.IndexOf(':');
			if (colon < 0 || !long.TryParse(s.AsSpan(colon + 1), out long code)) return null;
			return s.Substring(0, colon) switch
			{
				"key" => new InputEventKey { Keycode = (Key)code },
				"phys" => new InputEventKey { PhysicalKeycode = (Key)code },
				"mouse" => new InputEventMouseButton { ButtonIndex = (MouseButton)code },
				_ => null
			};
		}

		/// <summary>Nom lisible de la touche d'un évènement (« Z », « Clic gauche »…).</summary>
		public static string Describe(InputEvent ev)
		{
			switch (ev)
			{
				case InputEventKey k:
					return k.Keycode != Key.None
						? OS.GetKeycodeString(k.Keycode)
						: OS.GetKeycodeString(DisplayServer.KeyboardGetKeycodeFromPhysical(k.PhysicalKeycode));
				case InputEventMouseButton m:
					return m.ButtonIndex switch
					{
						MouseButton.Left => "Clic gauche",
						MouseButton.Right => "Clic droit",
						MouseButton.Middle => "Clic molette",
						_ => "Souris " + (int)m.ButtonIndex
					};
				default: return "—";
			}
		}

		private static Data Load()
		{
			try
			{
				if (!Godot.FileAccess.FileExists(Path)) return new Data();
				using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Read);
				return JsonSerializer.Deserialize<Data>(f.GetAsText()) ?? new Data();
			}
			catch (Exception e)
			{
				GD.PrintErr("[GameSettings] lecture impossible : " + e.Message);
				return new Data();
			}
		}

		private static void Save()
		{
			try
			{
				using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
				f?.StoreString(JsonSerializer.Serialize(D));
			}
			catch (Exception e) { GD.PrintErr("[GameSettings] écriture impossible : " + e.Message); }
		}
	}
}

using System;
using System.Collections.Generic;
using Godot;

/// <summary>Un personnage sauvegardé : un nom et son apparence.</summary>
public sealed class SavedCharacter
{
	public string Name;
	public CharacterAppearance Appearance;
}

/// <summary>
/// Les personnages créés par le joueur, sauvegardés dans user://characters.json, et celui qui est choisi pour jouer.
/// Au premier lancement, le personnage de l'ancienne sauvegarde (user://character.json) devient « Personnage 1 ».
/// </summary>
public static class CharacterLibrary
{
	private const string Path = "user://characters.json";

	public static List<SavedCharacter> Characters { get; private set; }
	public static int SelectedIndex { get; private set; }

	/// <summary>Appelé quand le personnage choisi change (la partie suivante l'utilisera).</summary>
	public static event Action SelectionChanged;

	public static SavedCharacter Selected
	{
		get { Ensure(); return Characters[Math.Clamp(SelectedIndex, 0, Characters.Count - 1)]; }
	}

	public static CharacterAppearance SelectedAppearance() => Selected.Appearance.Clone();

	/// <summary>Vrai quand la bibliothèque est celle du compte connecté (elle est alors synchronisée avec le compte et non avec le fichier local).</summary>
	private static bool _account;

	/// <summary>Charge les personnages du compte connecté. Un compte neuf reçoit un premier personnage.</summary>
	public static void UseAccount(Openesport.Online.AccountProfile profile)
	{
		_account = true;
		Characters = new List<SavedCharacter>();
		int selected = 0;
		foreach (var c in profile.Characters)
		{
			try
			{
				if (c.Id == profile.SelectedCharacter) selected = Characters.Count;
				Characters.Add(new SavedCharacter { Name = c.Name, Appearance = CharacterAppearance.FromDict(Json.ParseString(c.Appearance).AsGodotDictionary()) });
			}
			catch (Exception e) { GD.PrintErr("[CharacterLibrary] personnage du compte illisible : " + e.Message); }
		}
		bool fresh = Characters.Count == 0;
		if (fresh) Characters.Add(new SavedCharacter { Name = "Personnage 1", Appearance = new CharacterAppearance() });
		SelectedIndex = Math.Clamp(selected, 0, Characters.Count - 1);
		CharacterAppearance.Current = SelectedAppearance();
		if (fresh) Save();
		SelectionChanged?.Invoke();
	}

	/// <summary>Déconnexion : on oublie les personnages du compte (la bibliothèque locale sera relue au besoin).</summary>
	public static void LeaveAccount() { _account = false; Characters = null; SelectedIndex = 0; }

	private static void Ensure()
	{
		if (Characters != null) return;
		Characters = new List<SavedCharacter>();
		try
		{
			if (Godot.FileAccess.FileExists(Path))
			{
				using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Read);
				var root = Json.ParseString(f.GetAsText()).AsGodotDictionary();
				foreach (var item in root["characters"].AsGodotArray())
				{
					var d = item.AsGodotDictionary();
					Characters.Add(new SavedCharacter { Name = d["name"].AsString(), Appearance = CharacterAppearance.FromDict(d["appearance"].AsGodotDictionary()) });
				}
				SelectedIndex = root.ContainsKey("selected") ? root["selected"].AsInt32() : 0;
			}
		}
		catch (Exception e) { GD.PrintErr("[CharacterLibrary] lecture impossible : " + e.Message); }

		if (Characters.Count == 0)
		{
			// première fois : on reprend l'ancien personnage unique
			Characters.Add(new SavedCharacter { Name = "Personnage 1", Appearance = CharacterAppearance.LoadSaved() });
			SelectedIndex = 0;
			Save();
		}
		SelectedIndex = Math.Clamp(SelectedIndex, 0, Characters.Count - 1);
	}

	public static IReadOnlyList<SavedCharacter> All { get { Ensure(); return Characters; } }

	public static void Select(int index)
	{
		Ensure();
		SelectedIndex = Math.Clamp(index, 0, Characters.Count - 1);
		CharacterAppearance.Current = SelectedAppearance();
		Save();
		SelectionChanged?.Invoke();
	}

	/// <summary>Ajoute un personnage et le choisit ; retourne son indice.</summary>
	public static int Add(string name, CharacterAppearance appearance)
	{
		Ensure();
		Characters.Add(new SavedCharacter { Name = UniqueName(name), Appearance = appearance.Clone() });
		Select(Characters.Count - 1);
		return Characters.Count - 1;
	}

	public static void Update(int index, string name, CharacterAppearance appearance)
	{
		Ensure();
		var c = Characters[index];
		c.Name = string.IsNullOrWhiteSpace(name) ? c.Name : name.Trim();
		c.Appearance = appearance.Clone();
		if (index == SelectedIndex) CharacterAppearance.Current = SelectedAppearance();
		Save();
	}

	/// <summary>Supprime un personnage (il en reste toujours au moins un).</summary>
	public static bool Remove(int index)
	{
		Ensure();
		if (Characters.Count <= 1 || index < 0 || index >= Characters.Count) return false;
		Characters.RemoveAt(index);
		if (SelectedIndex >= index && SelectedIndex > 0) SelectedIndex--;
		CharacterAppearance.Current = SelectedAppearance();
		Save();
		SelectionChanged?.Invoke();
		return true;
	}

	private static string UniqueName(string name)
	{
		name = string.IsNullOrWhiteSpace(name) ? "Personnage " + (Characters.Count + 1) : name.Trim();
		string candidate = name;
		for (int i = 2; Characters.Exists(c => c.Name == candidate); i++) candidate = $"{name} ({i})";
		return candidate;
	}

	private static void Save()
	{
		if (_account) { Openesport.Online.AccountClient.PushCharacters(Characters, SelectedIndex); return; }
		try
		{
			var list = new Godot.Collections.Array();
			foreach (var c in Characters)
				list.Add(new Godot.Collections.Dictionary { { "name", c.Name }, { "appearance", c.Appearance.ToDict() } });
			using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
			f?.StoreString(Json.Stringify(new Godot.Collections.Dictionary { { "characters", list }, { "selected", SelectedIndex } }));
		}
		catch (Exception e) { GD.PrintErr("[CharacterLibrary] écriture impossible : " + e.Message); }
	}
}

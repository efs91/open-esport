using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Catalogue du kit de personnage (généré depuis MakeHuman/MPFB : assets/characters/kit/kit.json) :
/// peaux, morphologies (blend shapes) et pièces interchangeables (tenues, chaussures, cheveux, sourcils).
/// </summary>
public static class CharacterKit
{
	public const string Folder = "res://assets/characters/kit/";
	public const string ModelPath = Folder + "character_kit.glb";

	public sealed class Item { public string Id; public string Name; public bool IncludesShoes; public string MeshFile, Cover; }
	public sealed class Skin { public string Name; public string File; }

	public static List<Skin> Skins { get; private set; } = new();
	public static Dictionary<string, List<Item>> Groups { get; private set; } = new();
	/// <summary>Pour chaque morphologie, le déplacement (mètres, repère du modèle) de chaque articulation à pleine valeur.</summary>
	public static Dictionary<string, Dictionary<string, Vector3>> BoneOffsets { get; private set; } = new();

	/// <summary>Groupes de pièces que le joueur choisit (un seul de chaque porté à la fois).</summary>
	public static readonly string[] Choosable = { "clothes", "shoes", "hair", "eyebrows", "headwear" };
	/// <summary>Toujours portés.</summary>
	public static readonly string[] Always = { "eyes", "eyelashes" };

	private static bool _loaded;

	public static void EnsureLoaded()
	{
		if (_loaded) return;
		_loaded = true;
		using var file = Godot.FileAccess.Open(Folder + "kit.json", Godot.FileAccess.ModeFlags.Read);
		if (file == null) { GD.PrintErr("[CharacterKit] kit.json introuvable"); return; }
		var root = Json.ParseString(file.GetAsText()).AsGodotDictionary();
		foreach (var s in root["skins"].AsGodotArray())
		{
			var d = s.AsGodotDictionary();
			Skins.Add(new Skin { Name = d["name"].AsString(), File = d["file"].AsString() });
		}
		if (root.ContainsKey("bone_offsets"))
			foreach (var m in root["bone_offsets"].AsGodotDictionary())
			{
				var bones = new Dictionary<string, Vector3>();
				foreach (var b in m.Value.AsGodotDictionary())
				{
					var a = b.Value.AsGodotArray();
					bones[b.Key.AsString()] = new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble());
				}
				BoneOffsets[m.Key.AsString()] = bones;
			}
		foreach (var kv in root["items"].AsGodotDictionary())
		{
			var list = new List<Item>();
			foreach (var it in kv.Value.AsGodotArray())
			{
				var d = it.AsGodotDictionary();
				list.Add(new Item { Id = d["id"].AsString(), Name = d["name"].AsString(), Cover = d.ContainsKey("cover") ? d["cover"].AsString() : null, MeshFile = d.ContainsKey("mesh") ? d["mesh"].AsString() : null, IncludesShoes = d.ContainsKey("includes_shoes") && d["includes_shoes"].AsBool() });
			}
			Groups[kv.Key.AsString()] = list;
		}
	}

	public static List<Item> Items(string group)
	{
		EnsureLoaded();
		return Groups.TryGetValue(group, out var l) ? l : new List<Item>();
	}
}

/// <summary>
/// Choix d'apparence. Les dix octets historiques sont suivis des réglages détaillés si nécessaire.
/// </summary>
public sealed class CharacterAppearance
{
	public const string SavePath = "user://character.json";

	public int Skin;
	public Dictionary<string,float> Details = new();
	public float Detail(string id) => Details.TryGetValue(id, out var v) ? v : id == "macrodetails/Caucasian" ? 1f : 0f;
	public float Years => Age < 0 ? 25 + 7 * Age : 25 + 65 * Age;
	public void SetYears(float years) => Age = float.IsFinite(years) ? Mathf.Clamp((years - 25) / (years < 25 ? 7 : 65), -1, 1) : 0;
	public void SetDetail(string id, float value)
	{
		void Store(string key,float v)
		{
			if (Mathf.IsEqualApprox(v, key == "macrodetails/Caucasian" ? 1 : 0)) Details.Remove(key);
			else Details[key] = v;
		}
		if (id.StartsWith("macrodetails/"))
		{
			var others = System.Array.FindAll(new[] {"macrodetails/African","macrodetails/Asian","macrodetails/Caucasian"}, k => k != id);
			float sum = Detail(others[0]) + Detail(others[1]);
			float share = sum > .00001f ? Detail(others[0])/sum : .5f;
			Store(others[0], (1-value)*share);
			Store(others[1], (1-value)*(1-share));
		}
		Store(id,value);
	}
	/// <summary>0 = masculin, 1 = féminin.</summary>
	public float Gender;
	/// <summary>-1 (mince) à 1 (fort).</summary>
	public float Weight;
	/// <summary>-1 (frêle) à 1 (musclé).</summary>
	public float Muscle;
	/// <summary>-1 (petit) à 1 (grand).</summary>
	public float Height;
	/// <summary>-1 (jeune) à 1 (âgé).</summary>
	public float Age;
	/// <summary>Index dans le groupe, -1 = rien.</summary>
	public Dictionary<string, int> Pieces = new() { { "clothes", 4 }, { "shoes", 2 }, { "hair", 4 }, { "eyebrows", 0 } };

	/// <summary>Personnage choisi : celui des parties solo et en ligne. Défini par la bibliothèque (<see cref="CharacterLibrary"/>).</summary>
	public static CharacterAppearance Current { get; set; } = CharacterLibrary.SelectedAppearance();

	public CharacterAppearance Clone()
	{
		var c = (CharacterAppearance)MemberwiseClone();
		c.Details = new Dictionary<string,float>(Details);
		c.Pieces = new Dictionary<string, int>(Pieces);
		return c;
	}

	public int Piece(string group) => Pieces.TryGetValue(group, out var i) ? i : -1;

	public void Randomize(RandomNumberGenerator rng)
	{
		CharacterKit.EnsureLoaded();
		Skin = rng.RandiRange(0, Math.Max(0, CharacterKit.Skins.Count - 1));
		Gender = rng.Randf() < 0.5f ? 0f : 1f;
		Weight = rng.RandfRange(-0.6f, 0.8f);
		Muscle = rng.RandfRange(-0.4f, 0.9f);
		Height = rng.RandfRange(-0.7f, 0.7f);
		Age = rng.RandfRange(-0.5f, 0.6f);
		CharacterDetails.LoadCatalog();
		Details.Clear();
		// Start with a coherent silhouette and keep left/right features paired.
		foreach (var dial in CharacterDetails.Dials)
		{
			bool left = dial.Id.Contains("/l-");
			if (dial.Id.Contains("/r-") || dial.Id.StartsWith("macrodetails/") ||
				dial.Id.StartsWith("bodyshapes/") || rng.Randf() >= .045f) continue;
			float value = Mathf.Snapped(rng.RandfRange(dial.Signed ? -.35f : .05f, .35f), .01f);
			SetDetail(dial.Id,value);
			if (left) SetDetail(dial.Id.Replace("/l-","/r-"),value);
		}
		foreach (var g in CharacterKit.Choosable)
			Pieces[g] = rng.RandiRange(g == "headwear" ? -1 : 0, Math.Max(0, CharacterKit.Items(g).Count - 1));
	}

	// ------------------------------------------------------------------ réseau

	private static byte Q(float v01) => (byte)Mathf.Clamp(Mathf.RoundToInt(v01 * 255f), 0, 255);
	private static float Sgn(byte b) => b / 255f * 2f - 1f;

	public byte[] Pack()
	{
		CharacterDetails.LoadCatalog();
		var b = new byte[10 + (Details.Count == 0 ? 0 : CharacterDetails.Dials.Count)];
		for (int i = 10; i < b.Length; i++)
		{
			var dial = CharacterDetails.Dials[i-10];
			float v = Detail(dial.Id);
			b[i] = unchecked((byte)(sbyte)Mathf.RoundToInt(127 * Mathf.Clamp(v,-1,1)));
		}
		b[0] = (byte)Skin;
		b[1] = Q(Gender);
		b[2] = Q((Weight + 1f) / 2f);
		b[3] = Q((Muscle + 1f) / 2f);
		b[4] = Q((Height + 1f) / 2f);
		b[5] = Q((Age + 1f) / 2f);
		b[6] = (byte)(Piece("clothes") + 1);
		b[7] = (byte)(Piece("shoes") + 1);
		b[8] = (byte)(Piece("hair") + 1);
		// Preserve the first four headwear codes in bits 6-7. Bits 2-5 extend the catalog.
		int hat = Math.Clamp(Piece("headwear") + 1, 0, 63);
		b[9] = (byte)(((Piece("eyebrows") + 1) & 3) | (hat & 60) | ((hat & 3) << 6));
		return b;
	}

	public static CharacterAppearance Unpack(byte[] b)
	{
		CharacterKit.EnsureLoaded();
		var a = new CharacterAppearance();
		if (b == null || b.Length < 10) return Default();
		a.Skin = CharacterKit.Skins.Count > 0 ? b[0] % CharacterKit.Skins.Count : 0;
		CharacterDetails.LoadCatalog();
		if (b.Length > 10)
			for (int j = 0; j < CharacterDetails.Dials.Count && 10+j < b.Length; j++)
			{
				var dial = CharacterDetails.Dials[j];
				float v = Mathf.Clamp(unchecked((sbyte)b[10+j])/127f,dial.Signed ? -1 : 0,1);
				if (!Mathf.IsEqualApprox(v,a.Detail(dial.Id))) a.Details[dial.Id] = v;
			}
		a.Gender = b[1] / 255f;
		a.Weight = Sgn(b[2]); a.Muscle = Sgn(b[3]); a.Height = Sgn(b[4]); a.Age = Sgn(b[5]);
		int i = 6;
		foreach (var g in CharacterKit.Choosable)
		{
			int count = CharacterKit.Items(g).Count;
			int v = g switch { "headwear" => (b[9] >> 6) | (b[9] & 60), "eyebrows" => b[9] & 3, _ => b[i++] };
			a.Pieces[g] = v == 0 || count == 0 ? -1 : (v - 1) % count;
		}
		return a;
	}

	public static CharacterAppearance Default() => new CharacterAppearance();

	// ------------------------------------------------------------------ sauvegarde

	public Godot.Collections.Dictionary ToDict()
	{
		var d = new Godot.Collections.Dictionary
		{
			{ "skin", Skin }, { "gender", Gender }, { "weight", Weight }, { "muscle", Muscle }, { "height", Height }, { "age", Age }
		};
		var detail = new Godot.Collections.Dictionary();
		if (Details.Count > 0)
		{
			CharacterDetails.LoadCatalog();
			for (int i = 0; i < CharacterDetails.Dials.Count; i++)
				if (Details.TryGetValue(CharacterDetails.Dials[i].Id, out float v)) detail[i.ToString()] = Mathf.RoundToInt(100*v);
		}
		d["details"] = detail;
		d["years"] = Years;
		foreach (var kv in Pieces) d["piece_" + kv.Key] = kv.Value;
		return d;
	}

	public static CharacterAppearance FromDict(Godot.Collections.Dictionary d)
	{
		var a = new CharacterAppearance();
		float F(string k, float def) => d.ContainsKey(k) ? (float)d[k].AsDouble() : def;
		a.Skin = d.ContainsKey("skin") ? d["skin"].AsInt32() : 0;
		a.Gender = F("gender", 0); a.Weight = F("weight", 0); a.Muscle = F("muscle", 0); a.Height = Mathf.Clamp(F("height", 0), -1, 1); a.Age = Mathf.Clamp(F("age", 0),-1,1);
		a.SetYears(F("years", a.Age < 0 ? 25 + 4.8f*a.Age : a.Years));
		if (d.TryGetValue("details", out var detail) && detail.VariantType == Variant.Type.Dictionary)
		{
			CharacterDetails.LoadCatalog();
			var saved = detail.AsGodotDictionary();
			for (int i = 0; i < CharacterDetails.Dials.Count; i++)
			{
				var dial = CharacterDetails.Dials[i];
				if (saved.TryGetValue(i.ToString(), out var value))
					a.Details[dial.Id] = Mathf.Clamp(value.AsInt32()/100f, dial.Signed ? -1 : 0, 1);
			}
		}
		foreach (var g in CharacterKit.Choosable)
			if (d.ContainsKey("piece_" + g)) a.Pieces[g] = d["piece_" + g].AsInt32();
		return a;
	}

	/// <summary>Ancienne sauvegarde d'un seul personnage (reprise par la bibliothèque au premier lancement).</summary>
	public void Save()
	{
		using var f = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
		f?.StoreString(Json.Stringify(ToDict()));
	}

	public static CharacterAppearance LoadSaved()
	{
		var a = new CharacterAppearance();
		try
		{
			if (!Godot.FileAccess.FileExists(SavePath)) return a;
			using var f = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read);
			var d = Json.ParseString(f.GetAsText()).AsGodotDictionary();
			if (d != null) a = FromDict(d);
		}
		catch (Exception e) { GD.PrintErr("[CharacterAppearance] lecture impossible : " + e.Message); }
		return a;
	}
}

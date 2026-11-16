using Godot;

namespace Openesport.Online
{
	/// <summary>Une ligne de la liste d'objets d'un point de butin : l'objet et son poids (plus il est grand, plus l'objet tombe souvent ; 1 par défaut).</summary>
	[Tool]
	[GlobalClass]
	public partial class LootEntry : Resource
	{
		[Export] public string Item { get; set; } = "ak";
		[Export(PropertyHint.Range, "0,100,0.1,or_greater")] public float Weight { get; set; } = 1f;

		public override void _ValidateProperty(Godot.Collections.Dictionary property)
		{
			if (property["name"].AsString() == nameof(Item))
			{
				property["hint"] = (int)PropertyHint.Enum;
				property["hint_string"] = LootPoint.ItemNames(false);
			}
		}
	}
}

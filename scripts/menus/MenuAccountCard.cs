using System;
using Godot;
using Openesport.Online;
using Openesport.Utils;

namespace Openesport.Menus
{
	/// <summary>
	/// Encart en haut à droite du menu principal, sous le nom du compte connecté : le classement général, un résumé de la dernière
	/// partie (mode, résultat, temps, éliminations, dégâts, points) et un lien vers le profil avec toutes les statistiques.
	/// Les données viennent du profil du compte, rechargé à l'affichage du menu (au retour d'une partie notamment).
	/// </summary>
	public partial class MenuAccountCard : PanelContainer
	{
		/// <summary>« Voir mon profil » : ouvre la page profil et statistiques.</summary>
		public Action OpenProfile;

		private const double RefreshSeconds = 60;
		private VBoxContainer _box;
		private double _sinceRefresh = RefreshSeconds;
		private bool _busy;
		private string _signature = "";

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Pass;
			AddThemeStyleboxOverride("panel", MenuStyle.CardStyle(12));
			SetAnchorsPreset(LayoutPreset.TopRight);
			OffsetLeft = -340; OffsetRight = -24; OffsetTop = 58;
			GrowHorizontal = GrowDirection.Begin;
			_box = new VBoxContainer();
			_box.AddThemeConstantOverride("separation", 4);
			AddChild(_box);
			// le menu revient (fin de partie, retour d'un écran) : statistiques rechargées tout de suite
			VisibilityChanged += () => { if (IsVisibleInTree()) _sinceRefresh = RefreshSeconds; };
		}

		public override void _Process(double delta)
		{
			if (!IsVisibleInTree() || !AccountClient.LoggedIn) return;
			_sinceRefresh += delta;
			if (!_busy && _sinceRefresh >= RefreshSeconds) { _sinceRefresh = 0; Refresh(); }
			Rebuild();
		}

		private async void Refresh()
		{
			_busy = true;
			try { await AccountClient.RefreshAsync(); }
			finally { _busy = false; }
		}

		private void Rebuild()
		{
			var p = AccountClient.Profile;
			if (p == null) return;
			var m = p.LastMatch;
			string sig = $"{p.General.Rating}|{p.General.Rank}|{p.General.Matches}|{m?.At}|{m?.Ago}";
			if (sig == _signature) return;
			_signature = sig;
			foreach (var c in _box.GetChildren()) c.QueueFree();

			_box.AddChild(Title(I18n.T("Classement général")));
			_box.AddChild(p.General.Matches == 0
				? Muted(I18n.T("Non classé : joue une partie pour obtenir un score."))
				: p.General.Rank > 0
					? Line(I18n.T("Rang #{0}  ·  {1} pts", p.General.Rank, p.General.Rating))
					: Line(I18n.T("{0} pts  ·  classé après 3 parties ({1}/3)", p.General.Rating, p.General.Matches)));

			_box.AddChild(Title(I18n.T("Dernière partie")));
			if (m == null) _box.AddChild(Muted(I18n.T("Aucune partie enregistrée pour le moment.")));
			else
			{
				_box.AddChild(Muted($"{m.ModeText}  ·  {m.Ago}"));
				_box.AddChild(Line($"{m.Result}  ·  {m.Duration}  ·  {m.DeltaText} pts"));
				_box.AddChild(Line(I18n.T("{0} élim.  ·  {1} morts  ·  {2} dégâts", m.Kills, m.Deaths, m.Damage.ToString("N0"))));
			}

			var link = new LinkButton { Text = I18n.T("Voir mon profil et toutes mes stats ›"), Underline = LinkButton.UnderlineMode.OnHover };
			link.AddThemeFontSizeOverride("font_size", 15);
			link.Pressed += () => OpenProfile?.Invoke();
			_box.AddChild(link);
		}

		private static Label Title(string text)
		{
			var l = new Label { Text = text };
			l.AddThemeFontSizeOverride("font_size", 16);
			return l;
		}

		private static Label Line(string text)
		{
			var l = new Label { Text = text };
			l.AddThemeFontSizeOverride("font_size", 15);
			return l;
		}

		private static Label Muted(string text)
		{
			var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
			l.AddThemeColorOverride("font_color", MenuStyle.Muted);
			l.AddThemeFontSizeOverride("font_size", 14);
			return l;
		}
	}
}

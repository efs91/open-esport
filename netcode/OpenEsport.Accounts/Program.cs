using System;

namespace OpenEsport.Accounts
{
	public static class Program
	{
		public static int Main(string[] args)
		{
			var o = new AccountsOptions { ServerKey = Environment.GetEnvironmentVariable("OE_ACCOUNTS_KEY") };
			try
			{
				for (int i = 0; i < args.Length; i++)
				{
					string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("valeur manquante après " + args[i]);
					switch (args[i])
					{
						case "--db": o.DbPath = Next(); break;
						case "--port": o.Port = int.Parse(Next()); break;
						case "--bind": o.Bind = Next(); break;
						case "--server-key": o.ServerKey = Next(); break;
						case "--help": case "-h": PrintHelp(); return 0;
						default: throw new ArgumentException("option inconnue : " + args[i]);
					}
				}
			}
			catch (Exception e) { Console.Error.WriteLine("Arguments invalides : " + e.Message); PrintHelp(); return 2; }

			var app = AccountsApp.Build(o);
			Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service de comptes : http://{o.Bind}:{o.Port}  (base : {o.DbPath}){(string.IsNullOrEmpty(o.ServerKey) ? "  — ATTENTION : pas de --server-key, les serveurs de partie ne peuvent pas vérifier les comptes" : "")}");
			Console.WriteLine("Les mots de passe transitent en clair vers ce service : placez-le derrière un proxy HTTPS pour un service en ligne.");
			app.Run();
			return 0;
		}

		private static void PrintHelp() => Console.WriteLine(@"Service de comptes open-esport : inscription, connexion, personnages, statistiques et classements.

Options :
  --db FICHIER        base SQLite (accounts.db)
  --port N            port HTTP (27090)
  --bind ADRESSE      adresse d'écoute (0.0.0.0)
  --server-key CLÉ    clé que présentent les serveurs de partie (ou variable OE_ACCOUNTS_KEY) ; sans elle, ils ne peuvent ni vérifier les comptes ni rapporter les parties");
	}
}

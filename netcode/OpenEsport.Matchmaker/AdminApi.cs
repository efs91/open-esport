using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenEsport.Core;

namespace OpenEsport.Matchmaker
{
	/// <summary>
	/// Télémétrie et administration, en HTTP : une page web (tableau de bord) et une API JSON.
	/// <list type="bullet">
	/// <item><c>GET /</c> : le tableau de bord (serveurs en cours, joueurs, scores, santé), rafraîchi toutes les 2 s ;</item>
	/// <item><c>GET /api/servers</c> : les mêmes données en JSON ;</item>
	/// <item><c>POST /api/servers/{nom}/stop</c> : arrête un serveur de partie (en-tête <c>X-Admin-Token</c> obligatoire ; désactivé sans jeton configuré).</item>
	/// </list>
	/// Par défaut le serveur HTTP n'écoute que sur 127.0.0.1 ; pour un service en ligne, le placer derrière un proxy HTTPS.
	/// </summary>
	public sealed class AdminApi
	{
		private readonly MatchmakerService _service;
		private readonly string _token;
		private HttpListener _http;
		public Action<string> Log = s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

		public AdminApi(MatchmakerService service, string token) { _service = service; _token = token; }

		public void Start(string bind, int port)
		{
			_http = new HttpListener();
			// HttpListener (Linux) refuse « 0.0.0.0 » : l'écoute sur toutes les interfaces s'écrit « + »
			string host = bind == "0.0.0.0" || bind == "*" ? "+" : bind;
			_http.Prefixes.Add($"http://{host}:{port}/");
			_http.Start();
			Log($"Tableau de bord : http://{(bind == "+" || bind == "*" ? "localhost" : bind)}:{port}/  (arrêt des serveurs {(string.IsNullOrEmpty(_token) ? "désactivé : aucun jeton --admin-token" : "autorisé avec le jeton")})");
			Task.Run(LoopAsync);
		}

		public void Stop() { try { _http?.Stop(); } catch { } }

		private async Task LoopAsync()
		{
			while (_http != null && _http.IsListening)
			{
				HttpListenerContext ctx;
				try { ctx = await _http.GetContextAsync(); }
				catch (Exception) { break; }
				_ = Task.Run(() => Handle(ctx));
			}
		}

		private void Handle(HttpListenerContext ctx)
		{
			try
			{
				string path = ctx.Request.Url.AbsolutePath;
				if (ctx.Request.HttpMethod == "GET" && path == "/") { Send(ctx, 200, "text/html; charset=utf-8", Dashboard.Html); return; }
				if (ctx.Request.HttpMethod == "GET" && path == "/api/servers") { Send(ctx, 200, "application/json", ServersJson()); return; }
				if (ctx.Request.HttpMethod == "GET" && path == "/stats") { Send(ctx, 200, "text/html; charset=utf-8", StatsPage.Value); return; }
				if (ctx.Request.HttpMethod == "GET" && path == "/api/stats")
				{
					int.TryParse(ctx.Request.QueryString["days"], out int days);
					string json = _service.UsageStatsAsync(Math.Clamp(days, 1, 760)).GetAwaiter().GetResult();
					if (json == null) Send(ctx, 503, "application/json", "{\"error\":\"service de comptes injoignable\"}");
					else Send(ctx, 200, "application/json", json);
					return;
				}
				if (ctx.Request.HttpMethod == "GET" && path == "/api/health") { Send(ctx, 200, "application/json", "{\"ok\":true}"); return; }
				if (ctx.Request.HttpMethod == "POST" && path.StartsWith("/api/servers/", StringComparison.Ordinal) && path.EndsWith("/stop", StringComparison.Ordinal))
				{
					string name = Uri.UnescapeDataString(path.Substring("/api/servers/".Length, path.Length - "/api/servers/".Length - "/stop".Length));
					var (code, body) = StopRequest(name, ctx.Request.Headers["X-Admin-Token"]);
					Send(ctx, code, "application/json", body);
					return;
				}
				Send(ctx, 404, "application/json", "{\"error\":\"introuvable\"}");
			}
			catch (Exception e)
			{
				try { Send(ctx, 500, "application/json", JsonSerializer.Serialize(new { error = e.Message })); } catch { }
			}
		}

		/// <summary>Demande d'arrêt d'un serveur : vérifie le jeton, puis arrête le conteneur.</summary>
		public (int code, string body) StopRequest(string name, string token)
		{
			if (string.IsNullOrEmpty(_token)) return (403, "{\"error\":\"arrêt désactivé : le matchmaking n'a pas de jeton d'administration (--admin-token)\"}");
			if (token != _token) return (401, "{\"error\":\"jeton invalide\"}");
			if (!_service.StopServer(name)) return (404, "{\"error\":\"serveur inconnu\"}");
			return (200, JsonSerializer.Serialize(new { stopped = name }));
		}

		public string ServersJson()
		{
			var list = _service.Servers;
			var now = DateTime.UtcNow;
			var servers = new List<object>();
			foreach (var s in list)
			{
				object telemetry = null;
				if (!string.IsNullOrEmpty(s.TelemetryJson))
				{
					try { telemetry = JsonDocument.Parse(s.TelemetryJson).RootElement.Clone(); } catch { }
				}
				servers.Add(new
				{
					name = s.Spec.Name, mode = Matchmaking.ModeName(s.Spec.TeamSize), modeLabel = Matchmaking.ModeLabel(s.Spec.TeamSize), port = s.Spec.Port, teamSize = s.Spec.TeamSize,
					ready = s.Ready, phase = (int)s.Phase, humans = s.Humans, needed = s.Needed, max = s.Max,
					uptimeSeconds = (int)(now - s.LaunchedAt).TotalSeconds,
					lastReplySeconds = s.LastReply == DateTime.MinValue ? -1 : (int)(now - s.LastReply).TotalSeconds,
					telemetry
				});
			}
			int humans = list.Sum(s => s.Humans);
			return JsonSerializer.Serialize(new
			{
				time = DateTime.UtcNow, canStop = !string.IsNullOrEmpty(_token),
				totals = new { servers = list.Count, humans, inGame = list.Count(s => s.Phase == MatchPhase.Playing), waiting = list.Count(s => s.Ready && s.Phase == MatchPhase.Lobby) },
				servers
			});
		}

		private static void Send(HttpListenerContext ctx, int code, string contentType, string body)
		{
			var bytes = Encoding.UTF8.GetBytes(body);
			ctx.Response.StatusCode = code;
			ctx.Response.ContentType = contentType;
			ctx.Response.Headers["Cache-Control"] = "no-store";
			ctx.Response.ContentLength64 = bytes.Length;
			ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
			ctx.Response.Close();
		}
	}

	/// <summary>Page des statistiques d'utilisation (StatsPage.html, ressource intégrée).</summary>
	internal static class StatsPage
	{
		public static readonly Lazy<string> Lazy = new Lazy<string>(() =>
		{
			using var s = typeof(StatsPage).Assembly.GetManifestResourceStream("StatsPage.html");
			return s == null ? "<p>Page des statistiques absente.</p>" : new System.IO.StreamReader(s).ReadToEnd();
		});
		public static string Value => Lazy.Value;
	}

	/// <summary>La page du tableau de bord (une seule page, sans dépendance).</summary>
	internal static class Dashboard
	{
		public const string Html = @"<!doctype html>
<html lang=""fr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Open Esport — serveurs</title>
<style>
:root{--bg:#0e1116;--panel:#171b22;--line:#262c36;--text:#e6e9ee;--mute:#8b94a3;--ok:#3ecf8e;--warn:#f5b73b;--bad:#ef5b5b;--acc:#5aa9ff}
@media (prefers-color-scheme:light){:root{--bg:#f4f6f9;--panel:#fff;--line:#dfe3ea;--text:#1b2230;--mute:#5d6676}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,Segoe UI,sans-serif}
header{padding:18px 24px;border-bottom:1px solid var(--line);display:flex;gap:24px;align-items:baseline;flex-wrap:wrap}
h1{font-size:20px;margin:0}.sum{display:flex;gap:18px;color:var(--mute)}.sum b{color:var(--text);font-size:18px}
main{padding:20px 24px;display:grid;gap:16px;grid-template-columns:repeat(auto-fill,minmax(420px,1fr))}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
.card h2{font-size:16px;margin:0 0 4px;display:flex;justify-content:space-between;gap:8px}
.tag{font-size:12px;border-radius:99px;padding:2px 9px;border:1px solid var(--line);color:var(--mute)}
.tag.lobby{color:var(--warn);border-color:var(--warn)}.tag.play{color:var(--ok);border-color:var(--ok)}.tag.over{color:var(--bad);border-color:var(--bad)}
.meta{color:var(--mute);font-size:13px;margin-bottom:8px}
table{width:100%;border-collapse:collapse;font-size:13px}th,td{text-align:left;padding:3px 6px;border-bottom:1px solid var(--line)}
th{color:var(--mute);font-weight:500}td.n{text-align:right;font-variant-numeric:tabular-nums}.dead{opacity:.45}
button{background:transparent;color:var(--bad);border:1px solid var(--bad);border-radius:6px;padding:4px 10px;cursor:pointer}
button:hover{background:var(--bad);color:#fff}.empty{color:var(--mute);padding:40px;text-align:center;grid-column:1/-1}
footer{padding:12px 24px;color:var(--mute);font-size:12px}
</style></head><body>
<header><h1>Open Esport — serveurs de partie</h1><a href=""/stats"" style=""color:var(--acc)"">Statistiques d'utilisation →</a>
<div class=""sum""><span><b id=""t_servers"">0</b> serveurs</span><span><b id=""t_humans"">0</b> joueurs</span><span><b id=""t_play"">0</b> en partie</span><span><b id=""t_wait"">0</b> en attente</span></div></header>
<main id=""list""><div class=""empty"">Chargement…</div></main>
<footer id=""foot"">—</footer>
<script>
const PH=['Salle d\'attente','En partie','Terminée'], CL=['lobby','play','over'];
let canStop=false;
const esc=s=>String(s).replace(/[&<>""']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]));
const dur=s=>s<60?s+' s':Math.floor(s/60)+' min '+(s%60)+' s';
async function stop(name){
  if(!confirm('Arrêter le serveur '+name+' ? Les joueurs seront déconnectés.'))return;
  let t=localStorage.getItem('oe-token')||prompt('Jeton d\'administration');if(!t)return;
  const r=await fetch('/api/servers/'+encodeURIComponent(name)+'/stop',{method:'POST',headers:{'X-Admin-Token':t}});
  if(r.status===401){localStorage.removeItem('oe-token');alert('Jeton invalide');return;}
  localStorage.setItem('oe-token',t);
  if(!r.ok)alert((await r.json()).error);refresh();
}
function card(s){
  const t=s.telemetry, ph=t?t.phase:s.phase;
  let h='<div class=""card""><h2><span>'+esc(s.name)+'</span><span class=""tag '+CL[ph]+'"">'+(s.ready?PH[ph]:'Démarrage…')+'</span></h2>';
  h+='<div class=""meta"">'+esc(s.modeLabel)+' · port '+s.port+' · '+s.humans+'/'+s.max+' humains · actif '+dur(s.uptimeSeconds);
  if(t)h+=' · vivants '+t.alive+' ('+t.teams+' éq.) · bots '+t.bots+' · tick '+t.tickMeanMs+' ms (max '+t.tickMaxMs+')';
  h+='</div>';
  if(t&&ph===0)h+='<div class=""meta"">En attente : '+s.humans+'/'+s.needed+' joueurs'+(t.countdown?' · départ dans '+t.countdown+' s':'')+'</div>';
  if(t&&t.players.length){
    h+='<table><tr><th>Joueur</th><th>Éq.</th><th class=""n"">Élim.</th><th class=""n"">Dégâts</th><th></th></tr>';
    for(const p of t.players){const bot=p[3]&1,alive=p[3]&2,down=p[3]&4;
      h+='<tr class=""'+(alive?'':'dead')+'""><td>'+esc(p[1])+(bot?' <span class=""tag"">bot</span>':'')+'</td><td>'+p[2]+'</td><td class=""n"">'+p[4]+'</td><td class=""n"">'+p[5]+'</td><td>'+(down?'à terre':(alive?'':'éliminé #'+p[6]))+'</td></tr>';}
    h+='</table>';
    if(t.humans+t.bots>t.players.length)h+='<div class=""meta"">… '+(t.humans+t.bots-t.players.length)+' autres joueurs</div>';
  }
  if(canStop)h+='<p><button onclick=""stop(\''+esc(s.name)+'\')"">Arrêter ce serveur</button></p>';
  return h+'</div>';
}
async function refresh(){
  try{
    const d=await (await fetch('/api/servers')).json();canStop=d.canStop;
    t_servers.textContent=d.totals.servers;t_humans.textContent=d.totals.humans;t_play.textContent=d.totals.inGame;t_wait.textContent=d.totals.waiting;
    list.innerHTML=d.servers.length?d.servers.map(card).join(''):'<div class=""empty"">Aucun serveur de partie en cours. Ils sont lancés à la demande des joueurs.</div>';
    foot.textContent='Mis à jour '+new Date().toLocaleTimeString()+(canStop?'':' · arrêt des serveurs désactivé (aucun jeton configuré)');
  }catch(e){foot.textContent='Matchmaking injoignable';}
}
refresh();setInterval(refresh,2000);
</script></body></html>";
	}
}

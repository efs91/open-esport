// Surfaces de l'arène « Relais » : un seul matériau pour tous les volumes (murs, dalles, piliers, plafond).
// Projection dans le repère du monde (les motifs se prolongent d'une boîte à l'autre) ; l'aspect dépend de l'orientation de la face et du niveau :
//  - dessus (sols)   : plaques métalliques, une matière et une teinte par niveau (gris au sol, bleu à +4 m, violet à +8 m) ;
//  - dessous         : plafond en tôle perforée sombre ;
//  - côtés (murs)    : plinthe sombre, liseré lumineux à la couleur du niveau, bandeau de tôle nervurée, béton à joints, corniche avec un second liseré.
shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

uniform sampler2D floor0_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D floor0_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D floor1_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D floor1_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D floor2_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D floor2_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D wall_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D wall_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D panel_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D panel_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ceiling_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ceiling_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;

uniform float floor_tile = 4.0;
uniform float wall_tile = 4.0;
uniform float panel_tile = 2.0;
uniform float level_height = 4.0;
uniform vec3 tint0 : source_color = vec3(0.78, 0.79, 0.82);
uniform vec3 tint1 : source_color = vec3(0.62, 0.74, 0.95);
uniform vec3 tint2 : source_color = vec3(0.76, 0.64, 0.95);
uniform vec3 wall_tint : source_color = vec3(0.78, 0.8, 0.86);
uniform vec3 accent0 : source_color = vec3(1.0, 0.52, 0.12);
uniform vec3 accent1 : source_color = vec3(0.2, 0.62, 1.0);
uniform vec3 accent2 : source_color = vec3(0.72, 0.32, 1.0);
uniform float accent_energy = 3.0;

varying vec3 world_pos;
varying vec3 world_n;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec3 accent_of(float level) {
	return level < 0.5 ? accent0 : (level < 1.5 ? accent1 : accent2);
}

float band(float h, float a, float b) {
	float w = fwidth(h) * 1.5;
	return smoothstep(a - w, a + w, h) * (1.0 - smoothstep(b - w, b + w, h));
}

void fragment() {
	vec3 p = world_pos;
	vec3 n = world_n;
	vec3 albedo;
	vec3 nmap = vec3(0.5, 0.5, 1.0);
	float rough = 0.6;
	float metal = 0.0;
	vec3 emit = vec3(0.0);

	if (n.y > 0.5) {
		// sol : le niveau est celui du dessus de la dalle
		float level = clamp(floor((p.y + 0.5) / level_height), 0.0, 2.0);
		vec2 uv = p.xz / floor_tile;
		if (level < 0.5) { albedo = texture(floor0_albedo, uv).rgb * tint0; nmap = texture(floor0_normal, uv).rgb; metal = 0.55; rough = 0.45; }
		else if (level < 1.5) { albedo = texture(floor1_albedo, uv * 1.33).rgb * tint1; nmap = texture(floor1_normal, uv * 1.33).rgb; metal = 0.6; rough = 0.4; }
		else { albedo = texture(floor2_albedo, uv * 1.33).rgb * tint2; nmap = texture(floor2_normal, uv * 1.33).rgb; metal = 0.6; rough = 0.42; }
		// fin liseré lumineux le long des joints tous les 8 m (repères au sol)
		vec2 g = abs(fract(p.xz / 8.0 + 0.5) - 0.5) * 8.0;
		float line = 1.0 - smoothstep(0.02, 0.05, min(g.x, g.y));
		emit += accent_of(level) * line * 0.12;
	} else if (n.y < -0.5) {
		vec2 uv = p.xz / 3.0;
		albedo = texture(ceiling_albedo, uv).rgb * 0.55;
		nmap = texture(ceiling_normal, uv).rgb;
		metal = 0.7; rough = 0.5;
	} else {
		// mur : coordonnées le long de la face, hauteur dans le niveau
		float u = abs(n.x) > 0.5 ? p.z : p.x;
		float level = clamp(floor((p.y + 0.001) / level_height), 0.0, 2.0);
		float h = p.y - level * level_height;
		vec2 uvw = vec2(u, -p.y) / wall_tile;
		vec2 uvp = vec2(u, -p.y) / panel_tile;
		vec3 concrete = texture(wall_albedo, uvw).rgb * wall_tint;
		vec3 panel = texture(panel_albedo, uvp).rgb * 0.8;
		albedo = concrete; nmap = texture(wall_normal, uvw).rgb; rough = 0.85;
		if (h < 1.35) { albedo = panel; nmap = texture(panel_normal, uvp).rgb; metal = 0.6; rough = 0.45; }
		if (h < 0.3) { albedo = panel * 0.45; metal = 0.7; rough = 0.35; }
		if (h > 3.5) { albedo = panel * 0.5; nmap = texture(panel_normal, uvp).rgb; metal = 0.7; rough = 0.4; }
		// joints verticaux du béton tous les 4 m, joint horizontal à 2,6 m
		float gu = abs(fract(u / 4.0 + 0.5) - 0.5) * 4.0;
		float seam = (1.0 - smoothstep(0.015, 0.04, gu)) * step(1.35, h) * step(h, 3.5);
		seam = max(seam, band(h, 2.58, 2.62));
		albedo *= 1.0 - seam * 0.6;
		// liserés : bas du mur (couleur du niveau) et sous la corniche (couleur du niveau du dessus)
		emit += accent_of(level) * band(h, 0.3, 0.38) * accent_energy;
		emit += accent_of(min(level + 1.0, 2.0)) * band(h, 3.72, 3.78) * accent_energy * 0.8;
		// petites balises tous les 4 m sur le bandeau de tôle
		float beacon = (1.0 - smoothstep(0.05, 0.08, abs(fract(u / 4.0) - 0.5) * 4.0)) * band(h, 0.95, 1.05);
		emit += accent_of(level) * beacon * accent_energy * 0.7;
	}
	ALBEDO = albedo;
	NORMAL_MAP = nmap;
	ROUGHNESS = rough;
	METALLIC = metal;
	EMISSION = emit;
}

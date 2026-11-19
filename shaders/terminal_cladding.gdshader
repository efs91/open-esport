shader_type spatial;

uniform sampler2D aggregate : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_rough : repeat_enable, filter_linear_mipmap_anisotropic;
uniform bool freight = false;
varying vec3 position_ws;
varying vec3 face_ws;

void vertex() {
	position_ws = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	face_ws = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

void fragment() {
	vec2 wall = vec2(abs(face_ws.x) > 0.5 ? position_ws.z : position_ws.x, position_ws.y);
	vec2 uv = abs(face_ws.y) > 0.5 ? position_ws.xz : wall;
	vec3 stone = texture(aggregate, uv * 0.23).rgb;
	vec4 grain = texture(normal_rough, uv * 0.23);
	float course = fract(wall.y / 1.25);
	float panel = fract(wall.x / 3.0);
	float seam = smoothstep(0.0, 0.012 + fwidth(panel) * 0.15, min(panel, 1.0-panel));
	float pour = smoothstep(0.0, 0.025, min(course, 1.0-course));
	float rib = cos(6.283185 * wall.x / 0.28) * (1.0 - smoothstep(0.03, 0.17, fwidth(wall.x)));
	float metal = freight ? 1.0 : smoothstep(2.5, 2.53, wall.y);
	vec3 warm = vec3(0.48, 0.155, 0.045);
	vec3 cool = vec3(0.045, 0.215, 0.25);
	vec3 sector = mix(warm, cool, smoothstep(-22.0, 22.0, position_ws.x));
	vec3 coating = freight ? sector : mix(vec3(0.29,0.33,0.34), sector, 0.30);
	vec3 paint = coating * (0.82 + 0.14 * rib + 0.15 * grain.a);
	vec3 concrete = mix(vec3(0.15,0.17,0.175), stone, 0.65) * mix(0.67,1.0,seam*pour);
	vec3 color = mix(concrete, paint * mix(0.70,1.0,seam), metal);
	if (!freight) {
		float band = step(2.1,wall.y) * (1.0-step(2.42,wall.y));
		color = mix(color, sector * 1.4, band);
	}
	if (freight) {
		vec2 edge = min(UV, vec2(1.0) - UV);
		float rim = 1.0 - smoothstep(0.017,0.035,min(edge.x,edge.y));
		color = mix(color, coating * 0.43, rim);
	}
	color *= mix(0.60,1.0,smoothstep(0.0,0.75,wall.y));
	ALBEDO = color;
	METALLIC = metal * 0.38;
	ROUGHNESS = mix(0.84,0.5,metal);
	NORMAL_MAP = grain.rgb;
	NORMAL_MAP_DEPTH = mix(0.36,0.13,metal);
}

shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform sampler2D asphalt : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
uniform sampler2D surface_noise : repeat_enable, filter_linear_mipmap;
uniform vec4 dry_color : source_color = vec4(0.23, 0.25, 0.28, 1.0);
uniform float slab_size = 4.0;
varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 p = world_pos.xz;
	float basin = texture(surface_noise, p * 0.026).r;
	float aggregate = texture(surface_noise, p * 0.77).r;
	vec4 stones = texture(asphalt, p * 0.38);
	float water = smoothstep(0.48, 0.64, basin + 0.10 * (aggregate - 0.5) - 0.055 * (stones.a - 0.5));
	vec2 grid = p / slab_size;
	vec2 joint_dist = (0.5 - abs(fract(grid) - 0.5)) * slab_size;
	vec2 aa = max(fwidth(p), vec2(0.005));
	vec2 slab = smoothstep(vec2(0.0), aa + 0.023, joint_dist);
	float joint = (1.0 - min(slab.x, slab.y)) * (1.0 - smoothstep(0.08, 0.4, max(aa.x, aa.y)));
	ALBEDO = dry_color.rgb * (0.48 + 2.4 * stones.rgb + 0.18 * aggregate) * mix(1.0, 0.46, water) * (1.0 - joint * 0.45);
	ROUGHNESS = mix(0.84, 0.17, water);
	SPECULAR = mix(0.35, 0.65, water);
}

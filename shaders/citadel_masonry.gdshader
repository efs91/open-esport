shader_type spatial;
uniform sampler2D stone : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
varying vec3 p;
varying vec3 face;

void vertex() {
	p = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz;
	face = normalize(MODEL_NORMAL_MATRIX * NORMAL);
}

void fragment() {
	vec2 uv = abs(face.y) > 0.5 ? p.xz : vec2(abs(face.x) > 0.5 ? p.z : abs(p.x), p.y);
	vec3 grain = texture(stone, uv * 0.32).rgb;
	vec2 blocks = uv / vec2(1.65,0.76);
	blocks.x += 0.5 * mod(floor(blocks.y),2.0);
	vec2 cell = fract(blocks);
	vec2 edge = min(cell,1.0-cell) * vec2(1.65,0.76);
	float joint = smoothstep(0.012,0.037+0.012*grain.r,min(edge.x,edge.y));
	float tone = fract(sin(dot(floor(blocks),vec2(17.17,89.48))) * 4378.3);
	vec3 masonry = mix(vec3(0.19,0.215,0.225),vec3(0.41,0.39,0.33),tone*0.65);
	ALBEDO = mix(vec3(0.10,0.11,0.105),masonry*(0.62+grain*1.2),joint);
	ROUGHNESS = 0.89;
}

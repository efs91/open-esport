shader_type sky;

uniform vec4 zenith_color : source_color = vec4(0.36, 0.57, 0.78, 1.0);
uniform vec4 horizon_color : source_color = vec4(0.92, 0.80, 0.72, 1.0);
uniform vec4 ground_color : source_color = vec4(0.42, 0.43, 0.47, 1.0);
uniform sampler2D cloud_noise : repeat_enable, filter_linear_mipmap;
uniform float cloud_opacity : hint_range(0.0, 1.0) = 0.24;

void sky() {
	float height = EYEDIR.y;
	float altitude = pow(clamp(height, 0.0, 1.0), 0.48);
	vec3 daylight = mix(horizon_color.rgb, zenith_color.rgb, altitude);
	vec2 cloud_uv = EYEDIR.xz / max(height + 0.18, 0.18);
	float wisps = texture(cloud_noise, cloud_uv * vec2(0.21, 0.57)).r;
	float clouds = smoothstep(0.43, 0.69, wisps) * smoothstep(0.015, 0.20, height);
	daylight = mix(daylight, vec3(0.95, 0.89, 0.82), clouds * cloud_opacity);

	if (LIGHT0_ENABLED) {
		float alignment = max(dot(EYEDIR, LIGHT0_DIRECTION), 0.0);
		float halo = pow(alignment, 22.0) * 0.25;
		float disc = smoothstep(cos(0.006), cos(0.004), alignment);
		daylight += LIGHT0_COLOR * LIGHT0_ENERGY * (halo + 6.0 * disc);
	}

	COLOR = mix(daylight, ground_color.rgb, smoothstep(0.0, 0.55, -height));
}

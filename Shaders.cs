namespace CloudWallpaper;

internal static class Shaders
{
    public const string Vertex = @"#version 120
void main() { gl_Position = gl_Vertex; }
";

    // uField : 由图标位置生成的云朵密度场（低分辨率，行 0 = 屏幕顶部）
    // uBlobs : 自由飘动的云团 (中心 x, 中心 y, 影响半径 x, 影响半径 y)，像素坐标，原点在左上
    // uTheme : 0 = 天空，1 = 草原        uInvert : 1 = 没有图标的地方有云
    public const string Fragment = @"#version 120
uniform sampler2D uField;
uniform vec2  uRes;
uniform float uTime;        // 一直在走的时间（草地的风）
uniform float uCloudTime;   // 云的时间；关闭「云朵流动」后停住
uniform int   uPass;        // 0 = 整幅画面，1 = 只画背景，2 = 只画云（叠在角色上面）
uniform int   uExtra;       // 1 = 显示无图标的小云（天空主题里是远处的薄云）
uniform int   uShadow;      // 1 = 草原上显示云的影子
uniform float uScale;
uniform int   uTheme;
uniform int   uInvert;
uniform int   uBlobCount;
uniform vec4  uBlobs[32];

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
vec2 hash2(vec2 p) {
    return vec2(hash(p), hash(p + vec2(37.7, 91.3)));
}
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm(vec2 p) {
    float a = 0.5, s = 0.0;
    for (int i = 0; i < 5; i++) { s += a * noise(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
    return s;
}
float fbm3(vec2 p) {
    float a = 0.5, s = 0.0;
    for (int i = 0; i < 3; i++) { s += a * noise(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; }
    return s;
}

// 圆形云泡：到最近特征点的距离取反，让云的边缘由一段段圆弧组成
float puffs(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    float m = 1.0;
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++) {
        vec2 g = vec2(float(x), float(y));
        vec2 o = 0.5 + 0.36 * sin(uCloudTime * 0.22 + 6.2831 * hash2(i + g));
        vec2 r = g + o - f;
        m = min(m, dot(r, r));
    }
    return 1.0 - sqrt(m);
}

float freeField(vec2 pt) {
    float s = 0.0;
    for (int i = 0; i < 32; i++) {
        if (i >= uBlobCount) break;
        vec4 b = uBlobs[i];
        vec2 dl = (pt - b.xy) / b.zw;
        float d2 = dot(dl, dl);
        if (d2 < 1.0) { float v = 1.0 - d2; s += v * v; }
    }
    return s;
}

// 云的基础密度（pt 为左上原点的像素坐标）
float density(vec2 pt) {
    float d = texture2D(uField, pt / uRes).r + freeField(pt);
    if (uInvert == 1) {
        float big = fbm3(pt / (uScale * 420.0) + vec2(uCloudTime * 0.008, 0.0));
        // 整片云海，只在少数地方露出干净的天空缺口；图标周围挖空
        d = 1.1 - smoothstep(0.33, 0.23, big) - d * 1.7;
    }
    return d;
}

// 加上云泡起伏后的最终形状值，> 0.45 左右为云
float shape(vec2 pt, out float d) {
    d = density(pt);
    float m = smoothstep(0.08, 0.45, d);
    float bump = (puffs(pt / (uScale * 40.0)) - 0.62) * 0.40
               + (fbm3(pt / (uScale * 30.0) + vec2(-uCloudTime * 0.03, uCloudTime * 0.012)) - 0.45) * 0.16;
    return d + bump * m;
}

vec3 skyColor(vec2 uv, vec2 fc) {
    vec3 col = mix(vec3(0.60, 0.82, 0.98), vec3(0.09, 0.40, 0.88), pow(uv.y, 0.8));
    // 太阳与光晕（右上角）
    vec2 sd = (uv - vec2(0.83, 0.84)) * vec2(uRes.x / uRes.y, 1.0);
    float r = length(sd);
    col += vec3(1.0, 0.93, 0.75) * (0.30 * exp(-r * 4.5) + 0.55 * exp(-r * 22.0));
    col = mix(col, vec3(1.0, 0.99, 0.94), smoothstep(0.036, 0.028, r));
    if (uExtra == 0) return col;
    // 远处缓慢飘动的薄云
    vec2 bp = fc / (uScale * 460.0) * vec2(1.0, 2.3);
    bp.x += uCloudTime * 0.006;
    float b = fbm(bp + fbm3(bp * 2.0 + uCloudTime * 0.004) * 0.7);
    return mix(col, vec3(1.0), smoothstep(0.55, 0.85, b) * 0.45);
}

vec3 grassColor(vec2 pt) {
    vec2 g = pt / (uScale * 340.0);
    float n1 = fbm(g + vec2(3.1, 7.7));
    float n2 = fbm3(g * 3.1 + vec2(11.0, 2.0));
    vec3 col = mix(vec3(0.50, 0.68, 0.32), vec3(0.72, 0.83, 0.47), smoothstep(0.30, 0.68, n1));
    // 横向的笔触纹理
    float streak = noise(vec2(pt.x / (uScale * 150.0), pt.y / (uScale * 13.0)) + n2 * 0.8);
    col *= 0.965 + 0.07 * streak;
    // 深色的灌木 / 树林斑块
    float forest = smoothstep(0.56, 0.60, fbm3(g * 0.9 + vec2(31.0, 17.0)) + (n2 - 0.45) * 0.10);
    col = mix(col, vec3(0.37, 0.56, 0.27) * (0.92 + 0.16 * n2), forest * 0.85);
    // 风吹过草地的明暗波纹
    float wind = fbm3(g * 2.2 + vec2(uTime * 0.05, uTime * 0.012));
    col *= 0.96 + 0.09 * wind;
    return col;
}

void main() {
    vec2 fc = gl_FragCoord.xy;
    vec2 uv = fc / uRes;
    vec2 pt = vec2(fc.x, uRes.y - fc.y);
    float t = uCloudTime;

    // 整体的缓慢形变，让云边缘轻轻流动
    vec2 q = pt / (uScale * 90.0);
    vec2 wp = pt + (vec2(fbm3(q * 1.3 + vec2(t * 0.020, 3.7)),
                         fbm3(q * 1.3 + vec2(8.3, -t * 0.017))) - 0.45) * 14.0 * uScale;

    vec3 col = vec3(0.0);
    if (uPass != 2) {
        if (uTheme == 0) {
            col = skyColor(uv, fc);
        } else {
            col = grassColor(pt);
            if (uShadow == 1) {
                // 云投在地面上的影子（向右下偏移）
                float sdRaw;
                float sv = shape(wp - vec2(44.0, 56.0) * uScale, sdRaw);
                float sh = smoothstep(0.40, 0.52, sv);
                col = mix(col, col * vec3(0.52, 0.66, 0.50), sh * 0.85);
            }
        }
        if (uPass == 1) { gl_FragColor = vec4(col, 1.0); return; }
    }

    float d;
    float dd = shape(wp, d);
    if (dd <= 0.38 && uPass == 2) discard;
    if (dd > 0.38) {
        float a = smoothstep(0.42, 0.50, dd);

        // 明暗只取决于「到云边缘的距离」：密度截断在 0.8，
        // 多个图标融合后的内部完全一致，不会出现接缝
        float dc = min(d, 0.8);
        vec2 L = vec2(0.45, -0.9) * 20.0 * uScale;          // 朝向光源（屏幕上方偏右）
        float dl = min(density(wp + L), 0.8);
        float rim = clamp((dc - dl) * 1.6, -1.0, 1.0);

        float core = smoothstep(0.50, 0.80, dc);
        vec3 inner = uTheme == 0 ? vec3(0.83, 0.89, 0.98) : vec3(0.86, 0.89, 0.96);
        vec3 cloud = mix(vec3(1.0), inner, core * 0.85);
        cloud = mix(cloud, vec3(1.0), max(rim, 0.0) * 0.9);                       // 受光的上缘
        cloud = mix(cloud, vec3(0.66, 0.74, 0.90), max(-rim, 0.0) * 0.55);        // 背光的下缘
        // 靠近边缘处的云泡轮廓，增加蓬松的层次
        float lobes = puffs(wp / (uScale * 40.0));
        cloud -= vec3(0.10, 0.08, 0.04) * smoothstep(0.42, 0.30, lobes) * (1.0 - core);

        if (uPass == 2) { gl_FragColor = vec4(cloud, a); return; }   // 由混合叠到角色上面
        col = mix(col, cloud, a);
    }

    gl_FragColor = vec4(col, 1.0);
}
";
}

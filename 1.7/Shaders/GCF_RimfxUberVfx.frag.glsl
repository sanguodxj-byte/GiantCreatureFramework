#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _CoreColor;  // @color @default = vec4(0.84, 0.82, 0.78, 1.0)
    vec4 _BodyColor;  // @color @default = vec4(0.62, 0.58, 0.52, 1.0)
    vec4 _EdgeColor;  // @color @default = vec4(0.38, 0.36, 0.34, 1.0)
    vec4 _VfxParams1; // @default = vec4(0.0, 0.25, 0.0, 1.0)  x=mode, y=progress, z=ageSecs, w=seed
    vec4 _VfxParams2; // @default = vec4(0.85, 2.1, 1.0, 0.48) x=emissiveBoost, y=spanOrDir, z=subParam, w=masterAlpha
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

float sat(float x)
{
    return clamp(x, 0.0, 1.0);
}

vec3 sat3(vec3 v)
{
    return clamp(v, vec3(0.0), vec3(1.0));
}

float hash11(float p)
{
    return fract(sin(p * 91.173 + 17.31) * 43758.5453);
}

float hash21(vec2 p, float s)
{
    vec2 q = fract(p * vec2(123.34, 456.21) + vec2(s * 0.173));
    q += dot(q, q + 45.32);
    return fract(q.x * q.y);
}

float vnoiseQuintic(vec2 p, float s)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = hash21(i, s);
    float b = hash21(i + vec2(1.0, 0.0), s);
    float c = hash21(i + vec2(0.0, 1.0), s);
    float d = hash21(i + vec2(1.0, 1.0), s);
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm3(vec2 p, float s)
{
    float v = 0.0;
    float amp = 0.54;
    float norm = 0.0;
    mat2 rot = mat2(0.80, 0.60, -0.60, 0.80);
    for (int k = 0; k < 3; k++)
    {
        v += amp * vnoiseQuintic(p, s);
        norm += amp;
        p = (rot * p) * 2.02 + vec2(1.73, 4.19);
        amp *= 0.50;
    }
    return v / max(norm, 1e-4);
}

float segDist(vec2 p, vec2 a, vec2 b)
{
    vec2 pa = p - a;
    vec2 ba = b - a;
    float denom = max(dot(ba, ba), 1e-5);
    float h = sat(dot(pa, ba) / denom);
    return length(pa - ba * h);
}

vec2 rot2d(vec2 p, float a)
{
    float c = cos(a);
    float s = sin(a);
    return vec2(c * p.x - s * p.y, s * p.x + c * p.y);
}

vec3 desatAtmos(vec3 c, float keepSat)
{
    float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
    vec3 neutralAtmos = vec3(lum * 0.99, lum * 0.98, lum * 0.96);
    return mix(neutralAtmos, c, sat(keepSat));
}

vec4 composeSoftAdd(
    vec3 darkCol,
    float darkMask,
    vec3 edgeCol,
    float edgeW,
    vec3 bodyCol,
    float bodyW,
    vec3 coreCol,
    float coreW,
    float boost,
    float env)
{
    float e = sat(env);
    if (e <= 0.001)
    {
        return vec4(0.0);
    }

    // DeriveRimfxTriad already clamps saturation to muted atmospheric levels;
    // preserve subtle tonal warmth while preventing high-chroma cartoon colors.
    vec3 mEdge = desatAtmos(edgeCol, 0.88);
    vec3 mBody = desatAtmos(bodyCol, 0.86);
    vec3 mCore = desatAtmos(coreCol, 0.84);

    float mistDensity = sat(edgeW * 0.44 + bodyW * 0.66 + coreW * 0.72);
    float effDark = sat(darkMask) * (1.0 - mistDensity * 0.72);

    float wSum = max(edgeW * 0.22 + bodyW * 0.52 + coreW * 0.95, 1e-4);
    vec3 mistTint = (mEdge * (edgeW * 0.22) + mBody * (bodyW * 0.52) + mCore * (coreW * 0.95)) / wSum;

    float safeBoost = clamp(boost, 0.25, 1.35);
    vec3 vaporVal = mistTint * (1.58 * safeBoost);
    // Smooth highlight roll-off prevents blown-out 1.0 white cartoon cores while lifting above mid-gray stone
    vaporVal = vaporVal / (vec3(1.0) + vaporVal * 0.28);

    float darkA = effDark * 0.58 * e;
    float mistA = mistDensity * 0.85 * e;
    float outA = sat(darkA + mistA * (1.0 - darkA * 0.45));
    vec3 outRgb = darkCol * darkA + vaporVal * (mistA * 1.14);

    return vec4(sat3(outRgb), outA);
}

float ridgedFlameFbm(vec2 p, float s)
{
    float total = 0.0;
    float norm = 0.0;
    float amp = 0.55;
    float weight = 1.0;
    mat2 rot = mat2(0.80, 0.60, -0.60, 0.80);
    for (int k = 0; k < 4; k++)
    {
        float n = vnoiseQuintic(p, s + float(k) * 23.7);
        float ridge = 1.0 - abs(n * 2.0 - 1.0);
        ridge = ridge * ridge * weight;
        weight = clamp(ridge * 1.85, 0.15, 1.0);
        total += amp * ridge;
        norm += amp;
        p = (rot * p) * 2.08 + vec2(2.91, 1.37);
        amp *= 0.50;
    }
    return sat(total / max(norm, 1e-4));
}

vec3 blackbodyCrimsonFlameRamp(float temp)
{
    float t = sat(temp);
    vec3 c0 = vec3(0.13, 0.08, 0.08);
    vec3 c1 = vec3(0.78, 0.11, 0.08);
    vec3 c2 = vec3(0.94, 0.24, 0.10);
    vec3 c3 = vec3(0.98, 0.54, 0.15);
    vec3 c4 = vec3(1.00, 0.87, 0.50);

    vec3 col = mix(c0, c1, smoothstep(0.06, 0.28, t));
    col = mix(col, c2, smoothstep(0.26, 0.50, t));
    col = mix(col, c3, smoothstep(0.48, 0.76, t));
    col = mix(col, c4, smoothstep(0.74, 0.97, t));
    return col;
}

vec4 composeDragonFlame(float tempField, float outerSkirt, float masterAlpha, float boost)
{
    float t = sat(tempField) * sat(outerSkirt);
    if (t <= 0.002)
    {
        return vec4(0.0);
    }
    vec3 flameRgb = blackbodyCrimsonFlameRamp(t);
    float sootA = smoothstep(0.02, 0.14, t) * (1.0 - smoothstep(0.20, 0.45, t)) * 0.42 * masterAlpha;
    float emisA = smoothstep(0.10, 0.36, t) * 0.84 * masterAlpha;

    vec3 emisRgb = flameRgb * (1.38 * clamp(boost, 0.50, 1.40));
    emisRgb = emisRgb / (vec3(1.0) + emisRgb * 0.24);

    float outA = sat(sootA + emisA * 0.62);
    vec3 outRgb = vec3(0.11, 0.07, 0.07) * sootA + emisRgb * emisA;
    return vec4(sat3(outRgb), outA);
}

void main()
{
    vec2 uv = vUv;
    vec4 tex = vec4(1.0);
#ifdef USE_TEXTURE
    tex = texture(_MainTex, uv);
#endif
    float keepTex = 1.0 + (tex.r - 1.0) * 0.0001;

    float mode = uMaterial._VfxParams1.x;
    float prog = sat(uMaterial._VfxParams1.y);
    float age = max(0.0, uMaterial._VfxParams1.z);
    float seed = mod(abs(uMaterial._VfxParams1.w), 97.0) + 1.0;

    float boost = max(0.15, uMaterial._VfxParams2.x);
    float spanParam = uMaterial._VfxParams2.y;
    float subParam = uMaterial._VfxParams2.z;
    float masterAlpha = sat(uMaterial._VfxParams2.w) * sat(uMaterial._Color.a) * sat(vColor.a);

    vec3 coreCol = uMaterial._CoreColor.rgb;
    vec3 bodyCol = uMaterial._BodyColor.rgb;
    vec3 edgeCol = uMaterial._EdgeColor.rgb;
    vec3 darkCol = mix(vec3(0.045, 0.044, 0.046), desatAtmos(edgeCol, 0.20) * 0.22, 0.40);

    vec2 p = (uv - vec2(0.5)) * 2.0;
    float rCenter = length(p);
    // Wide Gaussian-like outer quad feathering: zero hard circular or square boundary
    float radialSoft = pow(sat(1.0 - rCenter * rCenter), 1.60);

    vec4 res = vec4(0.0);

    // =========================================================================
    // MODE 0: Blurred-Edge Dual-Whip Air-Shear Vapor Crescent (Zero Ink Outline)
    // =========================================================================
    if (mode < 0.5)
    {
        float whipQuadFade = 1.0 - smoothstep(0.74, 0.99, rCenter);
        if (whipQuadFade <= 0.001)
            discard;

        float dirSign = (spanParam >= 0.0) ? 1.0 : -1.0;
        float halfSpan = clamp(abs(spanParam) * 0.5, 0.45, 2.75);
        float px = p.x * dirSign;
        float py = p.y;

        vec2 qArc = vec2(px - 0.01, py + 0.08);
        float rq = length(qArc);
        float ang = atan(qArc.x, qArc.y);
        float uPhi = (ang + halfSpan) / max(2.0 * halfSpan, 1e-4);
        float uC = sat(uPhi);

        float swingIn = 1.0 - pow(max(1.0 - sat(prog / 0.22), 1e-4), 2.0);
        float uHead = mix(0.38, 1.04, swingIn);
        float tDecay = sat((prog - 0.20) / 0.80);

        float warp1 = fbm3(vec2(px * 2.5 + seed * 0.31, py * 2.4 - prog * 1.5), seed) - 0.5;
        float warp2 = fbm3(vec2(uC * 4.0 - prog * 1.8, rq * 3.6 + seed * 0.41), seed + 7.0) - 0.5;

        float bellySin = sin(sat(uPhi / max(uHead, 0.10)) * 3.14159265);
        float archBelly = pow(max(bellySin, 1e-4), 0.68);
        float rOuter = 0.68 - 0.05 * (1.0 - archBelly) + 0.055 * warp1;

        float maxW = clamp(subParam, 0.28, 0.58);
        float bladeSpan = maxW * (0.34 + 0.78 * archBelly * (0.86 + 0.28 * (warp1 + 0.5)));
        float rInner = rOuter - bladeSpan + 0.055 * warp2;

        // Wide Gaussian/smoothstep air-vapor feathering on all 4 sides of the sweeping crescent
        float inSweep = smoothstep(-0.12, 0.16, uPhi) * (1.0 - smoothstep(uHead - 0.26, uHead + 0.10, uPhi));
        float outerFeather = 1.0 - smoothstep(rOuter - 0.22, rOuter + 0.16, rq);
        float innerFeather = smoothstep(rInner - 0.24, rInner + 0.18, rq);
        float vaporArc = inSweep * outerFeather * innerFeather;

        float midR = mix(rInner, rOuter, 0.58);
        float ridge = exp(-pow((rq - midR) / max(bladeSpan * 0.48, 0.10), 2.0));
        float flowTurb = 0.58 + 0.42 * fbm3(vec2(uC * 5.6 - prog * 2.6, rq * 4.2), seed + 15.0);

        float erodeKeep = smoothstep(tDecay * 0.56 - 0.30, tDecay * 0.56 + 0.30, flowTurb * 0.75 + archBelly * 0.48);
        float lifeEnv = smoothstep(0.0, 0.07, prog) * pow(max(1.0 - prog, 1e-4), 1.12);
        float activeMist = vaporArc * erodeKeep * lifeEnv;

        float dustShadow = activeMist * (1.0 - ridge * 0.70) * 0.82;
        float edgeW = activeMist * (0.72 + 0.28 * flowTurb);
        float bodyW = activeMist * ridge * (0.80 + 0.20 * flowTurb);
        float coreW = activeMist * pow(ridge, 1.45) * archBelly * 0.90;

        res = composeSoftAdd(darkCol, dustShadow, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * whipQuadFade);
    }
    // =========================================================================
    // MODE 1: Diffused Ballistic Propellant Gas Cone & Muzzle Smoke Plume
    // =========================================================================
    else if (mode < 1.5)
    {
        float edgeBox = pow(sat(1.0 - p.x * p.x), 1.6) * pow(sat(1.0 - p.y * p.y), 1.5);
        if (edgeBox <= 0.001)
            discard;

        if (subParam > 1.8)
        {
            // Procedural Dragon-Breath Turbulent Flame Jet (Domain-Warped Ridged FBM + Blackbody Crimson Ramp)
            vec2 q = p - vec2(0.0, -0.68);
            float coneFade = sat(1.0 - prog * 1.16);
            float cu = q.y / 1.32;
            float warpX = fbm3(vec2(cu * 3.4 - prog * 5.2, q.x * 3.2), seed) - 0.5;
            float warpY = fbm3(vec2(cu * 3.4 - prog * 4.6 + 5.1, q.x * 3.2 + 2.3), seed + 3.1) - 0.5;
            float halfW = 0.12 + clamp(cu, 0.0, 1.4) * 0.46;
            float latNorm = (q.x + warpX * 0.26 * clamp(cu, 0.0, 1.3)) / max(halfW, 0.04);

            float ridges = ridgedFlameFbm(vec2(cu * 4.2 - prog * 6.5 + warpX * 1.4, latNorm * 2.8 + warpY * 1.3), seed + 7.7);
            float axialEnv = smoothstep(-0.08, 0.08, cu) * (1.0 - smoothstep(0.45, 1.24, cu + 0.22 * (1.0 - ridges)));
            float latEnv = exp(-1.85 * latNorm * latNorm);
            float erosion = 0.22 * smoothstep(0.35, 1.15, cu) + 0.18 * (1.0 - coneFade);
            float tempJet = sat(axialEnv * latEnv * (0.42 + 0.82 * ridges) - erosion) * coneFade;
            res = composeDragonFlame(tempJet, edgeBox, masterAlpha, boost);
        }
        else
        {
            vec2 q = p - vec2(0.0, -0.64);
            float expand = 1.0 - pow(max(1.0 - sat(prog / 0.22), 1e-4), 2.1);
            float decay = sat((prog - 0.14) / 0.86);
            float lifeEnv = smoothstep(0.0, 0.05, prog) * pow(max(1.0 - decay, 1e-4), 1.15);

            float jetLen = mix(0.45, 1.42, expand);
            float tY = q.y / max(jetLen, 0.10);
            float inJet = smoothstep(-0.08, 0.12, tY) * (1.0 - smoothstep(0.62, 1.04, tY));
            float nFlow = fbm3(vec2(q.x * 3.8, q.y * 2.9 - prog * 3.4), seed);

            float jetHalfW = (0.11 + 0.28 * sin(sat(tY) * 3.14159265)) * (0.85 + 0.35 * nFlow);
            float uJet = abs(q.x) / max(jetHalfW, 0.04);
            float jetMist = exp(-pow(uJet / 0.92, 1.65)) * inJet;
            float jetCore = exp(-pow(uJet / 0.44, 1.85)) * inJet * (1.0 - smoothstep(0.35, 0.88, tY)) * (1.0 - 0.55 * decay);

            vec2 qb = vec2(abs(q.x), q.y - 0.06);
            vec2 bAxis = rot2d(qb, -1.32);
            float tb = bAxis.y / max(mix(0.26, 0.72, expand), 0.08);
            float inBrake = smoothstep(-0.05, 0.12, tb) * (1.0 - smoothstep(0.55, 1.02, tb));
            float brakeMist = exp(-pow(abs(bAxis.x) / (0.14 + 0.16 * nFlow), 1.6)) * inBrake;

            float muzzleGlow = exp(-dot(q, q) * 9.5) * (1.0 - smoothstep(0.0, 0.48, prog)) * 0.75;

            float darkSmoke = sat((jetMist * 0.65 + brakeMist * 0.75) * (0.50 + 0.50 * nFlow)) * lifeEnv;
            float edgeW = sat(jetMist * 0.85 + brakeMist * 0.80) * lifeEnv;
            float bodyW = sat(jetMist * 0.62 + brakeMist * 0.48 + muzzleGlow * 0.65) * lifeEnv;
            float coreW = sat(jetCore * 0.78 + muzzleGlow * 0.72) * lifeEnv;

            res = composeSoftAdd(darkCol, darkSmoke, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * edgeBox);
        }
    }
    // =========================================================================
    // MODE 2: Diffused Ionized Muzzle Vapor Plume & Soft Pressure Shroud
    // =========================================================================
    else if (mode < 2.5)
    {
        float edgeBox = pow(sat(1.0 - p.x * p.x), 1.6) * pow(sat(1.0 - p.y * p.y), 1.5);
        if (edgeBox <= 0.001)
            discard;

        vec2 q = p - vec2(0.0, -0.64);
        float expand = 1.0 - pow(max(1.0 - sat(prog / 0.20), 1e-4), 2.2);
        float decay = sat((prog - 0.14) / 0.86);
        float lifeEnv = smoothstep(0.0, 0.05, prog) * pow(max(1.0 - decay, 1e-4), 1.12);

        float spearLen = mix(0.48, 1.46, expand);
        float tY = q.y / max(spearLen, 0.10);
        float inPlume = smoothstep(-0.08, 0.10, tY) * (1.0 - smoothstep(0.66, 1.04, tY));
        float nTurb = fbm3(vec2(q.x * 4.2 + seed * 0.2, q.y * 3.2 - prog * 3.8), seed + 3.0);

        float ax = abs(q.x);
        float plumeW = (0.09 + 0.20 * sin(sat(tY) * 3.14159265)) * (0.86 + 0.30 * nTurb);
        float outerSheath = exp(-pow(ax / max(plumeW * 1.45, 0.05), 1.55)) * inPlume;
        float bodyPlume = exp(-pow(ax / max(plumeW * 0.82, 0.03), 1.75)) * inPlume;
        float corePlume = exp(-pow(ax / max(plumeW * 0.38, 0.02), 1.90)) * inPlume * (1.0 - smoothstep(0.42, 0.92, tY));

        // Soft blurred aerodynamic condensation bow
        float yShroud = (0.28 + 0.44 * expand) - 1.45 * pow(max(ax, 1e-4), 1.45);
        float shroudSpan = smoothstep(0.04, 0.22, ax) * (1.0 - smoothstep(0.42, 0.78, ax));
        float aeroMist = exp(-pow((yShroud - q.y) / 0.14, 2.0)) * shroudSpan * (1.0 - smoothstep(0.28, 0.95, prog));

        float boreHaze = exp(-dot(q, q) * 10.5) * (1.0 - smoothstep(0.0, 0.50, prog)) * 0.70;

        float darkGuard = (outerSheath * 0.42 + aeroMist * 0.30) * lifeEnv;
        float edgeW = sat(outerSheath * 0.85 + aeroMist * 0.75) * lifeEnv;
        float bodyW = sat(bodyPlume * 0.75 + aeroMist * 0.65 + boreHaze * 0.55) * lifeEnv;
        float coreW = sat(corePlume * 0.72 + boreHaze * 0.68) * lifeEnv;

        res = composeSoftAdd(darkCol, darkGuard, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * edgeBox);
    }
    // =========================================================================
    // MODE 3: Blurred Atmospheric Pressure Wave & Feathered Dust Ring
    // =========================================================================
    else if (mode < 3.5)
    {
        if (radialSoft <= 0.001)
            discard;

        float expand = 1.0 - pow(max(1.0 - prog, 1e-4), 2.05);
        float ringR = mix(0.16, 0.72, expand);
        float ang = atan(p.y, p.x);

        float nLow = fbm3(vec2(cos(ang) * 2.3 + seed * 0.21, sin(ang) * 2.3 - prog * 1.1), seed);
        float nDetail = fbm3(p * 3.1 + vec2(prog * 0.45, -prog * 0.35), seed + 11.0);

        // Wide blurred Gaussian ring profile (sigma ~ 0.18..0.28) — zero sharp cartoon line
        float ringSigma = mix(0.17, 0.27, prog) * clamp(subParam, 0.70, 1.40);
        float rPert = rCenter + (nLow - 0.5) * 0.11 + (nDetail - 0.5) * 0.06;
        float ringWave = exp(-pow((rPert - ringR) / max(ringSigma, 0.08), 2.0)) * (0.68 + 0.36 * nLow);
        float innerMist = (1.0 - smoothstep(ringR * 0.35, ringR + 0.18, rPert)) * (0.34 + 0.28 * nDetail) * (1.0 - prog * 0.65);

        float erodeThr = sat((prog - 0.28) / 0.72) * 0.65;
        float waveDissolve = smoothstep(erodeThr - 0.26, erodeThr + 0.28, nLow * 0.65 + (1.0 - prog) * 0.45);
        ringWave *= waveDissolve;

        float lifeEnv = smoothstep(0.0, 0.06, prog) * pow(max(1.0 - prog, 1e-4), 1.22);
        float darkMask = sat(ringWave * 0.42 + innerMist * 0.38) * lifeEnv;
        float edgeW = sat(ringWave * 0.86 + innerMist * 0.62) * lifeEnv;
        float bodyW = sat(ringWave * 0.64 + innerMist * 0.45) * lifeEnv;
        float coreW = sat(pow(ringWave, 1.5) * 0.48) * lifeEnv;

        res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
    }
    // =========================================================================
    // MODE 4: Blurred Directional Hit Dust & Armor Deflection Vapor Puff
    // =========================================================================
    else if (mode < 4.5)
    {
        float edgeBox = pow(sat(1.0 - p.x * p.x), 1.65) * pow(sat(1.0 - p.y * p.y), 1.65);
        if (edgeBox <= 0.001)
            discard;

        vec2 q = p - vec2(0.0, -0.26);
        float rq = length(q);
        float aq = atan(q.x, max(q.y, -0.95));
        float expand = 1.0 - pow(max(1.0 - sat(prog / 0.32), 1e-4), 2.05);
        float lifeEnv = smoothstep(0.0, 0.06, prog) * pow(max(1.0 - prog, 1e-4), 1.28);
        float nMist = fbm3(q * 3.1 - vec2(0.0, prog * 1.4), seed + 7.0);

        bool isDeflect = (subParam > 0.5);
        float shieldMist = 0.0;
        if (isDeflect)
        {
            float arcR = mix(0.18, 0.48, expand);
            shieldMist = exp(-pow((rq - arcR) / 0.15, 2.0))
                       * (1.0 - smoothstep(0.55, 1.35, abs(aq)))
                       * (0.72 + 0.38 * nMist);
        }

        // Soft directional vapor cone instead of cartoon stick lines or 4-point cross stars
        float coneSpread = isDeflect ? 1.15 : 1.65;
        float dirKeep = 1.0 - smoothstep(coneSpread * 0.55, coneSpread, abs(aq));
        float reachR = mix(0.24, 0.78, expand) * (0.75 + 0.40 * nMist);
        float puffCone = (1.0 - smoothstep(reachR * 0.35, reachR + 0.18, rq)) * dirKeep * (0.58 + 0.52 * nMist);

        // 4 soft out-of-focus ember motes (wide Gaussian blobs, zero hard segments)
        float softEmbers = 0.0;
        for (int k = 0; k < 4; k++)
        {
            float fk = float(k);
            float h0 = hash11(fk * 8.31 + seed * 4.7);
            float h1 = hash11(fk * 17.19 + seed * 2.1);
            float aS = (h0 - 0.5) * coneSpread;
            vec2 mPos = vec2(sin(aS), cos(aS)) * (0.12 + expand * mix(0.28, 0.62, h1));
            float dM = length(q - mPos);
            softEmbers += exp(-pow(dM / 0.095, 2.0)) * (1.0 - prog * 0.65);
        }

        float hubMist = exp(-dot(q, q) * mix(10.0, 4.5, expand)) * (0.60 + 0.40 * nMist) * (1.0 - prog * 0.7);
        float darkMask = (puffCone * 0.48 + hubMist * 0.42) * lifeEnv;
        float edgeW = sat(puffCone * 0.82 + shieldMist * 0.85 + hubMist * 0.65) * lifeEnv;
        float bodyW = sat(puffCone * 0.55 + shieldMist * 0.72 + softEmbers * 0.52 + hubMist * 0.50) * lifeEnv;
        float coreW = sat(shieldMist * 0.48 + softEmbers * 0.45 + hubMist * 0.42) * lifeEnv;

        res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * edgeBox);
    }
    // =========================================================================
    // MODE 5: Blurred-Edge Volumetric Detonation Cloud & Thermal Dust Haze
    // =========================================================================
    else if (mode < 5.5)
    {
        if (radialSoft <= 0.001)
            discard;

        if (subParam > 1.8)
        {
            // Radially Advected Domain-Warped Turbulent Flame Tongues & Ember Erosion Breakup
            float grow = 0.42 + 0.58 * (1.0 - pow(max(1.0 - prog, 1e-4), 2.1));
            vec2 dp = p / max(grow, 0.25);
            float r = length(dp) + 1e-4;
            float theta = atan(dp.y, dp.x);

            float w1 = fbm3(dp * 3.2 - vec2(prog * 2.4, prog * 2.1), seed) - 0.5;
            float w2 = fbm3(dp * 3.2 + vec2(prog * 1.9 + 3.7, -prog * 2.6 + 1.9), seed + 6.4) - 0.5;
            float angCoord = cos(theta * 5.0 + w1 * 3.4) * 1.35 + sin(theta * 3.0 - w2 * 2.8) * 0.95;
            float radAdv = r * 3.4 - prog * 3.9 + w1 * 0.85;

            float ridgesPolar = ridgedFlameFbm(vec2(radAdv, angCoord + w2 * 1.4), seed + 11.2);
            float ridgesCart = ridgedFlameFbm(dp * 4.4 + vec2(w1 * 1.8 - prog * 1.6, w2 * 1.8 - prog * 2.2), seed + 19.8);
            float flameTurb = 0.58 * ridgesPolar + 0.42 * ridgesCart;

            float warpedR = r - 0.28 * (flameTurb - 0.42) * smoothstep(0.10, 0.85, r);
            float radialFalloff = exp(-1.95 * max(0.0, warpedR) * max(0.0, warpedR));
            float erodeNoise = fbm3(dp * 5.6 + vec2(prog * 1.5, -prog * 1.8), seed + 31.1);
            float cooling = smoothstep(0.32, 1.0, prog) * (0.52 * (1.0 - erodeNoise) + 0.34 * r);

            float lifeEnv = smoothstep(0.0, 0.08, prog) * (1.0 - smoothstep(0.76, 1.0, prog));
            float temp = sat((radialFalloff * (0.42 + 0.88 * flameTurb) - cooling) * lifeEnv);

            float boil = exp(-5.4 * r * r) * (0.75 + 0.35 * ridgesCart) * (1.0 - smoothstep(0.15, 0.62, prog));
            temp = sat(temp + boil * 0.46);

            float fissureLicks = exp(-3.8 * dot(dp, dp)) * ridgesCart;
            float lingering = sat(fissureLicks * 0.72 - 0.18) * smoothstep(0.20, 0.45, prog) * (1.0 - smoothstep(0.68, 0.98, prog));
            temp = max(temp, lingering * 0.78);

            res = composeDragonFlame(temp, radialSoft, masterAlpha, boost);
        }
        else
        {
            float expand = 1.0 - pow(max(1.0 - prog, 1e-4), 1.90);
            float nCloud = fbm3(p * 2.35 - vec2(0.0, prog * 0.95), seed);
            float nInner = fbm3(p * 3.90 + vec2(prog * 0.55, -prog * 0.48), seed + 19.0);

            float cloudRad = mix(0.28, 0.74, expand) + (nCloud - 0.5) * 0.20;
            float softCloud = 1.0 - smoothstep(cloudRad - 0.34, cloudRad + 0.26, rCenter);
            float erodeThr = sat((prog - 0.26) / 0.74) * 0.70;
            float cloudDissolve = smoothstep(erodeThr - 0.28, erodeThr + 0.30, nCloud * 0.68 + (1.0 - rCenter) * 0.50);
            float activeCloud = softCloud * cloudDissolve;

            float warmHeart = activeCloud * exp(-rCenter * rCenter * mix(3.4, 1.85, expand)) * (0.62 + 0.52 * nInner) * (1.0 - smoothstep(0.38, 0.94, prog));
            float outerDustWave = exp(-pow((rCenter - mix(0.16, 0.72, expand)) / 0.22, 2.0)) * (0.65 + 0.35 * nCloud) * (1.0 - pow(prog, 1.35));

            float lifeEnv = smoothstep(0.0, 0.05, prog) * pow(max(1.0 - prog, 1e-4), 1.20);
            float darkMask = sat(activeCloud * (1.0 - warmHeart * 0.68) * 0.62 + outerDustWave * 0.34) * lifeEnv;
            float edgeW = sat(activeCloud * 0.86 + outerDustWave * 0.82) * lifeEnv;
            float bodyW = sat(activeCloud * (0.48 + 0.46 * nCloud) + warmHeart * 0.84 + outerDustWave * 0.52) * lifeEnv;
            float coreW = sat(warmHeart * 0.82) * lifeEnv;

            res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
        }
    }
    // =========================================================================
    // MODE 6: Soft Billowing Atmospheric Dust & Ground Mist Cluster
    // =========================================================================
    else if (mode < 6.5)
    {
        if (radialSoft <= 0.001)
            discard;

        if (subParam > 1.8)
        {
            // Atomized Airborne Silt/Dust Mist Veil (Continuous Wide Gaussian Dispersion, Zero Clumpy Puff Borders)
            float nCoarse = fbm3(p * 2.1 + vec2(prog * 0.45, -prog * 0.55), seed);
            float nFine = fbm3(p * 5.4 - vec2(prog * 0.70, -prog * 0.50), seed + 6.7);
            float mistVeil = exp(-2.15 * dot(p, p)) * (0.52 + 0.30 * nCoarse + 0.24 * nFine);
            float fade = smoothstep(0.0, 0.16, prog) * (1.0 - smoothstep(0.38, 1.0, prog) * 0.88) * radialSoft * masterAlpha;

            vec3 mDark = desatAtmos(edgeCol, 0.32);
            vec3 mHi = desatAtmos(coreCol, 0.28);
            vec3 mistCol = mix(mDark, mHi, sat(0.38 + 0.42 * nCoarse + 0.20 * nFine));
            float finalA = sat(mistVeil * fade * 0.64);
            res = vec4(sat3(mistCol * finalA * clamp(boost, 0.25, 1.05)), finalA);
        }
        else
        {
            float expand = mix(0.45, 1.0, 1.0 - pow(max(1.0 - prog, 1e-4), 1.75));
            float puffAccum = 0.0;
            float puffRimLight = 0.0;

            for (int k = 0; k < 7; k++)
            {
                float fk = float(k);
                float angK = fk * 2.3999632 + seed * 0.21;
                float distK = (k == 0) ? 0.0 : (0.14 + 0.38 * sqrt(fk / 6.5)) * expand;
                vec2 center = vec2(cos(angK), sin(angK)) * distK;
                center.y += prog * (0.05 + 0.03 * sin(fk * 1.7));

                float radK = ((k == 0) ? 0.48 : (0.34 + 0.06 * sin(fk * 2.3))) * (0.80 + 0.25 * expand);
                vec2 dp = rot2d((p - center) / max(radK, 0.08), (age * 0.25 + fk * 1.1) * ((k % 2 == 0) ? 1.0 : -1.0));

                float nEdge = vnoiseQuintic(dp * 1.9 + vec2(fk * 3.1, age * 0.28), seed + fk);
                float dPert = length(dp) - (nEdge - 0.5) * 0.26;
                float m = exp(-pow(max(dPert, 0.0) / 0.72, 2.0)) * (1.0 - smoothstep(0.65, 1.18, dPert));

                puffAccum = 1.0 - (1.0 - puffAccum) * (1.0 - m * 0.42);
                puffRimLight += m * sat(0.50 - dp.x * 0.28 + dp.y * 0.36) * 0.18;
            }

            float billow = fbm3(uv * 2.8 + vec2(0.0, -age * 0.25), seed);
            float mistA = sat(puffAccum * (0.76 + 0.28 * billow));
            float lifeFade = smoothstep(0.0, 0.14, prog) * pow(max(1.0 - prog, 1e-4), 1.30) * radialSoft * masterAlpha;

            vec3 mEdge = desatAtmos(edgeCol, 0.28);
            vec3 mBody = desatAtmos(bodyCol, 0.24);
            vec3 mCore = desatAtmos(coreCol, 0.20);

            vec3 smokeDark = mEdge * 0.34;
            vec3 smokeLit = mix(mBody * 0.58, mCore * 0.72, sat(puffRimLight + billow * 0.30));
            vec3 smokeCol = mix(smokeDark, smokeLit, sat(puffRimLight * 1.15 + billow * 0.38));
            float finalA = sat(mistA * 0.52 * lifeFade);
            float safeBoost = clamp(boost, 0.25, 1.05);
            res = vec4(sat3(smokeCol * finalA * safeBoost), finalA);
        }
    }
    // =========================================================================
    // MODE 7: Soft Atmospheric Heat Haze OR Diffused Ionized Damage Corona
    // =========================================================================
    else if (mode < 7.5)
    {
        if (radialSoft <= 0.001)
            discard;

        if (subParam < 0.5)
        {
            float breath = 0.88 + 0.12 * sin(age * 3.1 + seed);
            float nHaze = fbm3(rot2d(p * 2.2, age * 0.35) + vec2(seed * 0.17), seed);
            float outerHaze = exp(-rCenter * rCenter * 2.8) * (0.65 + 0.35 * nHaze) * breath;
            float innerHaze = exp(-rCenter * rCenter * 6.5) * breath;

            float lifeEnv = smoothstep(0.0, 0.16, prog) * pow(max(1.0 - prog, 1e-4), 1.25);
            float edgeW = sat(outerHaze * 0.78) * lifeEnv;
            float bodyW = sat(outerHaze * 0.52 + innerHaze * 0.58) * lifeEnv;
            float coreW = sat(innerHaze * 0.48) * lifeEnv;
            res = composeSoftAdd(darkCol, outerHaze * 0.16 * lifeEnv, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
        }
        else
        {
            // Soft out-of-focus ionized discharge wisps on critically damaged mechanical joints
            float wispHalo = 0.0;
            float wispCore = 0.0;
            for (int b = 0; b < 3; b++)
            {
                float fb = float(b);
                float baseA = hash11(fb * 13.7 + seed * 5.3) * 6.2831853;
                vec2 prev = vec2(0.0);
                for (int s = 1; s <= 3; s++)
                {
                    float fs = float(s) / 3.0;
                    float jitter = (hash11(fb * 31.1 + float(s) * 7.7 + floor(age * 16.0) + seed) - 0.5) * 0.48;
                    float curA = baseA + jitter;
                    vec2 nxt = vec2(cos(curA), sin(curA)) * (fs * 0.66);
                    float d = segDist(p, prev, nxt);
                    wispCore += exp(-pow(d / 0.042, 2.0)) * 0.55;
                    wispHalo += exp(-pow(d / 0.115, 2.0)) * 0.48;
                    prev = nxt;
                }
            }
            float lifeEnv = smoothstep(0.0, 0.10, prog) * pow(max(1.0 - prog, 1e-4), 1.4);
            res = composeSoftAdd(darkCol, 0.0, edgeCol, sat(wispHalo) * lifeEnv, bodyCol, sat(wispHalo * 0.75) * lifeEnv, coreCol, sat(wispCore * 0.65) * lifeEnv, boost, masterAlpha * radialSoft);
        }
    }
    // =========================================================================
    // MODE 8: Blurred-Edge Ground Tremor Dust & Organic Soil Fracture Shadow
    // =========================================================================
    else if (mode < 8.5)
    {
        if (radialSoft <= 0.001)
            discard;

        float expand = 1.0 - pow(max(1.0 - sat(prog / 0.28), 1e-4), 2.05);
        float coolDown = sat((prog - 0.24) / 0.76);

        // Organic ridged FBM soil turbulence — zero geometric sunburst spokes!
        vec2 pWarp = p * 2.3 + vec2(fbm3(p * 2.8, seed) - 0.5) * 0.45;
        float nSoil = fbm3(pWarp, seed + 5.0);
        float nCrevice = 1.0 - abs(fbm3(pWarp * 1.85 + vec2(1.7, -2.3), seed + 13.0) * 2.0 - 1.0);
        float softCrevice = smoothstep(0.32, 0.88, nCrevice) * (0.55 + 0.45 * nSoil);

        float maxReach = mix(0.28, 0.76, expand) * (0.78 + 0.30 * nSoil);
        // Wide 0.48-unit Gaussian/smoothstep outer feathering
        float reachFeather = 1.0 - smoothstep(maxReach - 0.28, maxReach + 0.20, rCenter);

        float tremorField = softCrevice * reachFeather;
        float centerBasin = exp(-rCenter * rCenter * 4.2) * reachFeather;
        float dustRing = exp(-pow((rCenter - maxReach * 0.78) / 0.22, 2.0)) * (0.55 + 0.45 * nSoil);

        float warmSubtle = (1.0 - coolDown * 0.85) * 0.68;
        float lifeEnv = smoothstep(0.0, 0.07, prog) * pow(max(1.0 - prog, 1e-4), 1.25);

        if (subParam > 1.8)
        {
            float tempCrack = sat((tremorField * 0.88 + centerBasin * 0.42) * warmSubtle * lifeEnv * 1.35);
            res = composeDragonFlame(tempCrack, radialSoft, masterAlpha, boost * 0.92);
        }
        else
        {
            float darkMask = sat(centerBasin * 0.68 + tremorField * 0.62 + dustRing * 0.35) * lifeEnv;
            float edgeW = sat(tremorField * 0.75 + dustRing * 0.72 + centerBasin * 0.55) * lifeEnv;
            float bodyW = sat((tremorField * 0.52 + centerBasin * 0.48) * warmSubtle) * lifeEnv;
            float coreW = sat(pow(tremorField, 1.8) * 0.38 * warmSubtle) * lifeEnv;

            res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
        }
    }
    // =========================================================================
    // MODE 9: Soft Feathered Aerodynamic Ordnance Vapor Trail
    // =========================================================================
    else if (mode < 9.5)
    {
        float edgeBox = pow(sat(1.0 - p.x * p.x), 1.75) * (1.0 - smoothstep(0.86, 0.998, abs(p.y)));
        if (edgeBox <= 0.001)
            discard;

        float s = sat(uv.y);
        if (subParam > 1.8)
        {
            // Travelling Churning Fireball Core + Swept-Back Turbulent Flame Tail
            float fbU = sat(prog * 1.42);
            float fAlong = (s - fbU) * 3.8;
            float normA = (fAlong >= 0.0) ? (fAlong / 0.28) : (fAlong / 1.15);
            float wq1 = fbm3(vec2(fAlong * 3.2 + prog * 7.5, p.x * 2.8), seed + 12.4) - 0.5;
            float wq2 = fbm3(vec2(fAlong * 3.2 + prog * 6.8 + 4.2, p.x * 2.8 - 3.1), seed + 18.9) - 0.5;
            float taperW = clamp(0.42 + 0.14 * normA, 0.14, 0.50);
            float normP = (p.x + wq1 * 0.28 * clamp(-normA, 0.0, 1.4)) / taperW;

            float teardropR = length(vec2(normA, normP));
            float flameRidges = ridgedFlameFbm(vec2(normA * 2.9 + prog * 8.0 + wq1 * 1.5, normP * 2.6 + wq2 * 1.5), seed + 25.3);
            float baseShape = exp(-1.55 * teardropR * teardropR);
            float tailErode = 0.24 * clamp(-normA, 0.0, 1.2) * (1.0 - flameRidges);
            float visFade = (1.0 - smoothstep(0.84, 1.0, prog)) * smoothstep(0.0, 0.05, prog);
            float tempFb = sat(baseShape * (0.48 + 0.80 * flameRidges) - tailErode) * visFade;
            float headCore = exp(-4.2 * (pow(fAlong / 0.20, 2.0) + pow(p.x / 0.28, 2.0))) * visFade;
            tempFb = sat(tempFb + headCore * 0.42);
            res = composeDragonFlame(tempFb, edgeBox, masterAlpha, boost);
        }
        else
        {
            float shootIn = 1.0 - pow(max(1.0 - sat(prog / 0.18), 1e-4), 2.1);
            float fadeTail = sat((prog - 0.16) / 0.84);
            float headFeather = 1.0 - smoothstep(shootIn - 0.16, shootIn + 0.06, s);
            float tailFeather = smoothstep(fadeTail * 0.52 - 0.22, fadeTail * 0.52 + 0.26, s + 0.16);

            float nWake = fbm3(vec2(p.x * 3.2, s * 6.5 - age * 3.5), seed);
            float wobbleX = (nWake - 0.5) * 0.08 * sin(s * 3.14159265);
            float ax = abs(p.x - wobbleX);

            float taper = smoothstep(0.0, 0.16, s) * (1.0 - smoothstep(0.74, 1.0, s) * 0.72);
            float widthScale = clamp(subParam, 0.45, 1.45);
            float baseW = (0.18 + 0.16 * sin(s * 3.14159265)) * taper * widthScale;

            float outerVapor = exp(-pow(ax / max(baseW * 1.35, 0.06), 1.65)) * (0.72 + 0.38 * nWake);
            float bodyVapor = exp(-pow(ax / max(baseW * 0.72, 0.04), 1.80));
            float coreVapor = exp(-pow(ax / max(baseW * 0.32, 0.02), 1.95)) * (1.0 - 0.45 * fadeTail);

            float envBeam = headFeather * tailFeather * smoothstep(0.0, 0.05, prog) * pow(max(1.0 - fadeTail, 1e-4), 1.15);
            float darkSheath = outerVapor * 0.30 * envBeam;
            float edgeW = sat(outerVapor * 0.80) * envBeam;
            float bodyW = sat(bodyVapor * 0.68) * envBeam;
            float coreW = sat(coreVapor * 0.62) * envBeam;

            res = composeSoftAdd(darkCol, darkSheath, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * edgeBox);
        }
    }
    // =========================================================================
    // MODE 10: Blurred Psionic Atmospheric Swirl & Soft Phase-Shield Mist
    // =========================================================================
    else if (mode < 10.5)
    {
        if (radialSoft <= 0.001)
            discard;

        float expand = 1.0 - pow(max(1.0 - sat(prog / 0.26), 1e-4), 1.95);
        float decay = sat((prog - 0.18) / 0.82);
        float lifeEnv = smoothstep(0.0, 0.08, prog) * pow(max(1.0 - decay, 1e-4), 1.25);

        if (subParam < 0.5)
        {
            // Soft blurred psionic vapor swirl (zero 4-point star or hard geometric diamond)
            vec2 pr = rot2d(p, age * 1.65 + seed * 0.5);
            float ang = atan(pr.y, pr.x);
            float nSwirl = fbm3(pr * 2.5 - vec2(age * 0.6), seed);
            float spiral = 0.5 + 0.5 * cos(ang * 3.0 - rCenter * 5.2 - age * 4.2 + (nSwirl - 0.5) * 1.8);
            float softArms = smoothstep(0.22, 0.88, spiral) * smoothstep(0.04, 0.28, rCenter) * (1.0 - smoothstep(0.42, 0.88, rCenter));

            float mistHalo = exp(-pow((rCenter - mix(0.22, 0.62, expand)) / 0.22, 2.0)) * (0.68 + 0.32 * nSwirl);
            float centerWarmth = exp(-rCenter * rCenter * 5.8) * (1.0 - 0.35 * decay);

            float darkMask = (mistHalo * 0.28 + softArms * 0.22) * lifeEnv;
            float edgeW = sat(mistHalo * 0.82 + softArms * 0.72) * lifeEnv;
            float bodyW = sat(softArms * 0.65 + mistHalo * 0.55 + centerWarmth * 0.58) * lifeEnv;
            float coreW = sat(centerWarmth * 0.52 + softArms * 0.32) * lifeEnv;

            res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
        }
        else
        {
            // Soft translucent phase-refraction dome (zero cartoon hex wireframe grid)
            float shieldR = mix(0.24, 0.70, expand);
            float nPhase = fbm3(p * 2.8 + vec2(0.0, -age * 0.8), seed + 9.0);
            float rimMist = exp(-pow((rCenter - shieldR) / 0.20, 2.0)) * (0.70 + 0.30 * nPhase);
            float domeVeil = (1.0 - smoothstep(shieldR * 0.35, shieldR + 0.22, rCenter)) * (0.28 + 0.24 * nPhase);

            float darkMask = rimMist * 0.24 * lifeEnv;
            float edgeW = sat(rimMist * 0.82 + domeVeil * 0.75) * lifeEnv;
            float bodyW = sat(rimMist * 0.62 + domeVeil * 0.45) * lifeEnv;
            float coreW = sat(pow(rimMist, 1.6) * 0.45) * lifeEnv;

            res = composeSoftAdd(darkCol, darkMask, edgeCol, edgeW, bodyCol, bodyW, coreCol, coreW, boost, masterAlpha * radialSoft);
        }
    }
    // =========================================================================
    // MODE 11: Pure-Black Soft Elliptical Drop Shadow
    // =========================================================================
    else
    {
        if (radialSoft <= 0.001)
            discard;
        float shadowFalloff = pow(sat(1.0 - rCenter * rCenter), 1.85);
        float shadowA = sat(shadowFalloff * masterAlpha);
        res = vec4(0.0, 0.0, 0.0, shadowA);
    }

    res.rgb *= keepTex;
    if (res.a <= 0.001 && (res.r + res.g + res.b) <= 0.001)
        discard;

    outColor = clamp(res, 0.0, 1.0);
}

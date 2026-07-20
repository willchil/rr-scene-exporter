Shader "Rec Room Scene Exporter/Watch Hands"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}

        _Metallic ("Metallic", Range(0, 1)) = 0
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _MetallicGlossMap ("Metallic (R) Smoothness (A)", 2D) = "white" {}

        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0, 2)) = 1

        _OcclusionMap ("Occlusion (G)", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1

        _EmissionMap ("Emission", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0, 0, 0, 1)

        [HDR] _HourColor ("Hour Hand Color", Color) = (0.9, 0.9, 0.9, 1)
        [HDR] _MinuteColor ("Minute Hand Color", Color) = (0.9, 0.9, 0.9, 1)
        [HDR] _SecondColor ("Second Hand Color", Color) = (0.9, 0.1, 0.08, 1)
        _HandEmission ("Hand Emission", Range(0, 10)) = 1
        _HourLength ("Hour Hand Length", Range(0.05, 0.48)) = 0.25
        _MinuteLength ("Minute Hand Length", Range(0.05, 0.48)) = 0.38
        _SecondLength ("Second Hand Length", Range(0.05, 0.48)) = 0.43
        _HourWidth ("Hour Hand Width", Range(0.002, 0.08)) = 0.026
        _MinuteWidth ("Minute Hand Width", Range(0.002, 0.08)) = 0.017
        _SecondWidth ("Second Hand Width", Range(0.001, 0.05)) = 0.007
        _CenterRadius ("Center Pin Radius", Range(0.002, 0.08)) = 0.025
        _TailLength ("Hand Tail Length", Range(0, 0.12)) = 0.035
        _ClockRotation ("Clock Rotation", Range(-180, 180)) = 0
        _SecondsOffset ("Seconds Offset", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 300
        Cull Back
        ZWrite On

        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #pragma multi_compile_instancing

        #include "UnityCG.cginc"
        #include "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc"

        sampler2D _MainTex;
        sampler2D _MetallicGlossMap;
        sampler2D _BumpMap;
        sampler2D _OcclusionMap;
        sampler2D _EmissionMap;
        float4 _MainTex_ST;
        float4 _MetallicGlossMap_ST;
        float4 _BumpMap_ST;
        float4 _OcclusionMap_ST;
        float4 _EmissionMap_ST;

        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        half _BumpScale;
        half _OcclusionStrength;
        fixed4 _EmissionColor;

        fixed4 _HourColor;
        fixed4 _MinuteColor;
        fixed4 _SecondColor;
        half _HandEmission;
        float _HourLength;
        float _MinuteLength;
        float _SecondLength;
        float _HourWidth;
        float _MinuteWidth;
        float _SecondWidth;
        float _CenterRadius;
        float _TailLength;
        float _ClockRotation;
        float _SecondsOffset;

        struct Input
        {
            float2 baseUV;
            float2 clockUV;
        };

        void vert(inout appdata_full vertex, out Input output)
        {
            UNITY_INITIALIZE_OUTPUT(Input, output);
            output.baseUV = vertex.texcoord.xy;
            output.clockUV = vertex.texcoord1.xy;
        }

        float HandDistance(float2 clockPosition, float2 direction, float handLength, float tail)
        {
            float alongHand = clamp(dot(clockPosition, direction), -tail, handLength);
            return length(clockPosition - direction * alongHand);
        }

        float HandMask(float distanceToHand, float halfWidth)
        {
            float antialiasing = max(fwidth(distanceToHand), 0.0001);
            return 1.0 - smoothstep(
                halfWidth - antialiasing,
                halfWidth + antialiasing,
                distanceToHand
            );
        }

        void surf(Input input, inout SurfaceOutputStandard output)
        {
            fixed4 albedo = tex2D(_MainTex, TRANSFORM_TEX(input.baseUV, _MainTex)) * _Color;
            fixed4 metallicGloss = tex2D(
                _MetallicGlossMap,
                TRANSFORM_TEX(input.baseUV, _MetallicGlossMap)
            );
            fixed occlusion = tex2D(
                _OcclusionMap,
                TRANSFORM_TEX(input.baseUV, _OcclusionMap)
            ).g;
            fixed3 baseEmission = tex2D(
                _EmissionMap,
                TRANSFORM_TEX(input.baseUV, _EmissionMap)
            ).rgb * _EmissionColor.rgb;

            const float TAU = 6.28318530718;
            float localSeconds =
                AudioLinkDecodeDataAsSeconds(ALPASS_GENERALVU_LOCAL_TIME) + _SecondsOffset;
            localSeconds = fmod(localSeconds, 86400.0);
            if (localSeconds < 0.0)
            {
                localSeconds += 86400.0;
            }
            float seconds = fmod(localSeconds, 60.0);
            float minutes = fmod(floor(localSeconds / 60.0), 60.0);
            float hours = fmod(floor(localSeconds / 3600.0), 12.0);
            float rotation = radians(_ClockRotation + 180.0);

            float hourAngle = TAU * (hours + minutes / 60.0 + seconds / 3600.0) / 12.0 + rotation;
            float minuteAngle = TAU * (minutes + seconds / 60.0) / 60.0 + rotation;
            float secondAngle = TAU * seconds / 60.0 + rotation;

            float2 hourDirection = float2(sin(hourAngle), cos(hourAngle));
            float2 minuteDirection = float2(sin(minuteAngle), cos(minuteAngle));
            float2 secondDirection = float2(sin(secondAngle), cos(secondAngle));
            float2 clockPosition = input.clockUV - 0.5;

            float audioLinkAvailable = AudioLinkIsAvailable() ? 1.0 : 0.0;
            float insideFace = audioLinkAvailable *
                step(0.0, input.clockUV.x) * step(input.clockUV.x, 1.0) *
                step(0.0, input.clockUV.y) * step(input.clockUV.y, 1.0);
            float hourMask = HandMask(
                HandDistance(clockPosition, hourDirection, _HourLength, _TailLength),
                _HourWidth * 0.5
            ) * insideFace;
            float minuteMask = HandMask(
                HandDistance(clockPosition, minuteDirection, _MinuteLength, _TailLength),
                _MinuteWidth * 0.5
            ) * insideFace;
            float secondMask = HandMask(
                HandDistance(clockPosition, secondDirection, _SecondLength, _TailLength),
                _SecondWidth * 0.5
            ) * insideFace;
            float centerMask = HandMask(length(clockPosition), _CenterRadius) * insideFace;

            fixed3 handColor = _HourColor.rgb;
            handColor = lerp(handColor, _MinuteColor.rgb, minuteMask);
            handColor = lerp(handColor, _SecondColor.rgb, max(secondMask, centerMask));
            float handMask = max(max(hourMask, minuteMask), max(secondMask, centerMask));

            output.Albedo = lerp(albedo.rgb, handColor, handMask);
            output.Normal = UnpackScaleNormal(
                tex2D(_BumpMap, TRANSFORM_TEX(input.baseUV, _BumpMap)),
                _BumpScale
            );
            output.Metallic = metallicGloss.r * _Metallic;
            output.Smoothness = metallicGloss.a * _Glossiness;
            output.Occlusion = lerp(1.0, occlusion, _OcclusionStrength);
            output.Emission =
                baseEmission * (1.0 - handMask) +
                handColor * handMask * _HandEmission;
            output.Alpha = albedo.a;
        }
        ENDCG
    }

    FallBack "Standard"
}
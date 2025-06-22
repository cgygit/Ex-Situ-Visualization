Shader "DR_Shader/BlinnPhong_Original" {
  Properties {
    _MainTex ("Texture", 2D) = "white" {}
    _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
    _Shininess ("Shininess", Float) = 20
  }
  SubShader {

    Pass {
      Tags { "LightMode" = "ForwardBase" }

      CGPROGRAM
      #include "UnityCG.cginc"
      #pragma vertex vert
      #pragma fragment frag

      // Properties
      uniform sampler2D _MainTex;
      uniform fixed4 _LightColor0;
      uniform fixed4 _SpecularColor;
      uniform half   _Shininess;

            // Duplicated Reality
      half4x4 _ROI_Inversed;
      half4x4 _Dupl_Inversed;
      half4x4 _Roi2Dupl;

      // Vertex Input
      struct appdata {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv     : TEXCOORD0;

        // Single Instanced Pass Rendering
		UNITY_VERTEX_INPUT_INSTANCE_ID
      };

      // Vertex to Fragment
      struct v2f {
        float4 pos      : SV_POSITION;
        float3 normal   : NORMAL;
        float2 uv       : TEXCOORD0;
        float4 posWorld : TEXCOORD1;

		// Single Instanced Pass Rendering
		UNITY_VERTEX_OUTPUT_STEREO
      };

      //------------------------------------------------------------------------
      // Vertex Shader
      //------------------------------------------------------------------------
      v2f vert(appdata v) {
        v2f o;

        // Single Instanced Pass Rendering
		UNITY_SETUP_INSTANCE_ID(v);
		UNITY_INITIALIZE_OUTPUT(v2f, o);
		UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

        o.pos      = UnityObjectToClipPos(v.vertex);
        o.normal   = normalize(mul(v.normal, unity_WorldToObject).xyz);
        o.uv       = v.uv;
        o.posWorld = mul(unity_ObjectToWorld, v.vertex);

        return o;
      }

      //------------------------------------------------------------------------
      // Fragment Shader
      //------------------------------------------------------------------------
      fixed4 frag(v2f i) : SV_Target {

        // Vector
        half3 normal   = normalize(i.normal);
        half3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
        half3 viewDir  = normalize(_WorldSpaceCameraPos.xyz - i.posWorld);
        half3 halfDir  = normalize(lightDir + viewDir);

        // Dot
        half NdotL = saturate(dot(normal, lightDir));
        half NdotH = saturate(dot(normal, halfDir));

        // Color
        fixed3 ambient  = UNITY_LIGHTMODEL_AMBIENT.rgb;
        fixed3 diffuse  = _LightColor0.rgb * tex2D(_MainTex, i.uv).rgb * NdotL;
        fixed3 specular = _LightColor0.rgb * _SpecularColor.rgb * pow(NdotH, _Shininess);
        fixed4 color = fixed4(ambient + diffuse + specular, 1.0);

        return color;
      }
      ENDCG
    }
  }
}